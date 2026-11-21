using System.Diagnostics;
using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Menu;
using PPlusTracklistEditor.Core.Sync;
using Xunit.Abstractions;

namespace PPlusTracklistEditor.Core.Tests;

public class PresetNameTests(ITestOutputHelper output)
{
    private static string MenuFile => Path.GetFullPath(Path.Combine(RealData.BuildSound, "..", "menu2", "mu_menumain.pac"));

    [Fact]
    public void Lz11_RoundTrips()
    {
        var rng = new Random(5);
        byte[] data = new byte[200_000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 1000 < 300 ? rng.Next(256) : (i / 7) % 13); // noise + repeats
        byte[] packed = Lz11.Compress(data);
        Assert.True(packed.Length < data.Length);
        Assert.Equal(data, Lz11.Decompress(packed));
        Assert.Equal([], Lz11.Decompress(Lz11.Compress([])));
        Assert.Equal([7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 1], Lz11.Decompress(Lz11.Compress([7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 1])));
    }

    [Fact]
    public void MessageTable_RoundTrips()
    {
        var table = MessageTable.Parse(new byte[] { 0, 0, 0, 12, 0, 0, 0, 14, 0, 0, 0, 17, (byte)'h', (byte)'i', 0xC3, 0xA9, (byte)'!' });
        Assert.Equal(["hi", "é!"], table.Strings);
        Assert.Equal(new byte[] { 0, 0, 0, 12, 0, 0, 0, 14, 0, 0, 0, 17, (byte)'h', (byte)'i', 0xC3, 0xA9, (byte)'!' }, table.ToBytes());
    }

    [Theory]
    [InlineData("Moist Singles (L/R to swap preset)", true)]
    [InlineData("", false)]
    [InlineData(" Padded", false)]
    [InlineData("Pokémon", false)]
    [InlineData("This name is much too long for the little menu box", false)]
    public void Names_AreChecked(string name, bool ok) => Assert.Equal(ok, PresetNames.Problem(name) is null);

    [Fact]
    public void RealMenu_ReadsTheStockNames_AndRebuildsUnchanged()
    {
        if (!File.Exists(MenuFile)) { output.WriteLine("Skipped: no menu file"); return; }
        byte[] pac = File.ReadAllBytes(MenuFile);
        Assert.Equal(["Default (L/R to swap preset)", "PMBR", "2023 Proposed", "2024 Proposed", "Midwest", "Australia", "Japan", "All Stages"],
                     PresetNames.Read(pac));

        byte[] raw = Lz11.Decompress(pac);
        var outer = ArcArchive.Parse(raw);
        Assert.Equal(raw, outer.ToBytes());                                                // every byte of the archive survives a rebuild
        for (int i = 0; i < outer.Count; i++)
            if (outer.Data(i).AsSpan(0, 4).SequenceEqual("ARC\0"u8))
                Assert.Equal(outer.Data(i), ArcArchive.Parse(outer.Data(i)).ToBytes());   // and the nested ones too
    }

    [Fact]
    public void RealMenu_RenameChangesOnlyTheNames()
    {
        if (!File.Exists(MenuFile)) { output.WriteLine("Skipped: no menu file"); return; }
        byte[] pac = File.ReadAllBytes(MenuFile);
        string[] names = ["Moist Singles (L/R to swap preset)", "Moist Doubles", "2023 Proposed", "2024 Proposed", "Midwest", "Australia", "Japan", "All Stages"];

        var watch = Stopwatch.StartNew();
        byte[] renamed = PresetNames.Rename(pac, names);
        output.WriteLine($"Renamed in {watch.ElapsedMilliseconds} ms; compressed {pac.Length:N0} -> {renamed.Length:N0} bytes");

        Assert.Equal(names, PresetNames.Read(renamed));
        Assert.True(renamed.Length < pac.Length * 1.05, "the new file should be about as small as the original");
        var before = ArcArchive.Parse(Lz11.Decompress(pac));
        var after = ArcArchive.Parse(Lz11.Decompress(renamed));
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(1, Enumerable.Range(0, before.Count).Count(i => !before.Data(i).AsSpan().SequenceEqual(after.Data(i)))); // just the menu-text archive

        byte[] back = PresetNames.Rename(renamed, PresetNames.Read(pac).ToList());
        Assert.Equal(Lz11.Decompress(pac), Lz11.Decompress(back));                       // renaming back gives the original archive exactly
    }

    [Fact]
    public void Library_ReadsNames_AndSyncRenamesWithABackup()
    {
        if (!File.Exists(MenuFile)) { output.WriteLine("Skipped: no menu file"); return; }
        using var t = new TempSetup();
        t.StockProjectPlus();
        string cardMenu = Path.Combine(t.ProjectPlusRoot, "pf", "menu2", "mu_menumain.pac");
        Directory.CreateDirectory(Path.GetDirectoryName(cardMenu)!);
        File.Copy(MenuFile, cardMenu);                                                    // a copy: the real file is only read
        byte[] originalMenu = File.ReadAllBytes(cardMenu);
        var lib = t.CreateLibrary();
        var moist = lib.NewBuild("Moist");
        Assert.Equal("PMBR", moist.PresetNamesOrStock[1]);
        Assert.Null(BuildSync.Plan(moist, t.ProjectPlus).PresetNamesToWrite);    // same as the card: menu left alone

        moist.RenamePreset(1, "Moist Doubles");
        Assert.Throws<MusicLibraryException>(() => moist.RenamePreset(2, ""));
        Assert.Equal("PMBR", lib.DefaultBuild.PresetNamesOrStock[1]);                     // other builds keep theirs
        var plan = BuildSync.Plan(moist, t.ProjectPlus);
        Assert.NotNull(plan.PresetNamesToWrite);
        Assert.Contains("preset names", plan.Summary());

        BuildSync.Execute(plan);
        Assert.Equal("Moist Doubles", PresetNames.Read(cardMenu)[1]);
        Assert.Equal(originalMenu, File.ReadAllBytes(Path.Combine(lib.BackupDir, "menu", "mu_menumain.original.pac")));
        Assert.False(BuildSync.Plan(moist, t.ProjectPlus).HasChanges);
        Assert.NotNull(BuildSync.Plan(lib.DefaultBuild, t.ProjectPlus).PresetNamesToWrite); // Default puts "PMBR" back
    }

    [Fact]
    public void FilesWithoutPresetNames_AreRefused()
    {
        var notArc = Lz11.Compress("hello world, this is not an archive"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => PresetNames.Read(notArc));
        Assert.Throws<InvalidDataException>(() => PresetNames.Read("plain"u8.ToArray()));
    }
}
