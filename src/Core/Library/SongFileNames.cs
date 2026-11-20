using System.Globalization;
using System.Text;

namespace PPlusTracklistEditor.Core.Library;

/// <summary>
/// Makes safe song file names. Every existing P+ song path is plain ASCII (titles can say "Pokémon", paths say
/// "Pokemon"), so names are folded to ASCII. Characters Windows/FAT32 forbid are removed; ' and &amp; are kept
/// (both are common in real paths).
/// </summary>
public static class SongFileNames
{
    /// <summary>Longest name we produce (without ".brstm"). Real paths top out around 80 characters including the series folder.</summary>
    public const int MaxNameLength = 80;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A file/folder name from free text, or null if nothing usable is left (e.g. the title was all symbols).</summary>
    public static string? Sanitize(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in FoldToAscii(text))
        {
            if (c < 0x20 || c > 0x7E || "<>:\"/\\|?*".Contains(c)) sb.Append(' ');
            else sb.Append(c);
        }
        string name = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        name = name.TrimEnd('.', ' ');
        if (name.Length == 0) return null;
        if (Reserved.Contains(name)) name += "_";
        return name;
    }

    private static string FoldToAscii(string text)
    {
        // "é" → "e" + combining accent → drop the accent. Plus a few letters that don't decompose.
        string decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch
            {
                'ß' => "ss", 'æ' => "ae", 'Æ' => "AE", 'ø' => "o", 'Ø' => "O", 'đ' => "d", 'Đ' => "D", 'ł' => "l", 'Ł' => "L",
                '–' or '—' => "-", '‘' or '’' => "'", '“' or '”' => "\"", '…' => "...", '×' => "x",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Picks a tracklist song path ("Series/Name") for which <paramref name="exists"/> is false
    /// and that isn't in <paramref name="alsoTaken"/> (songs queued but not yet copied). Adds " (2)", " (3)"… when needed.
    /// </summary>
    public static string UniqueSongPath(Func<string, bool> exists, string series, string title, IEnumerable<string>? alsoTaken = null)
    {
        // Series is optional: a song without one sits directly in strm/ (path "Title" instead of "Series/Title").
        string? seriesName = string.IsNullOrWhiteSpace(series)
            ? null
            : Sanitize(series) ?? throw new MusicLibraryException("Please give the series a name with some letters or numbers in it, or leave it empty.");
        string baseName = Sanitize(title) ?? throw new MusicLibraryException("Please give the song a title with some letters or numbers in it.");
        var taken = new HashSet<string>(alsoTaken ?? [], StringComparer.OrdinalIgnoreCase); // FAT32 is case-insensitive

        for (int n = 1; ; n++)
        {
            string suffix = n == 1 ? "" : $" ({n})";
            string name = baseName.Length + suffix.Length > MaxNameLength ? baseName[..(MaxNameLength - suffix.Length)].TrimEnd() + suffix : baseName + suffix;
            string path = seriesName is null ? name : $"{seriesName}/{name}";
            if (!taken.Contains(path) && !exists(path)) return path;
        }
    }
}
