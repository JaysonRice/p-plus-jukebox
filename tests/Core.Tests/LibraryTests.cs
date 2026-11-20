using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Tlst;
using static PPlusTracklistEditor.Core.Tests.TempSetup;

namespace PPlusTracklistEditor.Core.Tests;

public class LibraryTests
{
    // ---------- Finding the Project+ folder ----------

    [Theory]
    [InlineData("Project+")]                 // what we ask for
    [InlineData("")]                         // SD card root
    [InlineData(@"Project+\pf")]
    [InlineData(@"Project+\pf\sound")]
    public void ProjectPlusFolder_IsFoundFromWhateverTheUserPicks(string picked)
    {
        using var t = new TempSetup();
        var pplus = ProjectPlusFolder.Resolve(Path.Combine(t.SdCard, picked));
        Assert.Equal(t.ProjectPlusRoot, pplus.Root);
        Assert.Equal(t.Sound, pplus.SoundDir);
    }

    [Fact]
    public void ProjectPlusFolder_ExplainsAWrongPick()
    {
        using var t = new TempSetup();
        var ex = Assert.Throws<MusicLibraryException>(() => ProjectPlusFolder.Resolve(t.Downloads));
        Assert.Contains("Pick the \"Project+\" folder", ex.Message);
        Assert.Throws<MusicLibraryException>(() => ProjectPlusFolder.Resolve(Path.Combine(t.Root, "missing")));
    }

    // ---------- Where the library lives ----------

    [Fact]
    public void DefaultRoot_IsNextToTheApp_WhenWritable()
    {
        string root = AppLibrary.DefaultRoot(out bool fallback, appDir: @"D:\Apps\Editor", canWrite: _ => true);
        Assert.Equal(@"D:\Apps\Editor\Library", root);
        Assert.False(fallback);
    }

    [Fact]
    public void DefaultRoot_RealWriteCheck_LeavesNoFolderBehind()
    {
        using var t = new TempSetup();
        string root = AppLibrary.DefaultRoot(out bool fallback, appDir: t.Root);
        Assert.False(fallback);
        Assert.Equal(Path.Combine(t.Root, "Library"), root);
        Assert.False(Directory.Exists(root)); // first-run setup can tell it made the folder itself
    }

    [Fact]
    public void DefaultRoot_FallsBackToLocalAppData_WhenAppFolderIsReadOnly()
    {
        string root = AppLibrary.DefaultRoot(out bool fallback, appDir: @"C:\Program Files\Editor", canWrite: _ => false);
        Assert.True(fallback);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), root);
    }

    // ---------- First run ----------

    [Fact]
    public void FirstRun_ImportsAllSongsAndTheDefaultMapping()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var progress = new List<(int, int)>();

        var lib = AppLibrary.CreateFromProjectPlus(t.LibraryRoot, t.ProjectPlus, new SyncProgress(progress));

        Assert.Equal(["Super Mario/Overworld", "Super Mario/Overworld_b", "Zelda/Temple of Time 1", "Zelda/Temple of Time 2"],
                     lib.Songs.All().Order());
        Build def = Assert.Single(lib.Builds());
        Assert.True(def.IsDefault);
        Assert.Equal("Default", def.Name);
        Assert.Equal(["Mushroom_Kingdom", "Temple_of_Time"], def.TracklistNames().Order());
        Assert.Equal(File.ReadAllBytes(Path.Combine(t.Sound, "tracklist", "Temple_of_Time.tlst")),
                     File.ReadAllBytes(def.TracklistPath("Temple_of_Time")));
        Assert.Contains("Temple_of_Time", lib.KnownStages);
        Assert.Equal((4, 4), progress[^1]);

        // Reopening reads the same library back.
        var reopened = AppLibrary.Open(t.LibraryRoot);
        Assert.Equal(t.ProjectPlusRoot, reopened.Info.ProjectPlusFolder);
        Assert.Throws<MusicLibraryException>(() => AppLibrary.CreateFromProjectPlus(t.LibraryRoot, t.ProjectPlus)); // only once
    }

    [Fact]
    public void FirstRun_RefusesALibraryInsideProjectPlus()
    {
        using var t = new TempSetup();
        Assert.Throws<MusicLibraryException>(() => AppLibrary.CreateFromProjectPlus(Path.Combine(t.ProjectPlusRoot, "Library"), t.ProjectPlus));
    }

    // ---------- Builds ----------

    [Fact]
    public void NewBuild_CopiesTracklists_AndEditsStayInThatBuild()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();

        Build ky = lib.NewBuild("Kentucky Build");
        var session = ky.OpenStage("Temple_of_Time");
        session.Remove(session.Tracklist.Entries[0]);
        session.Save();

        Assert.Equal(["Default", "Kentucky Build"], lib.Builds().Select(b => b.Name));
        Assert.Equal(2, ky.OpenStage("Temple_of_Time").Tracklist.Entries.Count);
        Assert.Equal(3, lib.DefaultBuild.OpenStage("Temple_of_Time").Tracklist.Entries.Count);
        Assert.True(ky.Stages().Single(s => s.Name == "Temple_of_Time").ChangedFromDefault);
        Assert.False(ky.Stages().Single(s => s.Name == "Mushroom_Kingdom").ChangedFromDefault);
        Assert.Equal("Default", ky.Info.CopiedFrom);
    }

    [Fact]
    public void Builds_NeedUniqueNames_AndDefaultCantBeDeleted()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        Build tn = lib.NewBuild("Tennessee Build");

        Assert.Throws<MusicLibraryException>(() => lib.NewBuild("tennessee build"));
        Assert.Throws<MusicLibraryException>(() => lib.NewBuild("  "));
        Assert.Throws<MusicLibraryException>(() => lib.DeleteBuild(lib.DefaultBuild));

        Build copy = lib.NewBuild("Kentucky", from: tn);
        Assert.Equal("Tennessee Build", copy.Info.CopiedFrom);
        lib.DeleteBuild(tn);
        Assert.Equal(["Default", "Kentucky"], lib.Builds().Select(b => b.Name));
        copy.Rename("Kentucky Build");
        Assert.Equal("Kentucky Build", lib.GetBuild(copy.Id).Name);
    }

    // ---------- Songs in the library ----------

    [Fact]
    public void DeleteSong_IsBlockedWhileABuildUsesIt_ThenRemovesSongAndItsHurryUpVersion()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        Build ky = lib.NewBuild("Kentucky");

        var ex = Assert.Throws<MusicLibraryException>(() => lib.DeleteSong("Super Mario/Overworld"));
        Assert.Contains("Default, Kentucky", ex.Message);
        Assert.Equal(2, lib.BuildsUsing("Super Mario/Overworld_b").Count); // "_b" counts as its main song

        foreach (Build b in new[] { lib.DefaultBuild, ky })
        {
            var s = b.OpenStage("Mushroom_Kingdom");
            s.Remove(s.Tracklist.Entries.Single(e => e.Path == "Super Mario/Overworld"));
            s.Save();
        }
        Assert.Equal(["Super Mario/Overworld", "Super Mario/Overworld_b"], lib.UnusedSongs());

        lib.DeleteSong("Super Mario/Overworld");

        Assert.False(lib.Songs.Has("Super Mario/Overworld"));
        Assert.False(lib.Songs.Has("Super Mario/Overworld_b"));
        Assert.False(Directory.Exists(Path.Combine(lib.SongsDir, "Super Mario"))); // empty series folder removed
    }

    // ---------- Importing a music folder (e.g. a music repo) as a build ----------

    [Fact]
    public void ImportMusicFolder_SharesIdenticalSongs_RenamesClashes_AndOverlaysDefault()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();

        string repo = Path.Combine(t.Root, "Moist-PM-Music");
        string strm = Path.Combine(repo, "strm");
        t.Brstm(strm, "Zelda/Temple of Time 1");                 // identical to stock → shared
        t.Brstm(strm, "Super Mario/Overworld", content: 7);      // same name, different audio → renamed
        t.Brstm(strm, "Super Mario/Overworld_b", content: 7);    // its hurry-up file moves with it
        t.Brstm(strm, "Celeste/Confronting Myself");             // new
        Tracklist(Path.Combine(repo, "tracklist"), "Mushroom_Kingdom",
            Song(0xF000, "Super Mario/Overworld", "My Overworld"), Song(0xF001, "Celeste/Confronting Myself", "Celeste"));

        ImportResult result = lib.ImportMusicFolder(MusicFolder.Open(repo), "Moist");
        Build moist = result.Build;
        Assert.Equal(1, result.RenamedSongs);
        Assert.Equal(0, result.FixedPaths);

        Assert.True(lib.Songs.Has("Super Mario/Overworld (2)"));
        Assert.True(lib.Songs.Has("Super Mario/Overworld (2)_b"));
        Assert.Equal(1, File.ReadAllBytes(lib.Songs.FileFor("Super Mario/Overworld"))[4]);    // stock copy untouched
        Assert.Equal(7, File.ReadAllBytes(lib.Songs.FileFor("Super Mario/Overworld (2)"))[4]);
        Assert.False(lib.Songs.Has("Zelda/Temple of Time 1 (2)"));                           // identical → no duplicate

        Assert.Equal(["Super Mario/Overworld (2)", "Celeste/Confronting Myself"],
                     moist.OpenStage("Mushroom_Kingdom").Tracklist.Entries.Select(e => e.Path));
        Assert.Equal(3, moist.OpenStage("Temple_of_Time").Tracklist.Entries.Count);           // from Default
        Assert.Equal("Super Mario/Overworld",
                     lib.DefaultBuild.OpenStage("Mushroom_Kingdom").Tracklist.Entries[0].Path); // Default unchanged
    }

    private sealed class SyncProgress(List<(int, int)> log) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => log.Add(value);
    }

    // ---------- Loop Maker: songs straight into the library ----------

    [Fact]
    public void AddSong_CopiesIntoTheLibraryNow_WithASafeUniqueName_AndNoBuildUsesIt()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        t.Brstm(lib.SongsDir, "Kirby/Gourmet Race");

        string path = lib.AddSong(t.DownloadedBrstm("made.brstm"), "Gourmet Race", "Kirby");
        string loose = lib.AddSong(t.DownloadedBrstm("loose.brstm"), "Café Theme", "");

        Assert.Equal("Kirby/Gourmet Race (2)", path);
        Assert.Equal("Cafe Theme", loose);
        Assert.True(lib.Songs.Has(path));
        Assert.Contains(path, lib.UnusedSongs());
        Assert.True(File.Exists(Path.Combine(t.Downloads, "made.brstm"))); // copied, not moved

        string wav = Path.Combine(t.Downloads, "song.wav");
        File.WriteAllBytes(wav, "RIFF0000WAVE"u8.ToArray());
        Assert.Throws<MusicLibraryException>(() => lib.AddSong(wav, "Song", ""));
        Assert.Throws<MusicLibraryException>(() => lib.AddSong(t.DownloadedBrstm("x.brstm"), "  ", ""));
    }

    [Fact]
    public void FindDuplicate_SpotsTheSameFile_ButNotADifferentOne()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        string path = lib.AddSong(t.DownloadedBrstm("a.brstm", content: 5), "Song A", "Series");

        Assert.Equal(path, lib.FindDuplicate(t.DownloadedBrstm("again.brstm", content: 5)));   // same bytes, other name
        Assert.Null(lib.FindDuplicate(t.DownloadedBrstm("other.brstm", content: 6)));         // same size, different bytes
        Assert.Null(lib.FindDuplicate(Path.Combine(t.Downloads, "missing.brstm")));
    }
}
