using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Sync;

/// <summary>What a sync will do, worked out before anything is written so the user can review it.</summary>
public sealed class SyncPlan
{
    public required Build Build { get; init; }
    public required ProjectPlusFolder Target { get; init; }

    /// <summary>Tracklists that will be written (new or different on the target).</summary>
    public required IReadOnlyList<string> TracklistsToWrite { get; init; }
    public required int TracklistsUnchanged { get; init; }
    /// <summary>Tracklists on the target that this build doesn't have. Left alone (e.g. a stage from a newer P+).</summary>
    public required IReadOnlyList<string> TracklistsLeftAlone { get; init; }

    /// <summary>Song paths copied from the library (missing or different on the target).</summary>
    public required IReadOnlyList<string> SongsToCopy { get; init; }
    /// <summary>Song paths on the target that no tracklist uses anymore. Deleted from the target only, never from the library.</summary>
    public required IReadOnlyList<string> SongsToDelete { get; init; }
    /// <summary>Songs a tracklist uses that are neither in the library nor on the target: those stages would play silence.</summary>
    public required IReadOnlyList<string> MissingSongs { get; init; }

    public bool HasChanges => TracklistsToWrite.Count + SongsToCopy.Count + SongsToDelete.Count > 0;

    /// <summary>A short plain-language description for the confirm dialog.</summary>
    public string Summary()
    {
        if (!HasChanges) return $"\"{Build.Name}\" is already up to date in {Target.SoundDir}. Nothing to do.";
        var lines = new List<string> { $"Sync \"{Build.Name}\" to {Target.SoundDir}:" };
        if (TracklistsToWrite.Count > 0) lines.Add($"• Update {Plural(TracklistsToWrite.Count, "stage song list")}");
        if (SongsToCopy.Count > 0) lines.Add($"• Copy {Plural(SongsToCopy.Count, "song")}");
        if (SongsToDelete.Count > 0) lines.Add($"• Remove {Plural(SongsToDelete.Count, "song")} no stage uses anymore (they stay in your library)");
        if (MissingSongs.Count > 0) lines.Add($"• Warning: {Plural(MissingSongs.Count, "song")} can't be found and will be silent");
        lines.Add("Nothing outside the music folder is changed.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

/// <summary>
/// Puts a build's music on a Project+ folder (SD card or any copy). Only <c>pf\sound</c> is touched:
/// songs are copied first, then tracklists are written, then songs no tracklist uses are deleted.
/// "In use" = named by any tracklist on the target, plus the "_b" (hurry-up) version of each such song.
/// </summary>
public static class BuildSync
{
    /// <summary>FAT32 stores times to 2 seconds, so "same time" allows that much difference.</summary>
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    public static SyncPlan Plan(Build build, ProjectPlusFolder target)
    {
        AppLibrary lib = build.Library;
        if (AppLibrary.IsInside(target.Root, lib.Root) || AppLibrary.IsInside(lib.Root, target.Root))
            throw new MusicLibraryException("You picked the app's own library folder. Pick your Project+ folder instead.");

        var buildTracklists = build.TracklistNames().ToList();
        var buildSet = buildTracklists.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetTracklists = Directory.Exists(target.TracklistDir) ? target.TracklistNames().ToList() : [];

        var toWrite = new List<string>();
        int unchanged = 0;
        foreach (string name in buildTracklists)
        {
            string dest = Path.Combine(target.TracklistDir, name + ".tlst");
            if (File.Exists(dest) && File.ReadAllBytes(dest).AsSpan().SequenceEqual(File.ReadAllBytes(build.TracklistPath(name)))) unchanged++;
            else toWrite.Add(name);
        }
        var leftAlone = targetTracklists.Where(n => !buildSet.Contains(n)).ToList();

        // Every song any tracklist on the target will use after the sync.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in buildTracklists) AddPaths(used, build.TracklistPath(name));
        foreach (string name in leftAlone) AddPaths(used, Path.Combine(target.TracklistDir, name + ".tlst"));

        SongStore targetSongs = target.Songs;
        var toCopy = new List<string>();
        var missing = new List<string>();
        foreach (string song in used.Order(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string p in new[] { song, song + SongStore.HurryUpSuffix })
            {
                bool inLibrary = lib.Songs.Has(p);
                if (inLibrary && !SameFile(lib.Songs.FileFor(p), targetSongs.FileFor(p))) toCopy.Add(p);
                else if (!inLibrary && p == song && !targetSongs.Has(p) && !DiscSongs.IsDiscFileName(p)) missing.Add(p);
            }
        }

        var toDelete = targetSongs.All()
            .Where(p => !used.Contains(p) && !(SongStore.HurryUpBase(p) is { } b && used.Contains(b)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SyncPlan
        {
            Build = build,
            Target = target,
            TracklistsToWrite = toWrite,
            TracklistsUnchanged = unchanged,
            TracklistsLeftAlone = leftAlone,
            SongsToCopy = toCopy,
            SongsToDelete = toDelete,
            MissingSongs = missing,
        };
    }

    /// <summary>Carries out a plan. Reports progress as (done, total) steps.</summary>
    public static void Execute(SyncPlan plan, IProgress<(int Done, int Total)>? progress = null)
    {
        AppLibrary lib = plan.Build.Library;
        SongStore targetSongs = plan.Target.Songs;
        int total = plan.SongsToCopy.Count + plan.TracklistsToWrite.Count + plan.SongsToDelete.Count, done = 0;

        // 1. Songs first, so no tracklist ever points at a song that isn't there yet.
        foreach (string song in plan.SongsToCopy)
        {
            string dest = targetSongs.FileFor(song);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(lib.Songs.FileFor(song), dest, overwrite: true);
            progress?.Report((++done, total));
        }

        // 2. Tracklists (temp file + replace, so a pulled card never has half a tracklist).
        Directory.CreateDirectory(plan.Target.TracklistDir);
        foreach (string name in plan.TracklistsToWrite)
        {
            string dest = Path.Combine(plan.Target.TracklistDir, name + ".tlst");
            File.Copy(plan.Build.TracklistPath(name), dest + ".tmp", overwrite: true);
            File.Move(dest + ".tmp", dest, overwrite: true);
            progress?.Report((++done, total));
        }

        // 3. Songs nothing uses anymore (target only; the library keeps them).
        foreach (string song in plan.SongsToDelete)
        {
            File.Delete(targetSongs.FileFor(song));
            progress?.Report((++done, total));
        }
        AppLibrary.DeleteEmptyFolders(targetSongs.Dir);

        plan.Build.RememberSyncFolder(plan.Target.Root);
    }

    private static void AddPaths(HashSet<string> used, string tlstPath)
    {
        foreach (TlstEntry e in TlstFile.Load(tlstPath).Entries)
            if (e.Path is not null) used.Add(SongStore.Normalize(e.Path));
    }

    private static bool SameFile(string source, string dest)
    {
        var d = new FileInfo(dest);
        if (!d.Exists) return false;
        var s = new FileInfo(source);
        return s.Length == d.Length && (s.LastWriteTimeUtc - d.LastWriteTimeUtc).Duration() <= TimeTolerance;
    }
}
