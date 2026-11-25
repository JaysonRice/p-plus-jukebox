using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Menu;

namespace PPlusTracklistEditor.Core.Tests;

public class SafetyTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("Series/../../outside")]
    [InlineData(@"..\outside")]
    [InlineData("C:/Windows/outside")]
    public void SongPathsThatLeaveTheFolderAreRefused(string songPath)
    {
        using var setup = new TempSetup();
        var store = new SongStore(setup.Sound);
        Assert.False(store.IsSafe(songPath));
        Assert.False(store.Has(songPath));
        Assert.Throws<MusicLibraryException>(() => store.FileFor(songPath));
    }

    [Fact]
    public void NormalSongPathsStillWork()
    {
        using var setup = new TempSetup();
        var store = new SongStore(setup.Sound);
        Assert.True(store.IsSafe("Series/Song name"));
        Assert.StartsWith(store.Dir, store.FileFor("Series/Song name"));
    }

    [Fact]
    public void Lz11RejectsAnImpossibleSize()
    {
        // Header says 0x7FFFFFFF bytes (extended 4-byte size form), with no data behind it.
        byte[] bad = [0x11, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0];
        Assert.Throws<InvalidDataException>(() => Lz11.Decompress(bad));
    }
}
