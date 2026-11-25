using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Sync;
using PPlusTracklistEditor.Core.Tlst;
using static PPlusTracklistEditor.Core.Tests.TempSetup;

namespace PPlusTracklistEditor.Core.Tests;

public class ProjectPlusUpdateTests
{
    private static (TempSetup T, AppLibrary Lib) Setup()
    {
        var t = new TempSetup();
        t.StockProjectPlus();
        t.StockStageLists();
        File.WriteAllText(Path.Combine(t.ProjectPlusRoot, "info.xml"), "<root><game><version>3.2</version></game></root>");
        Directory.CreateDirectory(Path.Combine(t.SdCard, "apps", "project+"));
        File.WriteAllText(Path.Combine(t.SdCard, "apps", "project+", "meta.xml"), "<app><version>3.2.1</version></app>");
        return (t, t.CreateLibrary());
    }

    [Fact]
    public void TheSameProjectPlus_OrOneTheAppSyncedTo_HasNothingNew()
    {
        var (t, lib) = Setup();
        using var _ = t;
        Assert.Equal("3.2.1", lib.Info.ProjectPlusVersion);
        Assert.False(ProjectPlusUpdate.Check(lib, t.ProjectPlus).HasChanges);

        // The user's build on the card (other songs, another preset) is theirs, not a Project+ change.
        Build mine = lib.UserBuilds().Single();
        var s = mine.OpenStage("Temple_of_Time");
        s.Remove(s.Tracklist.Entries[0]);
        s.Save();
        File.WriteAllBytes(mine.StageListPath(2), Rss((1, true, true)));
        BuildSync.Execute(BuildSync.Plan(mine, t.ProjectPlus));
        Assert.False(ProjectPlusUpdate.Check(lib, t.ProjectPlus).HasChanges);

        // Also without a sync record (synced before the app kept them): equal to a build's copy = theirs.
        Directory.Delete(Path.Combine(lib.Root, "syncs"), recursive: true);
        Assert.False(ProjectPlusUpdate.Check(lib, t.ProjectPlus).HasChanges);

        // Same version, but the card has an older sync of the build (edited since) and a leftover file: still the user's.
        t.SdTracklist("Temple_of_Time", Song(0xF000, "Zelda/Temple of Time 1"), Song(0xF001, "Zelda/Temple of Time 2"));
        t.SdTracklist("Temple", Song(0xF000, "Zelda/Temple of Time 1"));
        Assert.False(ProjectPlusUpdate.Check(lib, t.ProjectPlus).HasChanges);
    }

    [Fact]
    public void AProjectPlusUpdate_RefreshesTheOriginal_ButNotTheBuilds()
    {
        var (t, lib) = Setup();
        using var _ = t;
        Build mine = lib.UserBuilds().Single();
        byte[] myTemple = File.ReadAllBytes(mine.TracklistPath("Temple_of_Time"));
        byte[] oldOriginalTemple = File.ReadAllBytes(lib.DefaultBuild.TracklistPath("Temple_of_Time"));

        // "3.2.2": a song added to a stock stage, a new stage with its song, a changed preset, new stage names.
        t.SdSong("Zelda/Temple of Time 3");
        t.SdTracklist("Temple_of_Time", Song(0xF000, "Zelda/Temple of Time 1"), Song(0xF001, "Zelda/Temple of Time 3"));
        t.SdSong("Pokemon/Radiant Stadium");
        t.SdTracklist("Radiant_Stadium", Song(0xF000, "Pokemon/Radiant Stadium"));
        File.WriteAllBytes(Path.Combine(t.StageListDir, "Switch07.rss"), Rss((1, true, false), (2, true, false), (3, true, false)));
        File.AppendAllLines(Path.Combine(t.ProjectPlusRoot, "pf", "stage", "stageslot", "Stageslot IDs.txt"), ["04 - Radiant Stadium"]);
        File.WriteAllText(Path.Combine(t.SdCard, "apps", "project+", "meta.xml"), "<app><version>3.2.2</version></app>");

        var changes = ProjectPlusUpdate.Check(lib, t.ProjectPlus);
        Assert.Equal(["Temple_of_Time"], changes.ChangedStages);
        Assert.Equal(["Radiant_Stadium"], changes.NewStages);
        Assert.Equal([7], changes.ChangedPresets);
        Assert.Equal(["Pokemon/Radiant Stadium", "Zelda/Temple of Time 3"], changes.NewSongs);
        Assert.True(changes.StageNamesChanged);
        Assert.Contains("version 3.2.2; the app was set up from 3.2.1", changes.Summary());

        string backup = ProjectPlusUpdate.Apply(lib, changes);

        Assert.Equal(2, lib.DefaultBuild.OpenStage("Temple_of_Time").Tracklist.Entries.Count);       // the original follows Project+
        Assert.Equal(myTemple, File.ReadAllBytes(mine.TracklistPath("Temple_of_Time")));             // the build keeps its songs
        Assert.True(File.Exists(mine.TracklistPath("Radiant_Stadium")));                             // new stages join the builds
        Assert.True(lib.Songs.Has("Zelda/Temple of Time 3") && lib.Songs.Has("Pokemon/Radiant Stadium"));
        Assert.Contains("Radiant_Stadium", lib.KnownStages);
        Assert.Equal(oldOriginalTemple, File.ReadAllBytes(Path.Combine(backup, "original", "tracklist", "Temple_of_Time.tlst")));
        Assert.True(File.Exists(Path.Combine(backup, "stagenames.txt")));
        Assert.Equal("3.2.2", AppLibrary.Open(lib.Root).Info.ProjectPlusVersion);
        Assert.True(lib.DefaultBuild.OpenStage("Temple_of_Time").CanResetToDefault == false);       // the original itself has no "reset"
        Assert.True(mine.OpenStage("Temple_of_Time").CanResetToDefault);                              // a build can reset to the new songs
        Assert.False(ProjectPlusUpdate.Check(lib, t.ProjectPlus).HasChanges);                         // done: nothing new anymore
    }

    [Fact]
    public void VersionsOnlyDifferWhenTheyReallyDo()
    {
        Assert.True(ProjectPlusChanges.SameVersion("3.2", "3.2.1"));       // the launcher's info.xml vs the Homebrew Channel's meta.xml
        Assert.True(ProjectPlusChanges.SameVersion("3.2.1", "3.2.1"));
        Assert.False(ProjectPlusChanges.SameVersion("3.2.1", "3.2.2"));
        Assert.False(ProjectPlusChanges.SameVersion("3.2", "3.21"));
    }
}
