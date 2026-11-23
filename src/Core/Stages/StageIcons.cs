using System.Globalization;
using PPlusTracklistEditor.Core.Menu;

namespace PPlusTracklistEditor.Core.Stages;

/// <summary>
/// The stage select icons from a build's <c>pf/menu2/sc_selmap.pac</c> (LZ11 → ARC → BRRES files with textures
/// <c>MenSelmapIcon.NN</c>, NN = the stage's cosmetic ID; 128x112 CI8 in stock). Read-only; icons decode on first use.
/// </summary>
public sealed class StageIcons
{
    private const string Prefix = "MenSelmapIcon.";
    private readonly Dictionary<int, (Brres File, string Name)> _textures;
    private readonly Dictionary<int, WiiImage?> _decoded = [];
    private readonly Lock _lock = new();

    private StageIcons(Dictionary<int, (Brres, string)> textures) => _textures = textures;

    public static StageIcons Empty { get; } = new([]);

    /// <summary>How many stage icons the file has.</summary>
    public int Count => _textures.Count;

    /// <summary>Reads the icons from a <c>sc_selmap.pac</c>; a missing or unreadable file gives no icons.</summary>
    public static StageIcons Load(string path)
    {
        try { return File.Exists(path) ? FromBytes(File.ReadAllBytes(path)) : Empty; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                    or ArgumentOutOfRangeException or IndexOutOfRangeException) { return Empty; }
    }

    public static StageIcons FromBytes(byte[] pac)
    {
        byte[] raw = Lz11.LooksCompressed(pac) ? Lz11.Decompress(pac) : pac;
        var textures = new Dictionary<int, (Brres, string)>();
        foreach (Brres brres in BrresFiles(ArcArchive.Parse(raw)))
            foreach (string name in brres.TextureNames)
                if (name.StartsWith(Prefix, StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                    && id > 0)
                    textures.TryAdd(id, (brres, name));
        return new StageIcons(textures);
    }

    private static IEnumerable<Brres> BrresFiles(ArcArchive arc)
    {
        for (int i = 0; i < arc.Count; i++)
        {
            byte[] data = arc.Data(i);
            if (Brres.LooksLikeBrres(data)) yield return Brres.Parse(data);
            else if (data.AsSpan().StartsWith("ARC\0"u8))
                foreach (Brres inner in BrresFiles(ArcArchive.Parse(data))) yield return inner;
        }
    }

    /// <summary>The icon for a cosmetic ID, or null if the file has none (or it can't be decoded).</summary>
    public WiiImage? For(int cosmeticId)
    {
        lock (_lock)
        {
            if (_decoded.TryGetValue(cosmeticId, out var done)) return done;
            WiiImage? image = null;
            if (_textures.TryGetValue(cosmeticId, out var t))
            {
                try { image = t.File.DecodeTexture(t.Name); }
                catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { }
            }
            _decoded[cosmeticId] = image;
            return image;
        }
    }
}
