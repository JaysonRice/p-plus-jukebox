using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Tlst;
using Xunit.Abstractions;
using static PPlusTracklistEditor.Core.Tests.TempMusicFolders;

namespace PPlusTracklistEditor.Core.Tests;

public class MusicLibraryTests(ITestOutputHelper output)
{
    [Fact]
    public void Open_ExplainsWhatsWrongWithANonMusicFolder()
    {
        using var t = new TempMusicFolders();
        var ex = Assert.Throws<MusicLibraryException>(() => MusicFolder.Open(t.Downloads));
        Assert.Contains("strm", ex.Message);
        Assert.Throws<MusicLibraryException>(() => MusicFolder.Open(Path.Combine(t.Root, "nope")));
    }

    [Fact]
    public void Library_RefusesSameFolderForMusicAndStock()
    {
        using var t = new TempMusicFolders();
        Assert.Throws<MusicLibraryException>(() => new MusicLibrary(MusicFolder.Open(t.MusicRoot), MusicFolder.Open(t.MusicRoot)));
    }

    [Fact]
    public void Stages_MergesMusicAndStock()
    {
        using var t = new TempMusicFolders();
        t.Tracklist(t.MusicRoot, "Battlefield");
        t.Tracklist(t.MusicRoot, "Battlefield_DPAD");          // not in the build
        t.Tracklist(t.StockRoot, "Battlefield");
        t.Tracklist(t.StockRoot, "Gateway_Colosseum");         // stock only

        var stages = t.Library().Stages().ToDictionary(s => s.Name);

        Assert.Equal(3, stages.Count);
        Assert.True(stages["Battlefield"] is { HasCustom: true, HasStock: true, UsedByBuild: true, IsStockOnly: false });
        Assert.True(stages["Battlefield_DPAD"] is { HasCustom: true, HasStock: false, UsedByBuild: false });
        Assert.True(stages["Gateway_Colosseum"].IsStockOnly);
        Assert.Equal("Battlefield (D-pad)", stages["Battlefield_DPAD"].DisplayName);
        Assert.Null(t.Library(withStock: false).Stages().Single(s => s.Name == "Battlefield").UsedByBuild);
    }

    [Fact]
    public void Locate_PrefersMusicFolderThenStockThenMissing()
    {
        using var t = new TempMusicFolders();
        t.FakeBrstm(t.MusicRoot, "Mario/Overworld");
        t.FakeBrstm(t.StockRoot, "Mario/Overworld");
        t.FakeBrstm(t.StockRoot, "Pokemon/Gateway Colosseum");
        var lib = t.Library();

        Assert.Equal(SongSource.Disc, lib.Locate(Song(0x26FC, null)).Source);
        Assert.Equal(SongSource.MusicFolder, lib.Locate(Song(0xF000, "Mario/Overworld")).Source);
        Assert.Equal(SongSource.StockBuild, lib.Locate(Song(0xF000, "Pokemon/Gateway Colosseum")).Source);
        Assert.Equal(SongSource.Missing, lib.Locate(Song(0xF000, "Nope/Nothing")).Source);
        Assert.Equal(SongSource.Disc, lib.Locate(Song(0xF000, "Z01")).Source);     // disc song by disc file name
        Assert.Equal(SongSource.Missing, lib.Locate(Song(0xF000, "z01")).Source);
        Assert.Equal(SongSource.Missing, lib.Locate(Song(0xF000, "Mario/Z01")).Source);
    }

    [Fact]
    public void DisplayTitle_UsesTitleThenDiscNameThenFileName()
    {
        Assert.Equal("My Title", MusicLibrary.DisplayTitle(Song(0x26FC, null, "My Title")));
        Assert.Equal("Battlefield", MusicLibrary.DisplayTitle(Song(0x26FC, null)));
        Assert.Equal("Paradigm Shift", MusicLibrary.DisplayTitle(Song(0xF000, "Final Fantasy/Paradigm Shift")));
        Assert.Equal("Unnamed song", MusicLibrary.DisplayTitle(Song(0x1, null)));
    }

    [Fact]
    public void AddSong_QueuesUntilSave_ThenCopiesWithSafeUniqueName()
    {
        using var t = new TempMusicFolders();
        t.Tracklist(t.MusicRoot, "Battlefield", Song(0xF000, "Final Fantasy/Paradigm Shift", "Paradigm Shift"));
        t.FakeBrstm(t.MusicRoot, "Final Fantasy/Paradigm Shift");
        t.FakeBrstm(t.MusicRoot, "Pokemon/Pokemon Center");      // name already taken
        var session = t.Library().OpenStage("Battlefield");
        Assert.False(session.IsDirty);

        TlstEntry added = session.AddSong(t.DownloadedBrstm("dl.brstm"), "Pokémon Center", "Pokémon");
        TlstEntry again = session.AddSong(t.DownloadedBrstm("dl2.brstm"), "Pokémon Center", "Pokémon");

        Assert.Equal("Pokemon/Pokemon Center (2)", added.Path);
        Assert.Equal("Pokemon/Pokemon Center (3)", again.Path);  // also avoids the queued one
        Assert.Equal("Pokémon Center", added.Title);              // title keeps the accent; file name doesn't
        Assert.Equal(0xF001u, added.SongId);
        Assert.Equal(0xF002u, again.SongId);
        Assert.True(session.IsDirty);
        Assert.False(File.Exists(Path.Combine(t.MusicRoot, "strm", "Pokemon", "Pokemon Center (2).brstm"))); // nothing copied yet
        Assert.Equal(SongSource.MusicFolder, session.Locate(added).Source);  // but it counts as present

        session.Save();

        Assert.True(File.Exists(Path.Combine(t.MusicRoot, "strm", "Pokemon", "Pokemon Center (2).brstm")));
        var saved = TlstFile.Load(Path.Combine(t.MusicRoot, "tracklist", "Battlefield.tlst"));
        Assert.Equal(["Paradigm Shift", "Pokémon Center", "Pokémon Center"], saved.Entries.Select(e => e.Title));
        Assert.False(session.IsDirty);
        Assert.Single(Directory.GetFiles(t.BackupDir, "Battlefield.*.tlst"));
    }

    [Fact]
    public void AddSong_RejectsFilesThatArentBrstm()
    {
        using var t = new TempMusicFolders();
        t.Tracklist(t.MusicRoot, "Battlefield");
        var session = t.Library().OpenStage("Battlefield");
        string wav = Path.Combine(t.Downloads, "song.wav");
        File.WriteAllBytes(wav, "RIFF0000WAVE"u8.ToArray());

        var ex = Assert.Throws<MusicLibraryException>(() => session.AddSong(wav, "Song", "Series"));
        Assert.Contains("isn't a BRSTM", ex.Message);
        Assert.Throws<MusicLibraryException>(() => session.AddSong(Path.Combine(t.Downloads, "missing.brstm"), "Song", "Series"));
        Assert.Throws<MusicLibraryException>(() => session.AddSong(t.DownloadedBrstm("a.brstm"), "   ", "Series"));
        Assert.Throws<MusicLibraryException>(() => session.AddSong(t.DownloadedBrstm("b.brstm"), "Song", "???"));
    }

    [Fact]
    public void RemovingAQueuedSong_UnqueuesItsFile()
    {
        using var t = new TempMusicFolders();
        t.Tracklist(t.MusicRoot, "Battlefield");
        var session = t.Library().OpenStage("Battlefield");
        TlstEntry added = session.AddSong(t.DownloadedBrstm("x.brstm"), "New Song", "Series");

        session.Remove(added);
        session.Save();

        Assert.Empty(session.PendingSongs);
        Assert.False(Directory.Exists(Path.Combine(t.MusicRoot, "strm", "Series")));
    }

    [Fact]
    public void StockOnlyStage_IsCopiedIntoMusicFolderOnSave_StockUntouched()
    {
        using var t = new TempMusicFolders();
        t.FakeBrstm(t.StockRoot, "Pokemon/Gateway Colosseum");
        t.Tracklist(t.StockRoot, "Gateway_Colosseum", Song(0xF000, "Pokemon/Gateway Colosseum", "Gateway Colosseum"));
        byte[] stockBefore = File.ReadAllBytes(Path.Combine(t.StockRoot, "tracklist", "Gateway_Colosseum.tlst"));

        var session = t.Library().OpenStage("Gateway_Colosseum");
        Assert.True(session.StartsFromStock);
        Assert.True(session.IsDirty);
        session.Tracklist.Entries[0].Volume = 100;
        session.Save();

        Assert.Equal(stockBefore, File.ReadAllBytes(Path.Combine(t.StockRoot, "tracklist", "Gateway_Colosseum.tlst")));
        var custom = TlstFile.Load(Path.Combine(t.MusicRoot, "tracklist", "Gateway_Colosseum.tlst"));
        Assert.Equal(100, custom.Entries[0].Volume);
        Assert.False(session.StartsFromStock);
        Assert.False(Directory.Exists(t.BackupDir)); // no earlier custom version existed, so nothing to back up
    }

    [Fact]
    public void Validate_FlagsMissingFilesAndBlocksSave()
    {
        using var t = new TempMusicFolders();
        t.Tracklist(t.StockRoot, "Battlefield");
        t.Tracklist(t.MusicRoot, "Battlefield", Song(0xF000, "Gone/Deleted Song", "Deleted Song"), Song(0x26FC, null, volume: 0));
        var session = t.Library().OpenStage("Battlefield");

        var problems = session.Validate();

        Problem missing = Assert.Single(problems, p => p.Severity == ProblemSeverity.Error);
        Assert.Contains("missing", missing.Message);
        Assert.Equal(0, missing.EntryIndex);
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Warning && p.Message.Contains("volume 0"));
        var ex = Assert.Throws<MusicLibraryException>(() => session.Save());
        Assert.Contains("Can't save yet", ex.Message);
    }

    [Fact]
    public void Validate_WarnsWhenTheBuildHasNoSuchStage()
    {
        using var t = new TempMusicFolders();
        t.Tracklist(t.MusicRoot, "Temple", Song(0x26FC, null));
        t.Tracklist(t.StockRoot, "Hyrule_Temple");

        var problems = t.Library().OpenStage("Temple").Validate();

        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Warning && p.Message.Contains("won't play anywhere"));
        Assert.DoesNotContain(problems, p => p.Severity == ProblemSeverity.Error);
    }

    [Fact]
    public void Move_ReordersAndIgnoresBadIndexes()
    {
        using var t = new TempMusicFolders();
        t.Tracklist(t.MusicRoot, "Battlefield", Song(0x26FC, null, "A"), Song(0x26FD, null, "B"), Song(0x26FE, null, "C"));
        var session = t.Library().OpenStage("Battlefield");

        session.Move(2, 0);
        session.Move(0, 99);

        Assert.Equal(["C", "A", "B"], session.Tracklist.Entries.Select(e => e.Title));
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void AddExistingSong_ReusesAFileAlreadyInTheLibrary()
    {
        using var t = new TempMusicFolders();
        t.FakeBrstm(t.MusicRoot, "Celeste/Confronting Myself");
        t.Tracklist(t.MusicRoot, "Battlefield");
        var session = t.Library().OpenStage("Battlefield");

        TlstEntry e = session.AddExistingSong("Celeste/Confronting Myself", "");

        Assert.Equal("Confronting Myself", e.Title);
        Assert.Empty(session.PendingSongs);
        Assert.Throws<MusicLibraryException>(() => session.AddExistingSong("Celeste/Not There", "x"));
    }

    // ---------- Real folders (read-only) ----------

    [Fact]
    public void RealMusicRepoOverBuild_OpensEveryStageWithNoMissingSongs()
    {
        if (!Directory.Exists(RealData.MusicRoot) || !Directory.Exists(RealData.BuildSound)) { output.WriteLine("Real folders not present; skipped."); return; }
        var lib = new MusicLibrary(MusicFolder.Open(RealData.MusicRoot), MusicFolder.Open(RealData.BuildSound)) { BackupDir = Path.GetTempPath() };

        var stages = lib.Stages();
        var errors = new List<string>();
        int unknownToBuild = 0;
        foreach (StageInfo s in stages)
        {
            var problems = lib.OpenStage(s.Name).Validate();
            errors.AddRange(problems.Where(p => p.Severity == ProblemSeverity.Error).Select(p => $"{s.Name}: {p.Message}"));
            if (s.UsedByBuild == false) unknownToBuild++;
        }

        output.WriteLine($"{stages.Count} stages ({stages.Count(s => s.IsStockOnly)} stock-only, {unknownToBuild} not used by the build).");
        output.WriteLine(string.Join(Environment.NewLine, stages.Take(12).Select(s => $"  {s.Name} -> {s.DisplayName}")));
        Assert.Equal(166, stages.Count);        // 163 music + 3 stock-only (Battlefield_Lunar, Gateway_Colosseum, Radiant_Stadium)
        Assert.Equal(3, stages.Count(s => s.IsStockOnly));
        Assert.Equal(13, unknownToBuild);       // 12 D-pad lists + Space_Debris (Temple was renamed)
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
