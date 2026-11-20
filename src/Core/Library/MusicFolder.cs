namespace PPlusTracklistEditor.Core.Library;

/// <summary>
/// A folder laid out like P+'s <c>pf/sound</c>: <c>strm/</c> (songs) and <c>tracklist/</c> (stage tracklists),
/// e.g. someone's music repo. Used as an import source.
/// </summary>
public sealed class MusicFolder
{
    public string Root { get; }
    public string StrmDir => Path.Combine(Root, "strm");
    public string TracklistDir => Path.Combine(Root, "tracklist");

    private MusicFolder(string root) => Root = Path.GetFullPath(root);

    /// <summary>Opens a music folder. Fails with a plain-language reason if it isn't one.</summary>
    public static MusicFolder Open(string root)
    {
        if (!Directory.Exists(root))
            throw new MusicLibraryException($"The folder \"{root}\" doesn't exist.");
        var folder = new MusicFolder(root);
        if (!Directory.Exists(folder.StrmDir) || !Directory.Exists(folder.TracklistDir))
            throw new MusicLibraryException(
                $"\"{root}\" isn't a music folder. It needs a \"strm\" folder (songs) and a \"tracklist\" folder (stage lists) inside it.");
        return folder;
    }

    /// <summary>Stage names (tracklist file names without ".tlst").</summary>
    public IEnumerable<string> TracklistNames() =>
        Directory.EnumerateFiles(TracklistDir, "*.tlst").Select(f => Path.GetFileNameWithoutExtension(f));

    public string TracklistPath(string stageName) => Path.Combine(TracklistDir, stageName + ".tlst");
}

/// <summary>A problem with the user's folders or files, worded so it can be shown to the user as-is.</summary>
public sealed class MusicLibraryException(string message) : Exception(message);
