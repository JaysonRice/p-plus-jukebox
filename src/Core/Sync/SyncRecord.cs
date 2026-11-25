using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.Core.Sync;

/// <summary>
/// What the app last put in one Project+ folder: a fingerprint of each song list and preset it wrote, and the preset
/// names. "Update the app from this Project+" uses it to tell the user's own music (put there by a sync) from changes
/// Project+ itself made (an update), so a build's songs never become the "Project+ original".
/// Stored as <c>Library\syncs\&lt;fingerprint of the folder path&gt;.json</c>.
/// </summary>
public sealed class SyncRecord
{
    public string Root { get; set; } = "";
    public Dictionary<string, string> Tracklists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> Presets { get; set; } = [];
    public List<string>? PresetNames { get; set; }

    private static string FileFor(AppLibrary lib, string root) =>
        Path.Combine(lib.Root, "syncs", Fingerprint(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant()))[..16] + ".json");

    public static SyncRecord Load(AppLibrary lib, string root)
    {
        try
        {
            string file = FileFor(lib, root);
            if (File.Exists(file) && JsonSerializer.Deserialize<SyncRecord>(File.ReadAllText(file)) is { } record)
            {
                record.Tracklists = new(record.Tracklists, StringComparer.OrdinalIgnoreCase);
                return record;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new SyncRecord { Root = Path.GetFullPath(root) };
    }

    public void Save(AppLibrary lib)
    {
        string file = FileFor(lib, Root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>A short fingerprint (SHA-256, hex) of some bytes: equal fingerprints = equal files.</summary>
    public static string Fingerprint(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static string FingerprintOf(string file) => Fingerprint(File.ReadAllBytes(file));
}
