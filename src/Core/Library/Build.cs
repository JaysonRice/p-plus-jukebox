using System.Text.Json;
using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Library;

/// <summary>Where a tracklist song's audio comes from.</summary>
public enum SongSource
{
    /// <summary>Brawl's original song, read from the game disc (no file needed).</summary>
    Disc,
    /// <summary>A song in the app's library.</summary>
    Library,
    /// <summary>The tracklist points at a song the library doesn't have: the stage would play silence.</summary>
    Missing,
}

public sealed record SongLocation(SongSource Source, string? FilePath);

/// <summary>One stage tracklist in a build, as the user sees it.</summary>
public sealed record StageInfo(string Name, string DisplayName, bool KnownToProjectPlus, bool ChangedFromDefault);

/// <summary>Saved per build in build.json.</summary>
public sealed class BuildInfo
{
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? CopiedFrom { get; set; }
    /// <summary>Project+ folder this build was last synced to, so Sync can suggest it again.</summary>
    public string? LastSyncFolder { get; set; }
}

/// <summary>
/// A named music setup: one tracklist per stage, pointing at songs in the shared library.
/// Stored as <c>builds\&lt;id&gt;\build.json</c> + <c>builds\&lt;id&gt;\tracklist\*.tlst</c>.
/// </summary>
public sealed class Build
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AppLibrary Library { get; }
    public string Id { get; }
    public string Dir { get; }
    public string TracklistDir => Path.Combine(Dir, "tracklist");
    /// <summary>This build's copy of the 8 stage list presets (Switch00.rss–Switch07.rss).</summary>
    public string StageListDir => Path.Combine(Dir, "stagelists");
    public const int PresetCount = 8;
    public string StageListPath(int preset) => Path.Combine(StageListDir, $"Switch{preset:00}.rss");
    /// <summary>Which presets this build has (normally all 8; none for a library made before stage lists existed and with no source to copy from).</summary>
    public IReadOnlyList<int> StageListPresets() => Enumerable.Range(0, PresetCount).Where(i => File.Exists(StageListPath(i))).ToList();

    public global::PPlusTracklistEditor.Core.Stages.StageListFile OpenStageList(int preset) => global::PPlusTracklistEditor.Core.Stages.StageListFile.Load(StageListPath(preset));

    /// <summary>Saves a preset, keeping the previous version in the backups folder.</summary>
    public void SaveStageList(int preset, global::PPlusTracklistEditor.Core.Stages.StageListFile list)
    {
        string path = StageListPath(preset);
        if (File.Exists(path))
        {
            string backups = Path.Combine(Library.BackupDir, Id);
            Directory.CreateDirectory(backups);
            File.Copy(path, Path.Combine(backups, $"Switch{preset:00}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.rss"));
        }
        list.Save(path);
    }
    public BuildInfo Info { get; private set; }
    public string Name => Info.Name;
    public bool IsDefault => Id == Library.Info.DefaultBuildId;

    internal Build(AppLibrary library, string id)
    {
        Library = library;
        Id = id;
        Dir = Path.Combine(library.BuildsDir, id);
        string infoFile = Path.Combine(Dir, "build.json");
        Info = File.Exists(infoFile)
            ? JsonSerializer.Deserialize<BuildInfo>(File.ReadAllText(infoFile), Json) ?? new BuildInfo { Name = id }
            : new BuildInfo { Name = id };
    }

    internal void SaveInfo()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "build.json"), JsonSerializer.Serialize(Info, Json));
    }

    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new MusicLibraryException("Please give the build a name.");
        Info.Name = newName.Trim();
        SaveInfo();
    }

    internal void RememberSyncFolder(string folder)
    {
        Info.LastSyncFolder = folder;
        SaveInfo();
    }

    public string TracklistPath(string stageName) => Path.Combine(TracklistDir, stageName + ".tlst");

    public IEnumerable<string> TracklistNames() =>
        Directory.Exists(TracklistDir)
            ? Directory.EnumerateFiles(TracklistDir, "*.tlst").Select(f => Path.GetFileNameWithoutExtension(f))
            : [];

    public IReadOnlyList<StageInfo> Stages()
    {
        Build? defaults = IsDefault ? null : Library.DefaultBuild;
        var known = Library.KnownStages;
        return TracklistNames()
            .Select(name => new StageInfo(
                name,
                StageNames.Pretty(name),
                KnownToProjectPlus: known.Count == 0 || known.Contains(name),
                ChangedFromDefault: defaults is not null && !SameFile(TracklistPath(name), defaults.TracklistPath(name))))
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SameFile(string a, string b) =>
        File.Exists(b) && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    public StageEditSession OpenStage(string stageName)
    {
        string path = TracklistPath(stageName);
        if (!File.Exists(path)) throw new MusicLibraryException($"This build has no tracklist called \"{stageName}\".");
        return new StageEditSession(this, stageName, File.ReadAllBytes(path));
    }

    public SongLocation Locate(TlstEntry entry)
    {
        if (entry.Path is null) return new SongLocation(SongSource.Disc, null);
        if (Library.Songs.Has(entry.Path)) return new SongLocation(SongSource.Library, Library.Songs.FileFor(entry.Path));
        // "Z01", "H06"…: a Brawl disc song by its disc file name. The game loads it from the disc when the SD card doesn't have it.
        if (DiscSongs.IsDiscFileName(entry.Path)) return new SongLocation(SongSource.Disc, null);
        return new SongLocation(SongSource.Missing, null);
    }

    /// <summary>Every song path this build's tracklists use (including disc file names like "Z01").</summary>
    public HashSet<string> SongPathsUsed()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in TracklistNames())
            foreach (TlstEntry e in TlstFile.Load(TracklistPath(name)).Entries)
                if (e.Path is not null) used.Add(SongStore.Normalize(e.Path));
        return used;
    }

    /// <summary>Title to show: the tracklist's own title, else the disc song's name, else the file name.</summary>
    public static string DisplayTitle(TlstEntry entry) =>
        entry.Title
        ?? (entry.Path is null ? DiscSongs.Find(entry.SongId)?.Name : null)
        ?? (entry.Path is not null ? entry.Path.Split('/')[^1] : null)
        ?? "Unnamed song";

    public override string ToString() => Name;
}
