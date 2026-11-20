namespace PPlusTracklistEditor.Core.Library;

/// <summary>
/// A folder laid out like P+'s <c>pf/sound</c>: <c>strm/</c> (songs) and <c>tracklist/</c> (stage tracklists).
/// Both the user's music repo and the build's stock sound folder are music folders.
/// </summary>
public sealed class MusicFolder
{
    public string Root { get; }
    public string StrmDir => System.IO.Path.Combine(Root, "strm");
    public string TracklistDir => System.IO.Path.Combine(Root, "tracklist");

    private MusicFolder(string root) => Root = System.IO.Path.GetFullPath(root);

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
        Directory.EnumerateFiles(TracklistDir, "*.tlst").Select(f => System.IO.Path.GetFileNameWithoutExtension(f));

    public string TracklistPath(string stageName) => System.IO.Path.Combine(TracklistDir, stageName + ".tlst");

    /// <summary>Full path of a song given the path stored in a tracklist ("Series/Title").</summary>
    public string SongFile(string songPath) =>
        System.IO.Path.Combine(StrmDir, songPath.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".brstm");

    public bool HasSong(string songPath) => File.Exists(SongFile(songPath));

    /// <summary>Series folders (first level under strm/), for the "which series?" picker.</summary>
    public IEnumerable<string> SeriesFolders() =>
        Directory.EnumerateDirectories(StrmDir).Select(d => System.IO.Path.GetFileName(d));

    /// <summary>Every song as a tracklist-style path ("Series/Title").</summary>
    public IEnumerable<string> AllSongPaths() =>
        Directory.EnumerateFiles(StrmDir, "*.brstm", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(StrmDir, f)[..^".brstm".Length].Replace('\\', '/'));
}

/// <summary>A problem with the user's folders or files, worded so it can be shown to the user as-is.</summary>
public sealed class MusicLibraryException(string message) : Exception(message);
