namespace PPlusTracklistEditor.Core.Tlst;

/// <summary>
/// One song in a Project+ tracklist (.tlst). Field layout (16 bytes, big-endian):
/// u32 song ID, i16 delay, u8 volume, u8 frequency, u16 path offset, u16 title offset,
/// u16 song switch, u8 disable stock pinch, u8 hidden from tracklist.
/// </summary>
public sealed class TlstEntry
{
    /// <summary>First ID used for custom (SD card) songs. Brawl's disc songs use lower IDs (0x26F9...).</summary>
    public const uint FirstCustomSongId = 0xF000;

    /// <summary>Song ID. Only needs to be unique within one tracklist.</summary>
    public uint SongId { get; set; }

    /// <summary>Song delay. Signed: -1 is used by some entries.</summary>
    public short Delay { get; set; }

    /// <summary>Volume, 0-255 (Brawl songs mostly sit at 40-127).</summary>
    public byte Volume { get; set; }

    /// <summary>Relative chance of this song being picked on the stage.</summary>
    public byte Frequency { get; set; }

    /// <summary>
    /// Song file relative to <c>strm/</c>, without ".brstm" (e.g. "Fire Emblem/Companions").
    /// Null for Brawl disc songs, which are found by <see cref="SongId"/>.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>Display title. Null when the file stores no title (the game then uses the disc song's name).</summary>
    public string? Title { get; set; }

    /// <summary>Seen values 1800/3600: probably frames until the music switches (on stages that change songs over time).</summary>
    public ushort SongSwitch { get; set; }

    /// <summary>Raw byte; non-zero turns off the low-stock "pinch" music for this song.</summary>
    public byte DisableStockPinchRaw { get; set; }

    /// <summary>Raw byte; non-zero hides the song from the in-game tracklist.</summary>
    public byte HiddenFromTracklistRaw { get; set; }

    public bool DisableStockPinch
    {
        get => DisableStockPinchRaw != 0;
        set => DisableStockPinchRaw = value ? (byte)1 : (byte)0;
    }

    public bool HiddenFromTracklist
    {
        get => HiddenFromTracklistRaw != 0;
        set => HiddenFromTracklistRaw = value ? (byte)1 : (byte)0;
    }

    /// <summary>True when the song is a file on the SD card (has a path), false for Brawl disc songs.</summary>
    public bool IsCustomSong => Path is not null;

    public TlstEntry Clone() => (TlstEntry)MemberwiseClone();

    public override string ToString() =>
        $"{SongId:X4} {Title ?? "(no title)"} [{Path ?? "disc"}] vol {Volume} freq {Frequency}";
}
