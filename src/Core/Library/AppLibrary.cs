using System.Text.Json;
using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Library;

/// <summary>Saved in library.json.</summary>
public sealed class LibraryInfo
{
    public int FormatVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public string DefaultBuildId { get; set; } = "default";
    /// <summary>Tracklist names the user's Project+ had when imported. Used to warn about names the game won't load.</summary>
    public List<string> KnownStages { get; set; } = [];
    public string? ProjectPlusFolder { get; set; }
    /// <summary>Song paths written with '\' that the first-run import converted to '/'. Shown to the user once.</summary>
    public int FixedPathsOnImport { get; set; }
}

/// <summary>Result of importing a music folder as a build.</summary>
public sealed record ImportResult(Build Build, int FixedPaths, int RenamedSongs);

/// <summary>
/// The app's library folder: one shared copy of every song (<c>songs\</c>) and any number of builds (<c>builds\</c>).
/// Created on first run by importing the user's Project+ folder.
/// </summary>
public sealed class AppLibrary
{
    public const string DefaultBuildName = "Default";

    public string Root { get; }
    public string SongsDir => Path.Combine(Root, "songs");
    public string BuildsDir => Path.Combine(Root, "builds");
    public string BackupDir => Path.Combine(Root, "backups");
    private string InfoFile => Path.Combine(Root, "library.json");

    public SongStore Songs { get; }
    public LibraryInfo Info { get; private set; }
    public HashSet<string> KnownStages { get; private set; }

    private AppLibrary(string root, LibraryInfo info)
    {
        Root = Path.GetFullPath(root);
        Songs = new SongStore(SongsDir);
        Info = info;
        KnownStages = new HashSet<string>(info.KnownStages, StringComparer.OrdinalIgnoreCase);
    }

    // ---------- Location ----------

    /// <summary>
    /// Where the library lives: <c>Library\</c> next to the app, so they travel together. If that folder can't be
    /// written (e.g. the app is in Program Files), falls back to LocalAppData. <paramref name="usedFallback"/> says which.
    /// </summary>
    public static string DefaultRoot(out bool usedFallback, string? appDir = null, Func<string, bool>? canWrite = null)
    {
        canWrite ??= CanWriteTo;
        string beside = Path.Combine(appDir ?? AppContext.BaseDirectory, "Library");
        if (canWrite(beside)) { usedFallback = false; return beside; }
        usedFallback = true;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PPlusTracklistEditor", "Library");
    }

    private static bool CanWriteTo(string dir)
    {
        // Leaves no trace: a folder made only for the test is removed again (setup relies on "did the folder exist before?").
        bool created = !Directory.Exists(dir);
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            if (created) Directory.Delete(dir);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool Exists(string root) => File.Exists(Path.Combine(root, "library.json"));

    public static AppLibrary Open(string root)
    {
        string infoFile = Path.Combine(root, "library.json");
        if (!File.Exists(infoFile)) throw new MusicLibraryException($"There's no song library in \"{root}\" yet.");
        var info = JsonSerializer.Deserialize<LibraryInfo>(File.ReadAllText(infoFile), Build.Json)
                   ?? throw new MusicLibraryException("The library's settings file is damaged.");
        return new AppLibrary(root, info);
    }

    // ---------- First run ----------

    /// <summary>
    /// First run: copies every song and tracklist from the user's Project+ into a new library,
    /// with the tracklists as the "Default" build. Reports progress as "copied/total".
    /// </summary>
    public static AppLibrary CreateFromProjectPlus(string root, ProjectPlusFolder pplus, IProgress<(int Done, int Total)>? progress = null)
    {
        if (Exists(root)) throw new MusicLibraryException("A song library already exists here.");
        if (IsInside(Path.GetFullPath(root), pplus.Root) || IsInside(pplus.Root, Path.GetFullPath(root)))
            throw new MusicLibraryException("The library can't be inside your Project+ folder (or the other way round).");

        var info = new LibraryInfo
        {
            CreatedAt = DateTime.Now,
            KnownStages = pplus.TracklistNames().Order(StringComparer.OrdinalIgnoreCase).ToList(),
            ProjectPlusFolder = pplus.Root,
        };
        var lib = new AppLibrary(root, info);
        Directory.CreateDirectory(lib.SongsDir);

        var source = pplus.Songs;
        var songs = source.All().ToList();
        for (int i = 0; i < songs.Count; i++)
        {
            if (!lib.Songs.Has(songs[i])) lib.Songs.Add(source.FileFor(songs[i]), songs[i]);
            progress?.Report((i + 1, songs.Count));
        }

        Build defaults = lib.CreateBuildFolder(DefaultBuildName, copiedFrom: null, id: info.DefaultBuildId);
        foreach (string name in pplus.TracklistNames())
            info.FixedPathsOnImport += SongStore.CopyTracklistNormalized(Path.Combine(pplus.TracklistDir, name + ".tlst"), defaults.TracklistPath(name));

        lib.SaveInfo();
        return lib;
    }

    private void SaveInfo()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(InfoFile, JsonSerializer.Serialize(Info, Build.Json));
    }

    // ---------- Builds ----------

    public IReadOnlyList<Build> Builds() =>
        (Directory.Exists(BuildsDir) ? Directory.EnumerateDirectories(BuildsDir) : [])
            .Select(d => new Build(this, Path.GetFileName(d)))
            .OrderBy(b => b.IsDefault ? 0 : 1).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public Build DefaultBuild => new(this, Info.DefaultBuildId);

    public Build GetBuild(string id) =>
        Directory.Exists(Path.Combine(BuildsDir, id)) ? new Build(this, id) : throw new MusicLibraryException("That build doesn't exist anymore.");

    /// <summary>"New build": a copy of <paramref name="from"/> (the Default build if null) under a new name.</summary>
    public Build NewBuild(string name, Build? from = null)
    {
        from ??= DefaultBuild;
        Build build = CreateBuildFolder(name, copiedFrom: from.Name);
        foreach (string stage in from.TracklistNames())
            File.Copy(from.TracklistPath(stage), build.TracklistPath(stage));
        return build;
    }

    private Build CreateBuildFolder(string name, string? copiedFrom, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new MusicLibraryException("Please give the build a name.");
        if (Builds().Any(b => b.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new MusicLibraryException($"There's already a build called \"{name.Trim()}\".");

        id ??= UniqueBuildId(name);
        var build = new Build(this, id);
        Directory.CreateDirectory(build.TracklistDir);
        build.Info.Name = name.Trim();
        build.Info.CreatedAt = DateTime.Now;
        build.Info.CopiedFrom = copiedFrom;
        build.SaveInfo();
        return build;
    }

    private string UniqueBuildId(string name)
    {
        string baseId = (SongFileNames.Sanitize(name) ?? "build").ToLowerInvariant().Replace(' ', '-');
        string id = baseId;
        for (int n = 2; Directory.Exists(Path.Combine(BuildsDir, id)); n++) id = $"{baseId}-{n}";
        return id;
    }

    public void DeleteBuild(Build build)
    {
        if (build.IsDefault) throw new MusicLibraryException("The Default build can't be deleted. It's what new builds start from.");
        Directory.Delete(build.Dir, recursive: true);
    }

    // ---------- Songs ----------

    /// <summary>Builds whose tracklists use this song (or its "_b" hurry-up version's main song).</summary>
    public IReadOnlyList<Build> BuildsUsing(string songPath)
    {
        string main = SongStore.HurryUpBase(songPath) is { } b && Songs.Has(b) ? b : songPath;
        return Builds().Where(build => build.SongPathsUsed().Contains(main)).ToList();
    }

    /// <summary>
    /// Copies a .brstm into the library right away (the Loop Maker tab), as <c>songs\&lt;series&gt;\&lt;title&gt;.brstm</c>
    /// with a safe, unique name. Returns its song path ("Series/Title"). No build uses it until it's added to a stage.
    /// </summary>
    public string AddSong(string brstmFile, string title, string series)
    {
        if (!File.Exists(brstmFile)) throw new MusicLibraryException($"Can't find the file \"{brstmFile}\".");
        if (!StageEditSession.IsBrstm(brstmFile))
            throw new MusicLibraryException($"\"{Path.GetFileName(brstmFile)}\" isn't a BRSTM song file.");
        if (string.IsNullOrWhiteSpace(title)) throw new MusicLibraryException("Please give the song a title.");
        string songPath = SongFileNames.UniqueSongPath(Songs.Has, series, title);
        Songs.Add(brstmFile, songPath);
        return songPath;
    }

    /// <summary>
    /// The library song that is byte-for-byte this file (so importing it again would just make a copy), or null.
    /// Only songs of the same size are compared, so this is quick even with hundreds of songs.
    /// </summary>
    public string? FindDuplicate(string brstmFile)
    {
        if (!File.Exists(brstmFile)) return null;
        long length = new FileInfo(brstmFile).Length;
        foreach (string songPath in Songs.All())
        {
            string file = Songs.FileFor(songPath);
            if (new FileInfo(file).Length == length && SongStore.SameContent(brstmFile, file)) return songPath;
        }
        return null;
    }

    /// <summary>Deletes a song (and its "_b" version) from the library. Refused while any build uses it.</summary>
    public void DeleteSong(string songPath)
    {
        var users = BuildsUsing(songPath);
        if (users.Count > 0)
            throw new MusicLibraryException(
                $"\"{songPath.Split('/')[^1]}\" is still used by: {string.Join(", ", users.Select(b => b.Name))}. Remove it from those builds first.");
        foreach (string p in new[] { songPath, songPath + SongStore.HurryUpSuffix })
            if (Songs.Has(p)) File.Delete(Songs.FileFor(p));
        DeleteEmptyFolders(SongsDir);
    }

    /// <summary>Songs no build uses (excluding "_b" versions of used songs).</summary>
    public IReadOnlyList<string> UnusedSongs()
    {
        var used = new HashSet<string>(Builds().SelectMany(b => b.SongPathsUsed()), StringComparer.OrdinalIgnoreCase);
        return Songs.All()
            .Where(p => !used.Contains(p) && !(SongStore.HurryUpBase(p) is { } b && used.Contains(b)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------- Import a music folder as a build ----------

    /// <summary>
    /// Imports a folder with <c>strm\</c> + <c>tracklist\</c> (e.g. a music repo) as a new build: the Default build's
    /// tracklists, replaced by the folder's where it has them. Songs are added to the library; identical files are
    /// shared, and a different file with an already-used name is imported under a new name (tracklists updated to match).
    /// </summary>
    public ImportResult ImportMusicFolder(MusicFolder folder, string buildName)
    {
        var source = new SongStore(folder.StrmDir);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var all = source.All().ToList();
        var mains = all.Where(p => !(SongStore.HurryUpBase(p) is { } b && all.Contains(b, StringComparer.OrdinalIgnoreCase))).ToList();

        // A song and its "_b" version move together, so the game still finds the "_b" next to its song.
        foreach (string main in mains)
        {
            string hurry = main + SongStore.HurryUpSuffix;
            bool hasHurry = source.Has(hurry);
            bool clash = Clashes(source, main) || (hasHurry && Clashes(source, hurry));
            string target = main;
            if (clash)
            {
                string series = main.Contains('/') ? main[..main.LastIndexOf('/')] : "Imported";
                target = SongFileNames.UniqueSongPath(p => Songs.Has(p) || Songs.Has(p + SongStore.HurryUpSuffix), series, main.Split('/')[^1]);
                renames[main] = target;
            }
            if (!Songs.Has(target)) Songs.Add(source.FileFor(main), target);
            if (hasHurry && !Songs.Has(target + SongStore.HurryUpSuffix)) Songs.Add(source.FileFor(hurry), target + SongStore.HurryUpSuffix);
        }

        Build build = NewBuild(buildName, DefaultBuild);
        int fixedPaths = 0;
        foreach (string stage in folder.TracklistNames())
        {
            string dest = build.TracklistPath(stage);
            fixedPaths += SongStore.CopyTracklistNormalized(folder.TracklistPath(stage), dest);
            if (renames.Count == 0) continue;
            var tlst = TlstFile.Load(dest);
            bool changed = false;
            foreach (TlstEntry e in tlst.Entries)
                if (e.Path is not null && renames.TryGetValue(e.Path, out string? renamed)) { e.Path = renamed; changed = true; }
            if (changed) tlst.Save(dest);
        }
        return new ImportResult(build, fixedPaths, renames.Count);
    }

    private bool Clashes(SongStore source, string songPath) =>
        Songs.Has(songPath) && !SongStore.SameContent(Songs.FileFor(songPath), source.FileFor(songPath));

    // ---------- Helpers ----------

    internal static bool IsInside(string path, string folder)
    {
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    internal static void DeleteEmptyFolders(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
}
