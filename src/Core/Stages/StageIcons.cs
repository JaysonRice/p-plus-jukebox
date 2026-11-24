using System.Globalization;
using PPlusTracklistEditor.Core.Menu;

namespace PPlusTracklistEditor.Core.Stages;

/// <summary>
/// The stage select icons from a build's <c>pf/menu2/sc_selmap.pac</c> (LZ11 → ARC → BRRES files with textures
/// <c>MenSelmapIcon.NN</c>, 128x112 CI8 in stock), looked up by the stage's cosmetic ID. Read-only; icons decode on
/// first use.
/// </summary>
public sealed class StageIcons
{
    private const string Prefix = "MenSelmapIcon.";
    private readonly Dictionary<int, (Brres File, string Name)> _textures;
    private readonly Dictionary<string, WiiImage?> _decoded = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    private StageIcons(Dictionary<int, (Brres, string)> textures) => _textures = textures;

    public static StageIcons Empty { get; } = new([]);

    /// <summary>How many different stage icons the file has.</summary>
    public int Count => _textures.Values.Select(t => t.Name).Distinct().Count();

    /// <summary>Reads the icons from a <c>sc_selmap.pac</c>; a missing or unreadable file gives no icons.</summary>
    public static StageIcons Load(string path)
    {
        try { return File.Exists(path) ? FromBytes(File.ReadAllBytes(path)) : Empty; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                    or ArgumentOutOfRangeException or IndexOutOfRangeException) { return Empty; }
    }

    /// <remarks>
    /// The game picks a stage's icon with the <c>MenSelmapIcon</c> texture-swap animation: frame = cosmetic ID, and each
    /// keyframe names the texture shown from that frame on. Stock stages map to their own number, but P+'s added stages
    /// don't (cosmetic 35 shows <c>MenSelmapIcon.80</c>, 61 shows <c>.95</c>). A file without that animation falls back
    /// to "texture number = cosmetic ID".
    /// </remarks>
    public static StageIcons FromBytes(byte[] pac)
    {
        byte[] raw = Lz11.LooksCompressed(pac) ? Lz11.Decompress(pac) : pac;
        var byName = new Dictionary<string, Brres>(StringComparer.Ordinal);
        IReadOnlyList<(float Frame, string Texture)> pattern = [];
        foreach (Brres brres in BrresFiles(ArcArchive.Parse(raw)))
        {
            foreach (string name in brres.TextureNames)
                if (name.StartsWith(Prefix, StringComparison.Ordinal)) byName.TryAdd(name, brres);
            if (pattern.Count == 0) pattern = brres.TexturePattern("MenSelmapIcon");
        }

        var textures = new Dictionary<int, (Brres, string)>();
        if (pattern.Count > 0)
        {
            var keys = pattern.OrderBy(k => k.Frame).ToList();
            int last = (int)keys[^1].Frame;
            for (int id = 1, k = -1; id <= Math.Min(last, MaxCosmeticId); id++)
            {
                while (k + 1 < keys.Count && keys[k + 1].Frame <= id) k++;
                if (k >= 0 && byName.TryGetValue(keys[k].Texture, out Brres? file)) textures[id] = (file, keys[k].Texture);
            }
        }
        else
        {
            foreach (var (name, file) in byName)
                if (int.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                    && id is > 0 and <= MaxCosmeticId)
                    textures.TryAdd(id, (file, name));
        }
        return new StageIcons(textures);
    }

    /// <summary>Cosmetic IDs are one byte in the stage list files.</summary>
    private const int MaxCosmeticId = 255;

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
            if (!_textures.TryGetValue(cosmeticId, out var t)) return null;
            if (_decoded.TryGetValue(t.Name, out var done)) return done;
            WiiImage? image = null;
            try { image = t.File.DecodeTexture(t.Name); }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { }
            _decoded[t.Name] = image;
            return image;
        }
    }
}
