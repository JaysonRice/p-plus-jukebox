namespace PPlusTracklistEditor.Core.Tests;

/// <summary>
/// Real P+ files on this machine, used for round-trip tests. Tests that need them pass trivially
/// (and say so in the output) when the folders aren't present, so the suite still runs elsewhere.
/// Override with env vars PPLUS_MUSIC_ROOT / PPLUS_BUILD_SOUND.
/// </summary>
internal static class RealData
{
    public static string MusicRoot =>
        Environment.GetEnvironmentVariable("PPLUS_MUSIC_ROOT") ?? @"C:\Users\jrice\Documents\GitHub\Moist-PM-Music";

    public static string BuildSound =>
        Environment.GetEnvironmentVariable("PPLUS_BUILD_SOUND") ?? @"C:\Users\jrice\workspace\pplus-wii\Project+\pf\sound";

    /// <summary>Backup of the old SD card; its Castle_Siege.tlst has 11 junk bytes after the header's file size.</summary>
    public const string JunkTrailingTlst = @"C:\Users\jrice\Desktop\Tussle Build\Project+\pf\sound\tracklist\Castle_Siege.tlst";

    public static IEnumerable<string> AllTracklists() =>
        new[] { MusicRoot, BuildSound }
            .Select(root => Path.Combine(root, "tracklist"))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.tlst"));
}
