using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Stages;
using PPlusTracklistEditor.Core.Sync;
using Xunit.Abstractions;

namespace PPlusTracklistEditor.Core.Tests;

public class StageListTests(ITestOutputHelper output)
{
    private static string ProjectPlusRoot => Path.GetFullPath(Path.Combine(RealData.BuildSound, "..", ".."));
    private const string TussleSwitch = @"C:\Users\jrice\Desktop\Tussle Build\Project+\pf\stage\switch";

    // ---------- the file format ----------

    [Fact]
    public void Reads_OnAndHazards_AndSavesUneditedFilesUnchanged()
    {
        var file = StageListFile.FromBytes(TempSetup.Rss((1, true, true), (2, false, false), (3, true, false)));

        Assert.Equal([1, 2, 3], file.Entries.Select(e => e.SlotId));
        Assert.Equal([1, 3], file.OnStages.Select(e => e.SlotId));
        Assert.True(file.HasHazards(file.Entries[0]));
        Assert.False(file.HasHazards(file.Entries[2]));
        Assert.Equal(1, file.PageCount);
        Assert.Equal(TempSetup.Rss((1, true, true), (2, false, false), (3, true, false)), file.ToBytes());
    }

    [Fact]
    public void Editing_ChangesOnlyThatStagesBits()
    {
        byte[] original = TempSetup.Rss((1, true, true), (2, false, false), (3, true, false));
        var file = StageListFile.FromBytes(original);

        file.SetOn(file.Entries[1], true);
        file.SetHazards(file.Entries[0], false);
        Assert.Equal([1, 2, 3], file.OnStages.Select(e => e.SlotId));
        Assert.False(file.HasHazards(file.Entries[0]));
        byte[] edited = file.ToBytes();
        int[] changed = Enumerable.Range(0, 800).Where(i => edited[i] != original[i]).ToArray();
        Assert.All(changed, i => Assert.True(i is >= 2 and <= 5 or >= 0x20 and <= 0x23, $"byte 0x{i:X} changed")); // only the masks

        file.SetOn(file.Entries[1], false);
        file.SetHazards(file.Entries[0], true);
        Assert.Equal(original, file.ToBytes());                                      // and back again exactly
    }

    [Fact]
    public void AddingAStage_StartsHazardsOff_ExceptFountainOfDreams()
    {
        var file = StageListFile.FromBytes(TempSetup.Rss((1, false, true), (StageListFile.FountainOfDreamsSlot, false, false), (3, true, true)));

        file.SetOnWithDefaultHazards(file.Entries[0], true);
        file.SetOnWithDefaultHazards(file.Entries[1], true);
        file.SetOnWithDefaultHazards(file.Entries[2], false);

        Assert.False(file.HasHazards(file.Entries[0]));
        Assert.True(file.HasHazards(file.Entries[1]));
        Assert.True(file.HasHazards(file.Entries[2]));   // taking a stage out leaves its setting alone
    }

    [Fact]
    public void RejectsFilesThatArentStageLists()
    {
        Assert.Throws<StageListFormatException>(() => StageListFile.FromBytes(new byte[799]));
        var bad = new byte[800];
        bad[0x3C] = 40; // more stages on a page than the masks can hold
        Assert.Throws<StageListFormatException>(() => StageListFile.FromBytes(bad));
    }

    [Fact]
    public void StageNames_ParseTheGamesList()
    {
        var names = StageSlotNames.Parse(["01 - Battlefield", "2B - Shadow Moses Island (written as Shadow Moses)", "junk line", "FF - "]);
        Assert.Equal("Battlefield", names.NameOf(0x01));
        Assert.Equal("Shadow Moses Island", names.NameOf(0x2B));
        Assert.Equal("Stage slot 7C", names.NameOf(0x7C));
    }

    // ---------- real presets ----------

    [Fact]
    public void RealPresets_RoundTripByteForByte_AndDecodeAsDocumented()
    {
        string switchDir = Path.Combine(ProjectPlusRoot, "pf", "stage", "switch");
        if (!Directory.Exists(switchDir)) { output.WriteLine("Skipped: no P+ build here"); return; }
        var names = StageSlotNames.Load(Path.Combine(ProjectPlusRoot, "pf", "stage", "stageslot", "Stageslot IDs.txt"));
        var dirs = new[] { switchDir, TussleSwitch }.Where(Directory.Exists);
        foreach (string file in dirs.SelectMany(d => Directory.GetFiles(d, "Switch*.rss")))
        {
            byte[] bytes = File.ReadAllBytes(file);
            var list = StageListFile.FromBytes(bytes);
            Assert.Equal(bytes, list.ToBytes());
            output.WriteLine($"{file}: {string.Join(", ", list.OnStages.Select(e => names.NameOf(e.SlotId) + (list.HasHazards(e) ? " (H)" : "")))}");
        }

        // Switch04 = Midwest (MODDING_NOTES): GHZ, FoD (hazards), Frigate, Dream Land, ToT, Smashville, BF, PS2, LM
        var midwest = StageListFile.Load(Path.Combine(switchDir, "Switch04.rss"));
        Assert.Equal(["Green Hill Zone", "Fountain of Dreams", "Frigate Husk", "Dream Land", "Temple of Time", "Smashville", "Battlefield", "Pokemon Stadium 2", "Luigi's Mansion"],
                     midwest.OnStages.Where(e => e.Page == 0).Select(e => names.NameOf(e.SlotId)));
        Assert.Equal(["Fountain of Dreams"], midwest.OnStages.Where(midwest.HasHazards).Select(e => names.NameOf(e.SlotId)));
    }

    // ---------- library and sync ----------

    [Fact]
    public void Library_CopiesPresets_NewBuildsGetTheirOwnCopy_AndSavingKeepsABackup()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        t.StockStageLists();
        var lib = t.CreateLibrary();

        Assert.Equal(Enumerable.Range(0, 8), lib.DefaultBuild.StageListPresets());
        Assert.Equal("Final Destination", lib.StageNames.NameOf(2));

        var ky = lib.NewBuild("Kentucky");
        var list = ky.OpenStageList(1);
        list.SetOn(list.Entries[2], true);
        ky.SaveStageList(1, list);

        Assert.Equal(2, ky.OpenStageList(1).OnStages.Count());                         // it had 1 on; now 2
        Assert.Single(lib.DefaultBuild.OpenStageList(1).OnStages);                    // the Default build's copy is untouched
        Assert.Single(Directory.GetFiles(Path.Combine(lib.BackupDir, ky.Id), "Switch01.*.rss"));
    }

    [Fact]
    public void OlderLibraries_GetPresetsWhenOpened()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();                                                  // made before the Project+ folder had stage lists
        Assert.Empty(lib.DefaultBuild.StageListPresets());

        t.StockStageLists();
        var reopened = AppLibrary.Open(t.LibraryRoot);

        Assert.Equal(8, reopened.DefaultBuild.StageListPresets().Count);
        Assert.Equal("Battlefield", reopened.StageNames.NameOf(1));
    }

    [Fact]
    public void Sync_WritesOnlyPresetsThatDiffer()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        t.StockStageLists();
        var lib = t.CreateLibrary();
        var ky = lib.NewBuild("Kentucky");
        Assert.Empty(BuildSync.Plan(ky, t.ProjectPlus).StageListsToWrite);           // same as the card

        var list = ky.OpenStageList(3);
        list.SetHazards(list.Entries[0], true);
        ky.SaveStageList(3, list);
        var plan = BuildSync.Plan(ky, t.ProjectPlus);
        Assert.Equal([3], plan.StageListsToWrite);
        Assert.Contains("Switch03", plan.Summary());

        BuildSync.Execute(plan);
        Assert.Equal(File.ReadAllBytes(ky.StageListPath(3)), File.ReadAllBytes(Path.Combine(t.StageListDir, "Switch03.rss")));
        Assert.Empty(BuildSync.Plan(ky, t.ProjectPlus).StageListsToWrite);
    }

    [Fact]
    public void Sync_SameSizeSongWithAnotherDate_IsCheckedNotCopied()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        var ky = lib.NewBuild("Kentucky");
        string onCard = Path.Combine(t.Sound, "strm", "Zelda", "Temple of Time 1.brstm");
        string onCard2 = Path.Combine(t.Sound, "strm", "Zelda", "Temple of Time 2.brstm");
        File.SetLastWriteTimeUtc(onCard, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));   // same bytes, other date
        File.WriteAllBytes(onCard2, [.. "RSTM"u8, 7, 7, 7]);                                      // same size, other bytes
        File.SetLastWriteTimeUtc(onCard2, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var plan = BuildSync.Plan(ky, t.ProjectPlus);
        Assert.Empty(plan.SongsToCopy);
        Assert.Equal(["Zelda/Temple of Time 1", "Zelda/Temple of Time 2"], plan.SongsToCheck);
        Assert.Contains("Check 2 songs", plan.Summary());

        BuildSync.Execute(plan);
        Assert.Equal(File.GetLastWriteTimeUtc(lib.Songs.FileFor("Zelda/Temple of Time 1")), File.GetLastWriteTimeUtc(onCard)); // date fixed
        Assert.Equal(File.ReadAllBytes(lib.Songs.FileFor("Zelda/Temple of Time 2")), File.ReadAllBytes(onCard2));          // different: copied
        Assert.False(BuildSync.Plan(ky, t.ProjectPlus).HasChanges);                                                          // next time: nothing
    }
}
