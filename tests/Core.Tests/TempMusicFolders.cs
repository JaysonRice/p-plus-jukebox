using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Tests;

/// <summary>A throwaway music folder + stock build folder in %TEMP%, deleted on dispose.</summary>
internal sealed class TempMusicFolders : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("pplus-lib-test").FullName;
    public string MusicRoot => Path.Combine(Root, "music");
    public string StockRoot => Path.Combine(Root, "stock");
    public string BackupDir => Path.Combine(Root, "backups");
    public string Downloads => Path.Combine(Root, "downloads");

    public TempMusicFolders()
    {
        foreach (string r in new[] { MusicRoot, StockRoot })
        {
            Directory.CreateDirectory(Path.Combine(r, "strm"));
            Directory.CreateDirectory(Path.Combine(r, "tracklist"));
        }
        Directory.CreateDirectory(Downloads);
    }

    public MusicLibrary Library(bool withStock = true) =>
        new(MusicFolder.Open(MusicRoot), withStock ? MusicFolder.Open(StockRoot) : null) { BackupDir = BackupDir };

    /// <summary>A file that passes the BRSTM check (starts with "RSTM").</summary>
    public string FakeBrstm(string root, string songPath)
    {
        string file = Path.Combine(root, "strm", songPath.Replace('/', '\\') + ".brstm");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [.. "RSTM"u8, 1, 2, 3, 4]);
        return file;
    }

    public string DownloadedBrstm(string name)
    {
        string file = Path.Combine(Downloads, name);
        File.WriteAllBytes(file, [.. "RSTM"u8, 9, 9, 9]);
        return file;
    }

    public void Tracklist(string root, string stage, params TlstEntry[] entries)
    {
        var t = new TlstFile();
        t.Entries.AddRange(entries);
        t.Save(Path.Combine(root, "tracklist", stage + ".tlst"));
    }

    public static TlstEntry Song(uint id, string? path, string? title = null, byte volume = 80) =>
        new() { SongId = id, Path = path, Title = title, Volume = volume, Frequency = 40 };

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
