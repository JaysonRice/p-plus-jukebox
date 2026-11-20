namespace PPlusTracklistEditor.Core.Library;

/// <summary>
/// The user's "Project+" folder (on an SD card, or any copy of a build). The app only ever reads/writes its
/// <c>pf\sound</c> folder (<c>tracklist\</c> + <c>strm\</c>).
/// </summary>
public sealed class ProjectPlusFolder
{
    public string Root { get; }
    public string SoundDir => Path.Combine(Root, "pf", "sound");
    public string TracklistDir => Path.Combine(SoundDir, "tracklist");
    public string StrmDir => Path.Combine(SoundDir, "strm");
    public SongStore Songs => new(StrmDir);

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
