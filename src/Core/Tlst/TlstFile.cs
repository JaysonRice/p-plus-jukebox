using System.Buffers.Binary;
using System.Text;

namespace PPlusTracklistEditor.Core.Tlst;

/// <summary>
/// A Project+ stage tracklist (.tlst): the list of songs a stage can play.
/// <para>
/// Layout (big-endian):
/// <code>
/// 0x00  "TLST"
/// 0x04  u32 entry count
/// 0x08  u16 file size (bytes after this are ignored by the game)
/// 0x0A  u16 string table offset
/// 0x0C  entries, 16 bytes each (see <see cref="TlstEntry"/>)
/// ....  string table: null-terminated UTF-8 strings, referenced by offsets relative to its start
/// </code>
/// Strings are written in entry order, path then title, one copy each (no sharing).
/// That's how every existing tracklist is laid out, so writing an unchanged file reproduces it byte for byte.
/// </para>
/// </summary>
public sealed class TlstFile
{
    public const int HeaderSize = 12;
    public const int EntrySize = 16;
    private const ushort NoString = 0xFFFF;
    private static readonly byte[] Magic = "TLST"u8.ToArray();

    // Strict UTF-8: invalid bytes throw instead of silently becoming '?', so we never write back a mangled title.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public List<TlstEntry> Entries { get; } = [];

    /// <summary>
    /// Bytes found after the header's file size when reading. The game ignores them and
    /// <see cref="ToBytes"/> drops them. Non-zero means the file had leftover junk.
    /// </summary>
    public int IgnoredTrailingBytes { get; private set; }

    public static TlstFile Load(string path) => Read(File.ReadAllBytes(path));

    public static TlstFile Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..4].SequenceEqual(Magic))
            throw new TlstFormatException("Not a tracklist file (missing \"TLST\" header).");

        uint count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int fileSize = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        int stringTable = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);

        if (fileSize > data.Length)
            throw new TlstFormatException($"File is cut short: header says {fileSize} bytes, file has {data.Length}.");
        if (count > (fileSize - HeaderSize) / EntrySize)
            throw new TlstFormatException($"Entry count {count} doesn't fit in a {fileSize}-byte file.");
        if (stringTable < HeaderSize + count * EntrySize || stringTable > fileSize)
            throw new TlstFormatException($"String table offset 0x{stringTable:X} is outside the file.");

        var file = new TlstFile { IgnoredTrailingBytes = data.Length - fileSize };
        ReadOnlySpan<byte> strings = data[stringTable..fileSize];

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = data.Slice(HeaderSize + i * EntrySize, EntrySize);
            file.Entries.Add(new TlstEntry
            {
                SongId = BinaryPrimitives.ReadUInt32BigEndian(e),
                Delay = BinaryPrimitives.ReadInt16BigEndian(e[4..]),
                Volume = e[6],
                Frequency = e[7],
                Path = ReadString(strings, BinaryPrimitives.ReadUInt16BigEndian(e[8..]), i, "path"),
                Title = ReadString(strings, BinaryPrimitives.ReadUInt16BigEndian(e[10..]), i, "title"),
                SongSwitch = BinaryPrimitives.ReadUInt16BigEndian(e[12..]),
                DisableStockPinchRaw = e[14],
                HiddenFromTracklistRaw = e[15],
            });
        }
        return file;
    }

    private static string? ReadString(ReadOnlySpan<byte> strings, ushort offset, int entry, string what)
    {
        if (offset == NoString) return null;
        if (offset >= strings.Length)
            throw new TlstFormatException($"Song {entry + 1}: {what} points outside the file.");
        int end = strings[offset..].IndexOf((byte)0);
        if (end < 0)
            throw new TlstFormatException($"Song {entry + 1}: {what} has no end marker.");
        try { return Utf8.GetString(strings.Slice(offset, end)); }
        catch (DecoderFallbackException) { throw new TlstFormatException($"Song {entry + 1}: {what} isn't valid text (UTF-8)."); }
    }

    public byte[] ToBytes()
    {
        int stringTable = HeaderSize + Entries.Count * EntrySize;
        var strings = new MemoryStream();
        var offsets = new (ushort Path, ushort Title)[Entries.Count];

        for (int i = 0; i < Entries.Count; i++)
        {
            TlstEntry entry = Entries[i];
            offsets[i] = (WriteString(strings, entry.Path, i, "path"), WriteString(strings, entry.Title, i, "title"));
        }

        int total = stringTable + (int)strings.Length;
        if (total > ushort.MaxValue)
            throw new TlstFormatException($"Tracklist is too big for the format ({total} bytes, max {ushort.MaxValue}). Remove some songs or shorten titles.");

        var data = new byte[total];
        Magic.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)Entries.Count);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), (ushort)total);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10), (ushort)stringTable);

        for (int i = 0; i < Entries.Count; i++)
        {
            TlstEntry entry = Entries[i];
            Span<byte> e = data.AsSpan(HeaderSize + i * EntrySize, EntrySize);
            BinaryPrimitives.WriteUInt32BigEndian(e, entry.SongId);
            BinaryPrimitives.WriteInt16BigEndian(e[4..], entry.Delay);
            e[6] = entry.Volume;
            e[7] = entry.Frequency;
            BinaryPrimitives.WriteUInt16BigEndian(e[8..], offsets[i].Path);
            BinaryPrimitives.WriteUInt16BigEndian(e[10..], offsets[i].Title);
            BinaryPrimitives.WriteUInt16BigEndian(e[12..], entry.SongSwitch);
            e[14] = entry.DisableStockPinchRaw;
            e[15] = entry.HiddenFromTracklistRaw;
        }
        strings.ToArray().CopyTo(data, stringTable);
        return data;
    }

    /// <summary>Like <see cref="ToBytes"/> but returns an empty array when the tracklist can't be written (too big, bad text).</summary>
    internal byte[] ToBytesOrEmpty()
    {
        try { return ToBytes(); }
        catch (TlstFormatException) { return []; }
    }

    private static ushort WriteString(MemoryStream strings, string? value, int entry, string what)
    {
        if (value is null) return NoString;
        if (value.Contains('\0'))
            throw new TlstFormatException($"Song {entry + 1}: {what} contains a null character.");
        long offset = strings.Length;
        if (offset >= NoString)
            throw new TlstFormatException("Tracklist is too big for the format. Remove some songs or shorten titles.");
        byte[] bytes = Utf8.GetBytes(value);
        strings.Write(bytes);
        strings.WriteByte(0);
        return (ushort)offset;
    }

    /// <summary>Writes the file atomically: a temp file next to the target, then a replace, so a crash can't leave half a tracklist.</summary>
    public void Save(string path)
    {
        byte[] bytes = ToBytes();
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Lowest song ID at or above <see cref="TlstEntry.FirstCustomSongId"/> not used in this tracklist.</summary>
    public uint NextFreeCustomSongId()
    {
        var used = Entries.Select(e => e.SongId).ToHashSet();
        uint id = TlstEntry.FirstCustomSongId;
        while (used.Contains(id)) id++;
        return id;
    }
}
