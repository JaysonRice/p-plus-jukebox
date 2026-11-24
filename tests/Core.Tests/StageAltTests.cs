using System.Text;
using PPlusTracklistEditor.Core.Stages;
using Xunit.Abstractions;

namespace PPlusTracklistEditor.Core.Tests;

public class StageAltTests(ITestOutputHelper output)
{
    private static string RealStageDir => Path.GetFullPath(Path.Combine(RealData.BuildSound, "..", "stage"));

    /// <summary>An .asl laid out like the game's: header, entries from +8, names after them.</summary>
    internal static byte[] Asl(params (int Button, string Param)[] entries)
    {
        var names = new MemoryStream();
        var offsets = new List<int>();
        foreach (var (_, p) in entries) { offsets.Add((int)names.Length); names.Write(Encoding.ASCII.GetBytes(p + "\0")); }
        int strings = 8 + entries.Length * 4;
        var b = new byte[strings + names.Length];
        "ASLS"u8.CopyTo(b);
        b[4] = (byte)(entries.Length >> 8); b[5] = (byte)entries.Length;
        b[6] = (byte)(strings >> 8); b[7] = (byte)strings;
        for (int i = 0; i < entries.Length; i++)
        {
            int at = 8 + i * 4;
            b[at] = (byte)(entries[i].Button >> 8); b[at + 1] = (byte)entries[i].Button;
            b[at + 2] = (byte)(offsets[i] >> 8); b[at + 3] = (byte)offsets[i];
        }
        names.ToArray().CopyTo(b, strings);
        return b;
    }

    internal static byte[] Param(string tracklist)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes("STEX"));
        b.AddRange([0, 0, 0, 0x2C]);
        while (b.Count < 0x2C) b.Add(0);
        b.AddRange(Encoding.ASCII.GetBytes(tracklist + "\0STGTEST\0st_test.rel\0"));
        return [.. b];
    }

    internal static void WriteStageFolder(string stageDir)
    {
        Directory.CreateDirectory(Path.Combine(stageDir, "stageslot"));
        Directory.CreateDirectory(Path.Combine(stageDir, "stageinfo"));
        File.WriteAllBytes(Path.Combine(stageDir, "stageslot", "01.asl"),
            Asl((0, "Battlefield"), (0x20, "Battlefield_R"), (0x40, "Battlefield_L"), (0x4000, "Battlefield_MR_00"), (0xC000, "Event_10")));
        foreach (var (param, tlst) in new[] { ("Battlefield", "Battlefield"), ("Battlefield_R", "Battlefield_Lunar"),
                                              ("Battlefield_L", "Battlefield_Melee"), ("Battlefield_MR_00", "Battlefield_64") })
            File.WriteAllBytes(Path.Combine(stageDir, "stageinfo", param + ".param"), Param(tlst));
    }

    [Fact]
    public void ReadsVersionsAndTheirSongLists()
    {
        using var t = new TempSetup();
        string stage = Path.Combine(t.Root, "stage");
        WriteStageFolder(stage);
        File.WriteAllBytes(Path.Combine(stage, "stageslot", "02.asl"), "junk"u8.ToArray());   // unreadable: left out

        var table = StageAltTable.FromStageFolder(stage);
        Assert.Equal(1, table.Count);
        var bf = table.For(1);
        Assert.Equal(["Default", "Hold R", "Hold L", "R alt menu", "Special"], bf.Select(a => a.HowToPick));
        Assert.Equal(["Battlefield", "Battlefield_Lunar", "Battlefield_Melee", "Battlefield_64", null], bf.Select(a => a.Tracklist));
        Assert.Equal(0, bf[3].MenuIndex);
        Assert.Empty(table.For(2));

        string saved = Path.Combine(t.Root, "alts.txt");                                    // the library's copy
        table.Save(saved);
        Assert.Equal(bf, StageAltTable.Load(saved).For(1));
        Assert.Equal(0, StageAltTable.Load(Path.Combine(t.Root, "missing.txt")).Count);
    }

    [Fact]
    public void Library_KeepsTheVersionTable()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        WriteStageFolder(t.ProjectPlus.StageDir);
        var lib = t.CreateLibrary();
        Assert.Equal("Battlefield_Melee", lib.LoadStageAlts().For(1)[2].Tracklist);

        File.Delete(lib.StageAltsFile);                                                     // older libraries get it on open
        Assert.Equal(5, Library.AppLibrary.Open(lib.Root).LoadStageAlts().For(1).Count);
    }

    [Fact]
    public void RealBuild_EveryVersionHasASongList()
    {
        if (!Directory.Exists(Path.Combine(RealStageDir, "stageslot"))) { output.WriteLine("Skipped: no build"); return; }
        var table = StageAltTable.FromStageFolder(RealStageDir);
        output.WriteLine($"{table.Count} slots");
        Assert.True(table.Count >= 60);
        Assert.Equal("Battlefield_64", table.For(1).First(a => a.Kind == StageAltKind.RMenu).Tracklist);
        Assert.Equal("Battlefield_Melee", table.For(1).First(a => a.Kind == StageAltKind.HoldL).Tracklist);
        Assert.All(Enumerable.Range(0, 256).SelectMany(table.For), a => Assert.NotNull(a.Tracklist));
    }
}
