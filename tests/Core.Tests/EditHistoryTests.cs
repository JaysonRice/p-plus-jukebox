using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Tests;

public class EditHistoryTests
{
    [Fact]
    public void UndoRedo_RestoresSongsAndQueuedFiles()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        var session = lib.NewBuild("KY").OpenStage("Temple_of_Time");
        var history = new EditHistory(session);
        int start = session.Tracklist.Entries.Count;

        history.BeforeChange();
        session.AddSong(t.DownloadedBrstm("new.brstm"), "New Song", "Test");
        history.BeforeChange();
        session.Remove(session.Tracklist.Entries[0]);
        Assert.Equal(start, session.Tracklist.Entries.Count);

        Assert.True(history.Undo());                       // un-remove
        Assert.Equal(start + 1, session.Tracklist.Entries.Count);
        Assert.True(history.Undo());                       // un-add: the queued file goes too
        Assert.Equal(start, session.Tracklist.Entries.Count);
        Assert.Empty(session.PendingSongs);
        Assert.False(session.IsDirty);
        Assert.False(history.Undo());

        Assert.True(history.Redo());
        Assert.Single(session.PendingSongs);
        Assert.Equal("New Song", session.Tracklist.Entries[^1].Title);
        Assert.True(history.CanRedo);

        history.BeforeChange();                            // a new edit drops the redo branch
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void SliderDrag_IsOneUndoStep_AndSnapshotsArePrivateCopies()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var session = t.CreateLibrary().NewBuild("KY").OpenStage("Temple_of_Time");
        var history = new EditHistory(session);
        TlstEntry song = session.Tracklist.Entries[0];
        byte original = song.Volume;

        foreach (byte v in new byte[] { 81, 85, 90, 100 })
        {
            history.BeforeChange("volume:0");
            session.Tracklist.Entries[0].Volume = v;
        }
        history.Break();
        history.BeforeChange("volume:0");
        session.Tracklist.Entries[0].Volume = 50;

        history.Undo();
        Assert.Equal(100, session.Tracklist.Entries[0].Volume);
        history.Undo();
        Assert.Equal(original, session.Tracklist.Entries[0].Volume); // the whole drag at once
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Frequency_LabelsAndChances()
    {
        Assert.Equal("Normal", SongFrequency.Label(40));
        Assert.Equal("Custom (85)", SongFrequency.Label(85));
        var a = new TlstEntry { Frequency = 40 };
        var b = new TlstEntry { Frequency = 80 };
        var never = new TlstEntry { Frequency = 0 };
        TlstEntry[] stage = [a, b, never];
        Assert.Equal(1 / 3.0, SongFrequency.Chance(stage, a), 6);
        Assert.Equal(2 / 3.0, SongFrequency.Chance(stage, b), 6);
        Assert.Equal(0, SongFrequency.Chance([never], never));
    }
}
