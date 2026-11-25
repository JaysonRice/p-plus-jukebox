using System.Security.Cryptography;

namespace PPlusTracklistEditor.Core.Library;

/// <summary>
/// A folder of .brstm songs laid out like P+'s <c>strm/</c>: <c>&lt;Series&gt;/&lt;Name&gt;.brstm</c>.
/// Songs are identified by the same path a tracklist stores ("Series/Name", no extension, '/' separators).
/// Used for the app's shared library and for any P+ <c>strm</c> folder.
/// </summary>
public sealed class SongStore(string dir)
{
    public const string Extension = ".brstm";

    /// <summary>The faster "hurry up" version of a song, which the game finds by name (<c>&lt;song&gt;_b.brstm</c>).</summary>
    public const string HurryUpSuffix = "_b";

    public string Dir { get; } = Path.GetFullPath(dir);

    /// <summary>
    /// The file for a song path. Song paths come from tracklists, which can be hand-made or downloaded, so a path that
    /// would leave this folder (<c>../x</c>, <c>C:/x</c>) is refused instead of being followed.
    /// </summary>
    public string FileFor(string songPath) =>
        TryFileFor(songPath) ?? throw new MusicLibraryException($"The song path \"{songPath}\" points outside the music folder, so it was skipped.");

    /// <summary>False for a song path that would leave this folder (see <see cref="FileFor"/>).</summary>
    public bool IsSafe(string songPath) => TryFileFor(songPath) is not null;

    private string? TryFileFor(string songPath)
    {
        string full = Path.GetFullPath(Path.Combine(Dir, songPath.Replace('/', Path.DirectorySeparatorChar) + Extension));
        string root = Dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public bool Has(string songPath) => TryFileFor(songPath) is { } file && File.Exists(file);

    /// <summary>Every song path in the store (includes "_b" files).</summary>
    public IEnumerable<string> All() =>
        Directory.Exists(Dir)
            ? Directory.EnumerateFiles(Dir, "*" + Extension, SearchOption.AllDirectories).Select(ToSongPath)
            : [];

    public IEnumerable<string> SeriesFolders() =>
        Directory.Exists(Dir) ? Directory.EnumerateDirectories(Dir).Select(d => Path.GetFileName(d)) : [];

    public string ToSongPath(string file) =>
        Path.GetRelativePath(Dir, file)[..^Extension.Length].Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Copies a file into the store under <paramref name="songPath"/>, keeping its timestamp. Never overwrites.</summary>
    public void Add(string sourceFile, string songPath)
    {
        string target = FileFor(songPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourceFile, target, overwrite: false); // File.Copy keeps the last-write time on Windows
    }

    public static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    /// <summary>
    /// Song paths must use '/' (every stock P+ path does). Some hand-made tracklists use '\', which Windows accepts but
    /// the Wii may not. Comparisons and imports go through this.
    /// </summary>
    public static string Normalize(string songPath) => songPath.Replace('\\', '/');

    /// <summary>
    /// Copies a tracklist, converting any '\' song paths to '/'. Unchanged files are copied byte for byte.
    /// Returns how many paths were fixed.
    /// </summary>
    public static int CopyTracklistNormalized(string source, string dest)
    {
        var tlst = Tlst.TlstFile.Load(source);
        int fixedPaths = 0;
        foreach (var e in tlst.Entries)
        {
            if (e.Path is not null && e.Path.Contains('\\')) { e.Path = Normalize(e.Path); fixedPaths++; }
        }
        if (fixedPaths == 0) File.Copy(source, dest, overwrite: true);
        else tlst.Save(dest);
        return fixedPaths;
    }

    /// <summary>"Series/Song_b" → "Series/Song"; null if it isn't a hurry-up name.</summary>
    public static string? HurryUpBase(string songPath) =>
        songPath.EndsWith(HurryUpSuffix, StringComparison.OrdinalIgnoreCase) && songPath.Length > HurryUpSuffix.Length
            ? songPath[..^HurryUpSuffix.Length]
            : null;
}
