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

    /// <summary>Song paths copied from the library (missing on the target, or a different size).</summary>
    public required IReadOnlyList<string> SongsToCopy { get; init; }
    /// <summary>
    /// Songs on the target with the same size but a different date (e.g. put there by another tool). Compared byte for
    /// byte while syncing: copied only if different, otherwise just given the library's date so the next sync skips them.
    /// Reading a card is much faster than writing it, so this avoids rewriting a card full of identical songs.
    /// </summary>
    public required IReadOnlyList<string> SongsToCheck { get; init; }
    /// <summary>Total size of <see cref="SongsToCopy"/>, for the "about how long" estimate.</summary>
    public required long BytesToCopy { get; init; }
    /// <summary>Stage list presets (0 = Switch00) that differ on the target and will be written.</summary>
    public required IReadOnlyList<int> StageListsToWrite { get; init; }
    /// <summary>Song paths on the target that no tracklist uses anymore. Deleted from the target only, never from the library.</summary>
    public required IReadOnlyList<string> SongsToDelete { get; init; }
    /// <summary>Songs a tracklist uses that are neither in the library nor on the target: those stages would play silence.</summary>
    public required IReadOnlyList<string> MissingSongs { get; init; }

    public bool HasChanges => TracklistsToWrite.Count + SongsToCopy.Count + SongsToCheck.Count + SongsToDelete.Count + StageListsToWrite.Count > 0;

    /// <summary>A short plain-language description for the confirm dialog.</summary>
    public string Summary()
    {
        if (!HasChanges) return $"\"{Build.Name}\" is already up to date in {Target.Root}. Nothing to do.";
        var lines = new List<string> { $"Sync \"{Build.Name}\" to {Target.Root}:" };
        if (TracklistsToWrite.Count > 0) lines.Add($"• Update {Plural(TracklistsToWrite.Count, "stage song list")}");
        if (SongsToCopy.Count > 0) lines.Add($"• Copy {Plural(SongsToCopy.Count, "song")} ({SizeText(BytesToCopy)}, about {TimeText(BytesToCopy)} to an SD card)");
        if (SongsToCheck.Count > 0) lines.Add($"• Check {Plural(SongsToCheck.Count, "song")} that look already there (only copied if different)");
        if (StageListsToWrite.Count > 0) lines.Add($"• Update {Plural(StageListsToWrite.Count, "stage list preset")} ({string.Join(", ", StageListsToWrite.Select(i => $"Switch{i:00}"))})");
        if (SongsToDelete.Count > 0) lines.Add($"• Remove {Plural(SongsToDelete.Count, "song")} no stage uses anymore (they stay in your library)");
        if (MissingSongs.Count > 0) lines.Add($"• Warning: {Plural(MissingSongs.Count, "song")} can't be found and will be silent");
        lines.Add("Only the music and stage list folders are changed.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string SizeText(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{Math.Max(1, bytes >> 20)} MB";

    /// <summary>Rough time at ~1.5 MB/s, a typical write speed for small files on an SD card in a USB reader.</summary>
    private static string TimeText(long bytes)
    {
        double minutes = bytes / (1.5 * (1 << 20)) / 60;
        return minutes < 1 ? "less than a minute" : minutes < 1.5 ? "1 minute" : $"{Math.Round(minutes)} minutes";
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

/// <summary>
/// Puts a build's music and stage lists on a Project+ folder (SD card or any copy). Only <c>pf\sound</c> and the stage
/// list presets (<c>pf\stage\switch\SwitchNN.rss</c>) are touched:
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
        var toCheck = new List<string>();
        long bytesToCopy = 0;
        var missing = new List<string>();
        foreach (string song in used.Order(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string p in new[] { song, song + SongStore.HurryUpSuffix })
            {
                bool inLibrary = lib.Songs.Has(p);
                if (inLibrary)
                {
                    switch (Compare(lib.Songs.FileFor(p), targetSongs.FileFor(p)))
                    {
                        case FileMatch.Different: toCopy.Add(p); bytesToCopy += new FileInfo(lib.Songs.FileFor(p)).Length; break;
                        case FileMatch.SameSizeOtherDate: toCheck.Add(p); break;
                    }
                }
                else if (!inLibrary && p == song && !targetSongs.Has(p) && !DiscSongs.IsDiscFileName(p)) missing.Add(p);
            }
        }

        var toDelete = targetSongs.All()
            .Where(p => !used.Contains(p) && !(SongStore.HurryUpBase(p) is { } b && used.Contains(b)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stageLists = build.StageListPresets()
            .Where(i => !File.Exists(target.StageListPath(i)) || !File.ReadAllBytes(target.StageListPath(i)).AsSpan().SequenceEqual(File.ReadAllBytes(build.StageListPath(i))))
            .ToList();

        return new SyncPlan
        {
            Build = build,
            Target = target,
            TracklistsToWrite = toWrite,
            TracklistsUnchanged = unchanged,
            TracklistsLeftAlone = leftAlone,
            SongsToCopy = toCopy,
            SongsToCheck = toCheck,
            BytesToCopy = bytesToCopy,
            StageListsToWrite = stageLists,
            SongsToDelete = toDelete,
            MissingSongs = missing,
        };
    }

    /// <summary>Carries out a plan. Reports progress as (done, total) steps.</summary>
    public static void Execute(SyncPlan plan, IProgress<(int Done, int Total)>? progress = null)
    {
        AppLibrary lib = plan.Build.Library;
        SongStore targetSongs = plan.Target.Songs;
        int total = plan.SongsToCopy.Count + plan.SongsToCheck.Count + plan.TracklistsToWrite.Count + plan.SongsToDelete.Count + plan.StageListsToWrite.Count, done = 0;

        // 1. Songs first, so no tracklist ever points at a song that isn't there yet.
        foreach (string song in plan.SongsToCopy)
        {
            string dest = targetSongs.FileFor(song);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(lib.Songs.FileFor(song), dest, overwrite: true);
            progress?.Report((++done, total));
        }
        foreach (string song in plan.SongsToCheck)
        {
            string source = lib.Songs.FileFor(song), dest = targetSongs.FileFor(song);
            if (SongStore.SameContent(source, dest)) File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
            else File.Copy(source, dest, overwrite: true);
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

        // Stage list presets (temp file + replace too).
        foreach (int preset in plan.StageListsToWrite)
        {
            string dest = plan.Target.StageListPath(preset);
            Directory.CreateDirectory(plan.Target.StageListDir);
            File.Copy(plan.Build.StageListPath(preset), dest + ".tmp", overwrite: true);
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

    private enum FileMatch { Same, SameSizeOtherDate, Different }

    private static FileMatch Compare(string source, string dest)
    {
        var d = new FileInfo(dest);
        if (!d.Exists) return FileMatch.Different;
        var s = new FileInfo(source);
        if (s.Length != d.Length) return FileMatch.Different;
        return (s.LastWriteTimeUtc - d.LastWriteTimeUtc).Duration() <= TimeTolerance ? FileMatch.Same : FileMatch.SameSizeOtherDate;
    }
}
