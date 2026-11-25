namespace PPlusTracklistEditor.Core.Library;

/// <summary>
/// The user's "Project+" folder (on an SD card, or any copy of a build). The app only writes its music
/// (<c>pf\sound</c>), stage list presets (<c>pf\stage\switch</c>) and the preset names in two menu files (<c>pf\menu2</c>).
/// </summary>
public sealed class ProjectPlusFolder
{
    public string Root { get; }
    public string SoundDir => Path.Combine(Root, "pf", "sound");
    public string TracklistDir => Path.Combine(SoundDir, "tracklist");
    public string StrmDir => Path.Combine(SoundDir, "strm");
    public SongStore Songs => new(StrmDir);
    public string StageDir => Path.Combine(Root, "pf", "stage");
    /// <summary>The 8 stage list presets (Switch00.rss–Switch07.rss).</summary>
    public string StageListDir => Path.Combine(Root, "pf", "stage", "switch");
    public string StageListPath(int preset) => Path.Combine(StageListDir, $"Switch{preset:00}.rss");
    /// <summary>"01 - Battlefield" lines naming each stage slot.</summary>
    public string StageSlotNamesFile => Path.Combine(Root, "pf", "stage", "stageslot", "Stageslot IDs.txt");
    /// <summary>
    /// The menu files holding the stage list preset names, each with its own copy of the list. The game shows the one
    /// in <c>sc_selcharacter2.pac</c> on Random (L/R); <c>mu_menumain.pac</c> has the main menu's copy. Both are
    /// renamed so they always agree.
    /// </summary>
    public static IReadOnlyList<string> MenuFileNames { get; } = ["sc_selcharacter2.pac", "mu_menumain.pac"];

    public string MenuFilePath(string fileName) => Path.Combine(Root, "pf", "menu2", fileName);

    /// <summary>The stage select screen's file, which holds the stage icons (only ever read).</summary>
    public string StageSelectMenuFile => MenuFilePath("sc_selmap.pac");

    /// <summary>The preset names in this folder's menu files (the first readable one), or null if none can be read.</summary>
    public IReadOnlyList<string>? ReadPresetNames()
    {
        foreach (string file in MenuFileNames)
        {
            try { if (File.Exists(MenuFilePath(file))) return Menu.PresetNames.Read(MenuFilePath(file)); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// The Project+ version as the build says it ("3.2.1"): from the Homebrew Channel's <c>apps\project+\meta.xml</c>
    /// next to the Project+ folder (the exact one), else the launcher's <c>info.xml</c> ("3.2"). Null if neither is there.
    /// </summary>
    public string? ReadVersion()
    {
        string? parent = Path.GetDirectoryName(Root);
        foreach (string file in new[] { parent is null ? "" : Path.Combine(parent, "apps", "project+", "meta.xml"), Path.Combine(Root, "info.xml") })
        {
            try
            {
                if (file.Length == 0 || !File.Exists(file)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(file), @"<version>\s*([^<]+?)\s*</version>");
                if (m.Success) return m.Groups[1].Value;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private ProjectPlusFolder(string root) => Root = Path.GetFullPath(root);

    /// <summary>
    /// Finds the Project+ folder from whatever the user picked: the Project+ folder itself, the SD card root,
    /// or its <c>pf</c> / <c>pf\sound</c> folder. Throws with a plain explanation if it isn't one.
    /// </summary>
    public static ProjectPlusFolder Resolve(string picked)
    {
        if (!Directory.Exists(picked))
            throw new MusicLibraryException($"The folder \"{picked}\" doesn't exist.");
        string full = Path.GetFullPath(picked).TrimEnd(Path.DirectorySeparatorChar);

        foreach (string candidate in Candidates(full))
        {
            if (Directory.Exists(Path.Combine(candidate, "pf", "sound", "tracklist")))
                return new ProjectPlusFolder(candidate);
        }
        throw new MusicLibraryException(
            $"Couldn't find Project+ music in \"{picked}\". Pick the \"Project+\" folder (on your SD card it's next to \"apps\" and \"private\"). It has a \"pf\" folder inside.");
    }

    private static IEnumerable<string> Candidates(string full)
    {
        yield return full;                                            // ...\Project+
        yield return Path.Combine(full, "Project+");                  // SD card root
        string name = Path.GetFileName(full);
        string? parent = Path.GetDirectoryName(full);
        if (parent is null) yield break;
        if (name.Equals("pf", StringComparison.OrdinalIgnoreCase)) yield return parent;   // ...\Project+\pf
        if (name.Equals("sound", StringComparison.OrdinalIgnoreCase)                       // ...\Project+\pf\sound
            && Path.GetFileName(parent).Equals("pf", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(parent) is { } pplus)
            yield return pplus;
    }

    public IEnumerable<string> TracklistNames() =>
        Directory.EnumerateFiles(TracklistDir, "*.tlst").Select(f => Path.GetFileNameWithoutExtension(f));
}
