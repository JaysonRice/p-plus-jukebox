using System.Buffers.Binary;
using PPlusTracklistEditor.Core.Tlst;
using Xunit.Abstractions;

namespace PPlusTracklistEditor.Core.Tests;

public class TlstFileTests(ITestOutputHelper output)
{
    // ---------- Real files ----------

    [Fact]
    public void EveryRealTracklist_RoundTripsByteForByte()
    {
        var files = RealData.AllTracklists().ToList();
        if (files.Count == 0) { output.WriteLine("No real tracklists on this machine; skipped."); return; }

        var failures = new List<string>();
        foreach (string path in files)
        {
            byte[] original = File.ReadAllBytes(path);
            try
            {
                byte[] written = TlstFile.Read(original).ToBytes();
                if (!written.AsSpan().SequenceEqual(original))
                    failures.Add($"{path}: differs (original {original.Length} bytes, written {written.Length})");
            }
            catch (TlstFormatException ex) { failures.Add($"{path}: {ex.Message}"); }
        }
        output.WriteLine($"Round-tripped {files.Count} tracklists, {failures.Count} failures.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void RealFileWithJunkAfterHeaderSize_ReadsAndDropsJunk()
    {
        if (!File.Exists(RealData.JunkTrailingTlst)) { output.WriteLine("Card backup not present; skipped."); return; }
        byte[] original = File.ReadAllBytes(RealData.JunkTrailingTlst);

        var tlst = TlstFile.Read(original);

        Assert.Equal(11, tlst.IgnoredTrailingBytes);
        int headerSize = BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(8));
        Assert.Equal(original.AsSpan(0, headerSize).ToArray(), tlst.ToBytes());
    }

    // ---------- Writer / reader behavior ----------

    private static TlstFile Sample() => new()
    {
        Entries =
        {
            new TlstEntry { SongId = 0x26FC, Volume = 80, Frequency = 40, Title = "Battlefield" },          // disc song
            new TlstEntry { SongId = 0xF000, Volume = 75, Frequency = 80, Delay = -1,
                            Path = "Final Fantasy/Paradigm Shift", Title = "Paradigm Shift" },
            new TlstEntry { SongId = 0xF001, Volume = 60, Frequency = 10, Path = "Pokemon/Pokémon Center",
                            Title = "Pokémon Center", SongSwitch = 3600, HiddenFromTracklistRaw = 1 },
            new TlstEntry { SongId = 0x2783, Volume = 80, Frequency = 40 },                                 // no path, no title
        }
    };

    [Fact]
    public void WriteThenRead_PreservesEveryField()
    {
        var original = Sample();

        var reread = TlstFile.Read(original.ToBytes());

        Assert.Equal(original.Entries.Count, reread.Entries.Count);
        for (int i = 0; i < original.Entries.Count; i++)
        {
            TlstEntry a = original.Entries[i], b = reread.Entries[i];
            Assert.Equal(a.SongId, b.SongId);
            Assert.Equal(a.Delay, b.Delay);
            Assert.Equal(a.Volume, b.Volume);
            Assert.Equal(a.Frequency, b.Frequency);
            Assert.Equal(a.Path, b.Path);
            Assert.Equal(a.Title, b.Title);
            Assert.Equal(a.SongSwitch, b.SongSwitch);
            Assert.Equal(a.DisableStockPinchRaw, b.DisableStockPinchRaw);
            Assert.Equal(a.HiddenFromTracklistRaw, b.HiddenFromTracklistRaw);
        }
    }

    [Fact]
    public void Write_LaysOutHeaderAndStringsLikeTheGameExpects()
    {
        byte[] data = Sample().ToBytes();

        Assert.Equal("TLST"u8.ToArray(), data[..4]);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)));
        Assert.Equal(data.Length, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8)));
        int stringTable = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));
        Assert.Equal(TlstFile.HeaderSize + 4 * TlstFile.EntrySize, stringTable);

        // Disc song: no path (0xFFFF), title first in the string table.
        Span<byte> first = data.AsSpan(TlstFile.HeaderSize, TlstFile.EntrySize);
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(first[8..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(first[10..]));

        // Delay -1 is stored as 0xFFFF.
        Span<byte> second = data.AsSpan(TlstFile.HeaderSize + TlstFile.EntrySize, TlstFile.EntrySize);
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(second[4..]));

        // Entry with neither path nor title.
        Span<byte> last = data.AsSpan(TlstFile.HeaderSize + 3 * TlstFile.EntrySize, TlstFile.EntrySize);
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(last[8..]));
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(last[10..]));
    }

    [Fact]
    public void EditingEntries_ProducesAValidFile()
    {
        var tlst = Sample();
        tlst.Entries.RemoveAt(0);
        tlst.Entries.Reverse();
        tlst.Entries.Add(new TlstEntry { SongId = tlst.NextFreeCustomSongId(), Volume = 90, Frequency = 40,
                                         Path = "Celeste/Confronting Myself", Title = "Confronting Myself" });

        var reread = TlstFile.Read(tlst.ToBytes());

        Assert.Equal(["(none)", "Pokémon Center", "Paradigm Shift", "Confronting Myself"],
                     reread.Entries.Select(e => e.Title ?? "(none)"));
        Assert.Equal(0xF002u, reread.Entries[^1].SongId);
    }

    [Fact]
    public void EmptyTracklist_RoundTrips()
    {
        var reread = TlstFile.Read(new TlstFile().ToBytes());
        Assert.Empty(reread.Entries);
    }

    [Fact]
    public void NextFreeCustomSongId_FillsGapsAndIgnoresDiscIds()
    {
        var tlst = new TlstFile { Entries = { new() { SongId = 0x26FC }, new() { SongId = 0xF000 }, new() { SongId = 0xF002 } } };
        Assert.Equal(0xF001u, tlst.NextFreeCustomSongId());
        Assert.Equal(0xF000u, new TlstFile().NextFreeCustomSongId());
    }

    // ---------- Bad input ----------

    [Fact]
    public void Read_RejectsNonTracklist() =>
        Assert.Throws<TlstFormatException>(() => TlstFile.Read("RSTM\0\0\0\0\0\0\0\0"u8));

    [Fact]
    public void Read_RejectsTruncatedFile()
    {
        byte[] data = Sample().ToBytes();
        Assert.Throws<TlstFormatException>(() => TlstFile.Read(data.AsSpan(0, data.Length - 5)));
    }

    [Fact]
    public void Read_RejectsStringOffsetOutsideFile()
    {
        byte[] data = Sample().ToBytes();
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(TlstFile.HeaderSize + 10), 0x7FFF); // first entry's title
        Assert.Throws<TlstFormatException>(() => TlstFile.Read(data));
    }

    [Fact]
    public void Read_RejectsInvalidUtf8InsteadOfMangling()
    {
        var tlst = new TlstFile { Entries = { new() { SongId = 0xF000, Title = "AB" } } };
        byte[] data = tlst.ToBytes();
        int stringTable = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));
        data[stringTable] = 0xFF; // not valid UTF-8
        Assert.Throws<TlstFormatException>(() => TlstFile.Read(data));
    }

    [Fact]
    public void Write_RejectsTracklistTooBigForFormat()
    {
        var tlst = new TlstFile();
        for (int i = 0; i < 300; i++)
            tlst.Entries.Add(new TlstEntry { SongId = (uint)(0xF000 + i), Title = new string('x', 250) });
        Assert.Throws<TlstFormatException>(() => tlst.ToBytes());
    }

    [Fact]
    public void Save_WritesFileAndLeavesNoTempBehind()
    {
        string dir = Directory.CreateTempSubdirectory("tlst-test").FullName;
        try
        {
            string path = Path.Combine(dir, "Battlefield.tlst");
            Sample().Save(path);

            Assert.Equal(Sample().ToBytes(), File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
