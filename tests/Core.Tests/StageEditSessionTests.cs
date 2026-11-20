using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Tlst;
using static PPlusTracklistEditor.Core.Tests.TempSetup;

namespace PPlusTracklistEditor.Core.Tests;

public class StageEditSessionTests
{
    private static (TempSetup Setup, AppLibrary Lib, Build Build) Kentucky()
    {
        var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        return (t, lib, lib.NewBuild("Kentucky"));
    }

    [Fact]
    public void AddSong_QueuesUntilSave_ThenCopiesIntoTheLibraryWithSafeUniqueName()
    {
        var (t, lib, ky) = Kentucky();
        using var _ = t;
        t.Brstm(lib.SongsDir, "Pokemon/Pokemon Center");               // name already taken in the library
        var session = ky.OpenStage("Temple_of_Time");
        Assert.False(session.IsDirty);

        TlstEntry added = session.AddSong(t.DownloadedBrstm("dl.brstm"), "Pokémon Center", "Pokémon");
        TlstEntry again = session.AddSong(t.DownloadedBrstm("dl2.brstm"), "Pokémon Center", "Pokémon");

        Assert.Equal("Pokemon/Pokemon Center (2)", added.Path);
        Assert.Equal("Pokemon/Pokemon Center (3)", again.Path);         // also avoids the queued one
        Assert.Equal("Pokémon Center", added.Title);                    // title keeps the accent; file name doesn't
        Assert.Equal(0xF002u, added.SongId);
        Assert.Equal((byte)80, added.Volume);
        Assert.True(session.IsDirty);
        Assert.False(lib.Songs.Has("Pokemon/Pokemon Center (2)"));      // nothing copied yet
        Assert.Equal(SongSource.Library, session.Locate(added).Source); // but it counts as present

        session.Save();

        Assert.True(lib.Songs.Has("Pokemon/Pokemon Center (2)"));
        Assert.Equal(["ToT 1", "ToT 2", null, "Pokémon Center", "Pokémon Center"],
                     TlstFile.Load(ky.TracklistPath("Temple_of_Time")).Entries.Select(e => e.Title));
        Assert.False(session.IsDirty);
        Assert.Single(Directory.GetFiles(Path.Combine(lib.BackupDir, ky.Id), "Temple_of_Time.*.tlst"));
        Assert.Equal(3, lib.DefaultBuild.OpenStage("Temple_of_Time").Tracklist.Entries.Count); // other builds untouched
    }

    [Fact]
    public void AddSong_RejectsBadInput_WithPlainMessages()
    {
        var (t, _, ky) = Kentucky();
        using var _t = t;
        var session = ky.OpenStage("Temple_of_Time");
        string wav = Path.Combine(t.Downloads, "song.wav");
        File.WriteAllBytes(wav, "RIFF0000WAVE"u8.ToArray());

        Assert.Contains("isn't a BRSTM", Assert.Throws<MusicLibraryException>(() => session.AddSong(wav, "Song", "Series")).Message);
        Assert.Throws<MusicLibraryException>(() => session.AddSong(Path.Combine(t.Downloads, "missing.brstm"), "Song", "Series"));
        Assert.Throws<MusicLibraryException>(() => session.AddSong(t.DownloadedBrstm("a.brstm"), "   ", "Series"));
        Assert.Throws<MusicLibraryException>(() => session.AddSong(t.DownloadedBrstm("b.brstm"), "Song", "???"));
    }

    [Fact]
    public void RemovingAQueuedSong_UnqueuesIt_AndRemovingASavedSongKeepsTheFile()
    {
        var (t, lib, ky) = Kentucky();
        using var _ = t;
        var session = ky.OpenStage("Temple_of_Time");
        TlstEntry added = session.AddSong(t.DownloadedBrstm("x.brstm"), "New Song", "Series");

        session.Remove(added);
        session.Remove(session.Tracklist.Entries[0]);          // "Zelda/Temple of Time 1"
        session.Save();

        Assert.Empty(session.PendingSongs);
        Assert.False(Directory.Exists(Path.Combine(lib.SongsDir, "Series")));
        Assert.True(lib.Songs.Has("Zelda/Temple of Time 1"));  // removed from the stage, still in the library
    }

    [Fact]
    public void AddExistingSong_ReusesALibrarySong()
    {
        var (t, _, ky) = Kentucky();
        using var _t = t;
        var session = ky.OpenStage("Mushroom_Kingdom");

        TlstEntry e = session.AddExistingSong("Zelda/Temple of Time 2");

        Assert.Equal("Temple of Time 2", e.Title);
        Assert.Empty(session.PendingSongs);
        Assert.Throws<MusicLibraryException>(() => session.AddExistingSong("Zelda/Not There"));
    }

    [Fact]
    public void ResetToDefault_RestoresTheDefaultSongs()
    {
        var (t, _, ky) = Kentucky();
        using var _t = t;
        var session = ky.OpenStage("Temple_of_Time");
        session.Remove(session.Tracklist.Entries[0]);
        session.Save();

        var again = ky.OpenStage("Temple_of_Time");
        Assert.True(again.CanResetToDefault);
        again.ResetToDefault();

        Assert.Equal(3, again.Tracklist.Entries.Count);
        Assert.True(again.IsDirty);
        Assert.False(ky.Library.DefaultBuild.OpenStage("Temple_of_Time").CanResetToDefault);
    }

    [Fact]
    public void Validate_FlagsMissingFiles_AndBlocksSave()
    {
        var (t, _, ky) = Kentucky();
        using var _t = t;
        Tracklist(ky.TracklistDir, "Temple_of_Time", Song(0xF000, "Gone/Deleted Song", "Deleted Song"), Song(0x26FC, null, volume: 0), Song(0xF001, "Z01"));
        var session = ky.OpenStage("Temple_of_Time");

        var problems = session.Validate();

        Problem missing = Assert.Single(problems, p => p.Severity == ProblemSeverity.Error);   // Z01 is a disc song, not missing
        Assert.Contains("missing", missing.Message);
        Assert.Equal(0, missing.EntryIndex);
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Warning && p.Message.Contains("volume 0"));
        Assert.Contains("Can't save yet", Assert.Throws<MusicLibraryException>(() => session.Save()).Message);
    }

    [Fact]
    public void Validate_WarnsAboutStageNamesProjectPlusDoesntHave()
    {
        var (t, _, ky) = Kentucky();
        using var _t = t;
        Tracklist(ky.TracklistDir, "Temple", Song(0x26FC, null));

        var problems = ky.OpenStage("Temple").Validate();

        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Warning && p.Message.Contains("won't play anywhere"));
        Assert.False(ky.Stages().Single(s => s.Name == "Temple").KnownToProjectPlus);
    }

    [Fact]
    public void Move_ReordersAndIgnoresBadIndexes()
    {
        var (t, _, ky) = Kentucky();
        using var _t = t;
        var session = ky.OpenStage("Temple_of_Time");

        session.Move(2, 0);
        session.Move(0, 99);

        Assert.Equal([null, "ToT 1", "ToT 2"], session.Tracklist.Entries.Select(e => e.Title));
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void DisplayTitle_UsesTitleThenDiscNameThenFileName()
    {
        Assert.Equal("My Title", Build.DisplayTitle(Song(0x26FC, null, "My Title")));
        Assert.Equal("Battlefield", Build.DisplayTitle(Song(0x26FC, null)));
        Assert.Equal("Paradigm Shift", Build.DisplayTitle(Song(0xF000, "Final Fantasy/Paradigm Shift")));
        Assert.Equal("Unnamed song", Build.DisplayTitle(Song(0x1, null)));
    }

    [Fact]
    public void SongWithoutSeries_IsSavedAndSyncedStraightIntoStrm()
    {
        var (t, lib, ky) = Kentucky();
        using var _ = t;
        var session = ky.OpenStage("Temple_of_Time");
        TlstEntry loose = session.AddSong(t.DownloadedBrstm("loose.brstm"), "Loose Song", series: "");
        Assert.Equal("Loose Song", loose.Path);
        session.Save();
        Assert.True(lib.Songs.Has("Loose Song"));

        PPlusTracklistEditor.Core.Sync.BuildSync.Execute(PPlusTracklistEditor.Core.Sync.BuildSync.Plan(ky, t.ProjectPlus));
        Assert.True(t.SdHasSong("Loose Song")); // strm\Loose Song.brstm
    }
}
