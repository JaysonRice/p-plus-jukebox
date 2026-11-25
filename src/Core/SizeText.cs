namespace PPlusTracklistEditor.Core;

/// <summary>File sizes in plain words: "4.7 MB", "707 MB", "1.2 GB".</summary>
public static class SizeText
{
    private const long Mb = 1L << 20, Gb = 1L << 30;

    public static string Of(long bytes) =>
        bytes >= Gb ? $"{bytes / (double)Gb:0.0} GB"
        : bytes >= 100 * Mb ? $"{bytes / Mb} MB"
        : $"{Math.Max(0.1, bytes / (double)Mb):0.0} MB";
}
