using System.Text.RegularExpressions;

namespace PPlusTracklistEditor.Core.Library;

/// <summary>Turns tracklist file names into names people recognize: "Battlefield_Melee" → "Battlefield (Melee)".</summary>
public static partial class StageNames
{
    // Trailing words that mark a version/variant of a stage, shown in parentheses.
    private static readonly HashSet<string> VariantWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Melee", "Brawl", "64", "N64", "Lunar", "OoT", "Mix", "DPAD", "DPADUP", "Remix", "Classic", "Omega",
    };

    // Names where splitting on underscores isn't enough.
    private static readonly Dictionary<string, string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Luigis_Mansion"] = "Luigi's Mansion",
        ["Bowsers_Castle"] = "Bowser's Castle",
        ["Yoshis_Island"] = "Yoshi's Island",
        ["Yoshis_Story"] = "Yoshi's Story",
        ["Draculas_Castle"] = "Dracula's Castle",
        ["Fountain_Of_Dreams"] = "Fountain of Dreams",
        ["Pokemon_Stadium_2"] = "Pokémon Stadium 2",
        ["Pokemon_Stadium"] = "Pokémon Stadium",
        ["Poke_Floats"] = "Poké Floats",
        ["Frigate_Orpheon"] = "Frigate Orpheon",
        ["P+_Stadium"] = "P+ Stadium",
        ["Mushroom_Kingdom_II"] = "Mushroom Kingdom II",
        ["Temple_of_Time"] = "Temple of Time",
    };

    public static string Pretty(string tracklistName)
    {
        if (Exact.TryGetValue(tracklistName, out string? exact)) return exact;

        var words = tracklistName.Split('_', StringSplitOptions.RemoveEmptyEntries).ToList();
        var variants = new List<string>();
        while (words.Count > 1 && VariantWords.Contains(words[^1]))
        {
            variants.Insert(0, VariantLabel(words[^1]));
            words.RemoveAt(words.Count - 1);
        }

        string baseName = string.Join(' ', words);
        if (Exact.TryGetValue(string.Join('_', words), out string? exactBase)) baseName = exactBase;
        baseName = LowercaseSmallWords().Replace(baseName, m => m.Value.ToLowerInvariant());
        return variants.Count == 0 ? baseName : $"{baseName} ({string.Join(", ", variants)})";
    }

    private static string VariantLabel(string word) => word.ToUpperInvariant() switch
    {
        "DPAD" => "D-pad",
        "DPADUP" => "D-pad Up",
        "OOT" => "Ocarina of Time",
        "N64" or "64" => "64",
        _ => word,
    };

    // "Fountain Of Dreams" → "Fountain of Dreams" (not at the start of the name).
    [GeneratedRegex(@"(?<=\s)(Of|The|And|In|On|A)(?=\s)")]
    private static partial Regex LowercaseSmallWords();
}
