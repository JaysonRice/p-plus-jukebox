using System.Buffers.Binary;
using System.Text;

namespace PPlusTracklistEditor.Core.Menu;

/// <summary>
/// Read-only view of a Wii BRRES resource file (the "bres" files inside menu archives). Only finds textures (TEX0) and
/// palettes (PLT0) by name; nothing is ever written back.
/// </summary>
/// <remarks>
/// Layout (big-endian): header "bres", u16 byte-order mark, u16 pad, u32 length, u16 root offset, u16 section count.
/// The root section ("root", u32 size) holds an index group of folders ("Textures(NW4R)", "Palettes(NW4R)", ...), each
/// pointing at an index group of its entries. An index group is u32 size, u32 count, then count + 1 entries of 16 bytes
/// (u16 id, u16 flag, u16 left, u16 right, u32 name offset, u32 data offset; offsets from the group's start; entry 0 is
/// the tree's root). A name offset points just past a u32 length.
/// </remarks>
public sealed class Brres
{
    private readonly byte[] _data;
    private readonly Dictionary<string, Dictionary<string, int>> _folders = new(StringComparer.Ordinal);

    private Brres(byte[] data)
    {
        _data = data;
        if (data.Length < 0x10 || !data.AsSpan(0, 4).SequenceEqual("bres"u8)) throw new InvalidDataException("Not a BRRES file.");
        int root = U16(0x0C);
        if (!data.AsSpan(root, 4).SequenceEqual("root"u8)) throw new InvalidDataException("BRRES has no root section.");
        foreach (var (name, folder) in Group(root + 8))
        {
            var entries = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (entryName, offset) in Group(folder)) entries[entryName] = offset;
            _folders[name] = entries;
        }
    }

    public static bool LooksLikeBrres(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("bres"u8);

    public static Brres Parse(byte[] data) => new(data);

    /// <summary>The names of the textures in this file.</summary>
    public IEnumerable<string> TextureNames => Folder("Textures(NW4R)").Keys;

    /// <summary>Decodes the named texture (its first image) to 32-bit BGRA pixels, or null if it isn't here.</summary>
    public WiiImage? DecodeTexture(string name)
    {
        if (!Folder("Textures(NW4R)").TryGetValue(name, out int tex)) return null;
        if (!_data.AsSpan(tex, 4).SequenceEqual("TEX0"u8)) throw new InvalidDataException($"Texture {name} is damaged.");
        int dataAt = tex + I32(tex + 0x10);
        bool hasPalette = U32(tex + 0x18) != 0;
        int width = U16(tex + 0x1C), height = U16(tex + 0x1E);
        var format = (WiiTextureFormat)U32(tex + 0x20);

        ushort[]? palette = null;
        var paletteFormat = WiiPaletteFormat.RGB5A3;
        if (hasPalette || format is WiiTextureFormat.CI4 or WiiTextureFormat.CI8)
        {
            if (!Folder("Palettes(NW4R)").TryGetValue(name, out int plt)) throw new InvalidDataException($"Texture {name} has no palette.");
            int colors = U16(plt + 0x1C);
            paletteFormat = (WiiPaletteFormat)U32(plt + 0x18);
            int at = plt + I32(plt + 0x10);
            palette = new ushort[colors];
            for (int i = 0; i < colors; i++) palette[i] = U16(at + i * 2);
        }
        return WiiTexture.Decode(format, width, height, _data.AsSpan(dataAt), palette, paletteFormat);
    }

    private Dictionary<string, int> Folder(string name) => _folders.GetValueOrDefault(name) ?? [];

    /// <summary>The named entries of the index group at <paramref name="group"/>, with absolute data offsets.</summary>
    private IEnumerable<(string Name, int Offset)> Group(int group)
    {
        int count = I32(group + 4);
        for (int i = 1; i <= count; i++)
        {
            int entry = group + 8 + i * 16;
            int nameAt = group + I32(entry + 8), dataAt = group + I32(entry + 12);
            int length = I32(nameAt - 4);
            yield return (Encoding.ASCII.GetString(_data, nameAt, length), dataAt);
        }
    }

    private ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));
    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at));
    private int I32(int at) => BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(at));
}
