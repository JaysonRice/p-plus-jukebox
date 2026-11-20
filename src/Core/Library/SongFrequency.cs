using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Library;

/// <summary>
/// "How often a song plays", in words. The tracklist stores a weight per song: the game picks a song at random,
/// each with chance weight / (sum of all weights on the stage). 40 is the normal weight (1731 of 1830 real entries);
/// 80 = twice as likely, 20 = half, 10/5 = rarely, 0 = never (only plays if picked from the in-game menu).
/// </summary>
public static class SongFrequency
{
    public sealed record Choice(string Label, byte Value);

    public static readonly IReadOnlyList<Choice> Choices =
    [
        new("Never (menu only)", 0),
        new("Rarely", 10),
        new("Less often", 20),
        new("Normal", 40),
        new("Often", 80),
    ];

    public static string Label(byte value) =>
        Choices.FirstOrDefault(c => c.Value == value)?.Label ?? $"Custom ({value})";

    /// <summary>Chance (0..1) this entry is the one picked when the stage loads. 0 if nothing on the stage can play.</summary>
    public static double Chance(IReadOnlyList<TlstEntry> stage, TlstEntry entry)
    {
        int total = stage.Sum(e => e.Frequency);
        return total == 0 ? 0 : (double)entry.Frequency / total;
    }
}
