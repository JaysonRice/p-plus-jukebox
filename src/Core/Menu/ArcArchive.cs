using System.Buffers.Binary;

namespace PPlusTracklistEditor.Core.Menu;

/// <summary>
/// Brawl's "ARC" archive (big-endian), as inside <c>.pac</c> files: a 0x40-byte header ("ARC\0", version, file count,
/// name), then for each file a 0x20-byte header (type, id, size, group, redirect...) followed by its data, padded with
/// zeros to a multiple of 0x20.
///
/// Only a file's data can be replaced; every header byte except the size is kept, so an unchanged archive rebuilds
/// byte for byte.
/// </summary>
public sealed class ArcArchive
{
    private const int HeaderSize = 0x40, EntryHeaderSize = 0x20, Align = 0x20;

    private readonly byte[] _header;
    private readonly List<(byte[] Header, byte[] Data)> _files = [];

    private ArcArchive(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual("ARC\0"u8))
            throw new InvalidDataException("This isn't a Brawl ARC archive.");
        _header = bytes[..HeaderSize].ToArray();
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        int at = HeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (at + EntryHeaderSize > bytes.Length) throw new InvalidDataException("The archive ends too early.");
            int size = BinaryPrimitives.ReadInt32BigEndian(bytes[(at + 4)..]);
            if (size < 0 || at + EntryHeaderSize + size > bytes.Length) throw new InvalidDataException("A file in the archive runs past its end.");
            _files.Add((bytes.Slice(at, EntryHeaderSize).ToArray(), bytes.Slice(at + EntryHeaderSize, size).ToArray()));
            at = AlignUp(at + EntryHeaderSize + size);
        }
        Length = at;
    }

    public static ArcArchive Parse(ReadOnlySpan<byte> bytes) => new(bytes);

    /// <summary>The size the archive takes up (what <see cref="ToBytes"/> gives for an unchanged archive).</summary>
    public int Length { get; }

    public string Name => System.Text.Encoding.ASCII.GetString(_header, 0x10, 0x30).Split('\0')[0];
    public int Count => _files.Count;

    public byte[] Data(int index) => _files[index].Data;

    public void Replace(int index, byte[] data) => _files[index] = (_files[index].Header, data);

    /// <summary>Index of the first file whose data contains <paramref name="needle"/>, or -1.</summary>
    public int IndexContaining(ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i < _files.Count; i++)
            if (_files[i].Data.AsSpan().IndexOf(needle) >= 0) return i;
        return -1;
    }

    public byte[] ToBytes()
    {
        int total = HeaderSize + _files.Sum(f => AlignUp(EntryHeaderSize + f.Data.Length));
        var output = new byte[total];
        _header.CopyTo(output, 0);
        int at = HeaderSize;
        foreach (var (header, data) in _files)
        {
            header.CopyTo(output, at);
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(at + 4), data.Length);
            data.CopyTo(output, at + EntryHeaderSize);
            at = AlignUp(at + EntryHeaderSize + data.Length);
        }
        return output;
    }

    private static int AlignUp(int n) => (n + Align - 1) & ~(Align - 1);
}

/// <summary>
/// A Brawl message table as used by Project+'s menus: a list of big-endian u32 offsets (one per string, plus one for
/// the end), then the strings back to back as UTF-8, with no terminators.
/// </summary>
public sealed class MessageTable
{
    public List<string> Strings { get; }

    private MessageTable(List<string> strings) => Strings = strings;

    public static MessageTable Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) throw new InvalidDataException("The message table is empty.");
        int first = BinaryPrimitives.ReadInt32BigEndian(bytes);
        if (first < 4 || first % 4 != 0 || first > bytes.Length) throw new InvalidDataException("The message table's offsets are damaged.");
        int count = first / 4;
        var offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadInt32BigEndian(bytes[(i * 4)..]);
            if (offsets[i] < first || offsets[i] > bytes.Length || (i > 0 && offsets[i] < offsets[i - 1]))
                throw new InvalidDataException("The message table's offsets are damaged.");
        }
        if (offsets[^1] != bytes.Length) throw new InvalidDataException("The message table has data after its last string.");
        var strings = new List<string>(count - 1);
        for (int i = 0; i < count - 1; i++) strings.Add(System.Text.Encoding.UTF8.GetString(bytes[offsets[i]..offsets[i + 1]]));
        return new MessageTable(strings);
    }

    public byte[] ToBytes()
    {
        var encoded = Strings.Select(System.Text.Encoding.UTF8.GetBytes).ToList();
        int tableSize = (encoded.Count + 1) * 4;
        var output = new byte[tableSize + encoded.Sum(e => e.Length)];
        int at = tableSize;
        for (int i = 0; i < encoded.Count; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(i * 4), at);
            encoded[i].CopyTo(output, at);
            at += encoded[i].Length;
        }
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(encoded.Count * 4), at);
        return output;
    }
}
