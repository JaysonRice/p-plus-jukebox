using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Library;

/// <summary>Where a tracklist song's audio comes from.</summary>
public enum SongSource
{
    /// <summary>Brawl's original song, read from the game disc (no file needed).</summary>
    Disc,
    /// <summary>A file in the user's music folder.</summary>
    MusicFolder,
    /// <summary>A file in the stock P+ build (used when the music folder doesn't have it).</summary>
    StockBuild,
    /// <summary>The tracklist points at a file that doesn't exist anywhere: the stage would play silence.</summary>
    Missing,
}

public sealed record SongLocation(SongSource Source, string? FilePath);

/// <summary>One stage tracklist as the user sees it.</summary>
public sealed record StageInfo(string Name, string DisplayName, bool HasCustom, bool HasStock, bool? UsedByBuild)
{
    /// <summary>The stage plays stock P+ music because the music folder has no tracklist for it.</summary>
    public bool IsStockOnly => !HasCustom && HasStock;
}

/// <summary>
/// The user's music folder, optionally layered over the stock P+ build's sound folder, like the SD card sync does:
/// files in the music folder win, stock files fill the gaps. Edits always go to the music folder; the build is never written.
/// </summary>
public sealed class MusicLibrary
{
    public MusicFolder Music { get; }
    public MusicFolder? Stock { get; }

    /// <summary>Where previous versions of tracklists are kept on save. Outside the music folder so it never syncs.</summary>
    public string BackupDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PPlusTracklistEditor", "backups");

    public MusicLibrary(MusicFolder music, MusicFolder? stock = null)
    {
        if (stock is not null && string.Equals(music.Root, stock.Root, StringComparison.OrdinalIgnoreCase))
            throw new MusicLibraryException("The music folder and the P+ build folder are the same folder. Pick your own music folder.");
        Music = music;
        Stock = stock;
    }

    public IReadOnlyList<StageInfo> Stages()
    {
        var custom = Music.TracklistNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stock = Stock?.TracklistNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return custom.Union(stock ?? [], StringComparer.OrdinalIgnoreCase)
            .Select(name => new StageInfo(
                name,
                StageNames.Pretty(name),
                HasCustom: custom.Contains(name),
                HasStock: stock?.Contains(name) ?? false,
                UsedByBuild: stock is null ? null : stock.Contains(name)))
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public SongLocation Locate(TlstEntry entry)
    {
        if (entry.Path is null) return new SongLocation(SongSource.Disc, null);
        if (Music.HasSong(entry.Path)) return new SongLocation(SongSource.MusicFolder, Music.SongFile(entry.Path));
        if (Stock?.HasSong(entry.Path) == true) return new SongLocation(SongSource.StockBuild, Stock.SongFile(entry.Path));
        // "Z01", "H06"…: a Brawl disc song by its disc file name. The game loads it from the disc when the SD card doesn't have it.
        if (DiscSongs.IsDiscFileName(entry.Path)) return new SongLocation(SongSource.Disc, null);
        return new SongLocation(SongSource.Missing, null);
    }

    /// <summary>Title to show: the tracklist's own title, else the disc song's name, else the file name.</summary>
    public static string DisplayTitle(TlstEntry entry) =>
        entry.Title
        ?? (entry.Path is null ? DiscSongs.Find(entry.SongId)?.Name : null)
        ?? (entry.Path is not null ? entry.Path.Split('/')[^1] : null)
        ?? "Unnamed song";

    /// <summary>Opens a stage for editing. A stock-only stage starts as a copy of its stock tracklist and is saved into the music folder.</summary>
    public StageEditSession OpenStage(string stageName)
    {
        string customPath = Music.TracklistPath(stageName);
        if (File.Exists(customPath))
            return new StageEditSession(this, stageName, File.ReadAllBytes(customPath), startsFromStock: false);

        string? stockPath = Stock?.TracklistPath(stageName);
        if (stockPath is not null && File.Exists(stockPath))
            return new StageEditSession(this, stageName, File.ReadAllBytes(stockPath), startsFromStock: true);

        throw new MusicLibraryException($"There's no tracklist called \"{stageName}\".");
    }
}
