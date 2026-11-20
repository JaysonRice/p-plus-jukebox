using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.Core.Tests;

public class NamingTests
{
    [Theory]
    [InlineData("Pokémon Center", "Pokemon Center")]
    [InlineData("Bowser's Galaxy Generator (M&S 2014)", "Bowser's Galaxy Generator (M&S 2014)")]   // ' and & kept, like real paths
    [InlineData("What: Is/This? <Song>*", "What Is This Song")]
    [InlineData("  lots   of   spaces  ", "lots of spaces")]
    [InlineData("Ending dots...", "Ending dots")]
    [InlineData("Straße – Remix", "Strasse - Remix")]
    [InlineData("Don’t Stop “Now”", "Don't Stop Now")]
    [InlineData("CON", "CON_")]
    public void Sanitize_MakesSafeAsciiNames(string input, string expected) =>
        Assert.Equal(expected, SongFileNames.Sanitize(input));

    [Theory]
    [InlineData("???")]
    [InlineData("   ")]
    [InlineData("日本語")]
    public void Sanitize_ReturnsNullWhenNothingUsableIsLeft(string input) =>
        Assert.Null(SongFileNames.Sanitize(input));

    [Fact]
    public void Sanitize_CapsLength() =>
        Assert.Equal(SongFileNames.MaxNameLength, SongFileNames.Sanitize(new string('a', 200))!.Length);

    [Theory]
    [InlineData("Kirby", "Royal Road", "Kirby/Royal Road")]
    [InlineData("", "Royal Road", "Royal Road")]      // no series: straight into strm/
    [InlineData("   ", "Royal Road", "Royal Road")]
    public void UniqueSongPath_SeriesIsOptional(string series, string title, string expected) =>
        Assert.Equal(expected, SongFileNames.UniqueSongPath(_ => false, series, title));

    [Fact]
    public void UniqueSongPath_NoSeries_StillAvoidsClashes()
    {
        Assert.Equal("Royal Road (2)", SongFileNames.UniqueSongPath(p => p == "Royal Road", "", "Royal Road"));
        Assert.Throws<MusicLibraryException>(() => SongFileNames.UniqueSongPath(_ => false, "???", "Royal Road")); // typed, but nothing usable
    }

    [Theory]
    [InlineData("Battlefield", "Battlefield")]
    [InlineData("Battlefield_Melee", "Battlefield (Melee)")]
    [InlineData("Battlefield_64", "Battlefield (64)")]
    [InlineData("Luigis_Mansion_DPAD", "Luigi's Mansion (D-pad)")]
    [InlineData("Dream_Land_DPADUP", "Dream Land (D-pad Up)")]
    [InlineData("Fountain_Of_Dreams", "Fountain of Dreams")]
    [InlineData("Temple_of_Time_OoT", "Temple of Time (Ocarina of Time)")]
    [InlineData("Pokemon_Stadium_2", "Pokémon Stadium 2")]
    [InlineData("Hyrule_Temple", "Hyrule Temple")]
    public void StageNames_ArePretty(string file, string expected) =>
        Assert.Equal(expected, StageNames.Pretty(file));

    [Fact]
    public void DiscSongs_KnowsBrawlSongs()
    {
        DiscSong? bf = DiscSongs.Find(0x26FC);
        Assert.Equal(("Battlefield", "X04", (int?)80), (bf!.Name, bf.FileName, bf.DefaultVolume));
        Assert.Null(DiscSongs.Find(0xF000));
        Assert.True(DiscSongs.ById.Count >= 280);
    }
}
