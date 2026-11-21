namespace PPlusTracklistEditor.Core.Menu;

/// <summary>
/// The stage list preset names the game shows on Random (L/R to switch). Project+ keeps them as the first 8 strings of
/// a message table, in two files with the same layout: <c>pf/menu2/sc_selcharacter2.pac</c> (the copy shown on Random)
/// and <c>pf/menu2/mu_menumain.pac</c>. Each is an LZ11-compressed ARC, with the table inside a nested ARC
/// ("MenuRule_en"). The stage names follow in the same table.
///
/// Renaming changes those 8 strings only. The result is checked before it's handed back: it must decompress to exactly
/// the archive we built, and every other file in both archives must be byte for byte what it was.
/// </summary>
public static class PresetNames
{
    public const int Count = 8;
    public const int MaxLength = 40;

    /// <summary>The names in a menu file, or throws <see cref="InvalidDataException"/> with a plain reason.</summary>
    public static IReadOnlyList<string> Read(string pacPath) => Read(File.ReadAllBytes(pacPath));

    public static IReadOnlyList<string> Read(byte[] pac) => Locate(Lz11.Decompress(pac)).Table.Strings.Take(Count).ToList();

    /// <summary>Null if the name is fine, else why not (for the UI).</summary>
    public static string? Problem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A preset needs a name.";
        if (name.Length > MaxLength) return $"Keep names to {MaxLength} characters or fewer.";
        if (name.Any(c => c < 0x20 || c > 0x7E)) return "Use plain letters, numbers and punctuation (the game's menu font may not have other characters).";
        if (name != name.Trim()) return "Names can't start or end with a space.";
        return null;
    }

    /// <summary>A new menu file with the 8 preset names replaced (compressed like the original).</summary>
    public static byte[] Rename(byte[] pac, IReadOnlyList<string> names)
    {
        if (names.Count != Count) throw new ArgumentException($"Expected {Count} names.", nameof(names));
        if (names.Select(Problem).FirstOrDefault(p => p is not null) is { } problem) throw new ArgumentException(problem, nameof(names));

        byte[] original = Lz11.Decompress(pac);
        var found = Locate(original);
        var table = found.Table;
        for (int i = 0; i < Count; i++) table.Strings[i] = names[i];

        found.Inner.Replace(found.InnerIndex, table.ToBytes());
        found.Outer.Replace(found.OuterIndex, found.Inner.ToBytes());
        byte[] rebuilt = found.Outer.ToBytes();
        byte[] compressed = Lz11.Compress(rebuilt);

        Verify(original, rebuilt, compressed, found.OuterIndex, found.InnerIndex, names);
        return compressed;
    }

    private static void Verify(byte[] original, byte[] rebuilt, byte[] compressed, int outerIndex, int innerIndex, IReadOnlyList<string> names)
    {
        if (!Lz11.Decompress(compressed).AsSpan().SequenceEqual(rebuilt)) Fail("compression didn't round-trip");
        var before = ArcArchive.Parse(original);
        var after = ArcArchive.Parse(rebuilt);
        if (before.Count != after.Count) Fail("the file count changed");
        for (int i = 0; i < before.Count; i++)
            if (i != outerIndex && !before.Data(i).AsSpan().SequenceEqual(after.Data(i))) Fail($"file {i} changed");
        var innerBefore = ArcArchive.Parse(before.Data(outerIndex));
        var innerAfter = ArcArchive.Parse(after.Data(outerIndex));
        for (int i = 0; i < innerBefore.Count; i++)
            if (i != innerIndex && !innerBefore.Data(i).AsSpan().SequenceEqual(innerAfter.Data(i))) Fail($"menu file {i} changed");
        var tableBefore = MessageTable.Parse(innerBefore.Data(innerIndex)).Strings;
        var tableAfter = MessageTable.Parse(innerAfter.Data(innerIndex)).Strings;
        if (!tableAfter.Take(Count).SequenceEqual(names) || !tableAfter.Skip(Count).SequenceEqual(tableBefore.Skip(Count))) Fail("the names didn't come out as written");

        static void Fail(string what) => throw new InvalidDataException($"Renaming was stopped because a safety check failed ({what}). Nothing was written.");
    }

    private sealed record Found(ArcArchive Outer, int OuterIndex, ArcArchive Inner, int InnerIndex, MessageTable Table);

    /// <summary>
    /// Finds the table: a message table, in an ARC nested in the menu ARC, that names the stages (it has "Battlefield"
    /// and "Final Destination" after the presets). Exactly one must match, so we never edit the wrong text.
    /// </summary>
    private static Found Locate(byte[] archive)
    {
        var outer = ArcArchive.Parse(archive);
        var matches = new List<Found>();
        for (int o = 0; o < outer.Count; o++)
        {
            byte[] data = outer.Data(o);
            if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual("ARC\0"u8)) continue;
            ArcArchive inner;
            try { inner = ArcArchive.Parse(data); } catch (InvalidDataException) { continue; }
            for (int i = 0; i < inner.Count; i++)
            {
                MessageTable table;
                try { table = MessageTable.Parse(inner.Data(i)); } catch (InvalidDataException) { continue; }
                var rest = table.Strings.Skip(Count).ToList();
                if (table.Strings.Count > Count && rest.Contains("Battlefield") && rest.Contains("Final Destination"))
                    matches.Add(new Found(outer, o, inner, i, table));
            }
        }
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException("This menu file doesn't have the stage list preset names where Project+ keeps them."),
            _ => throw new InvalidDataException("This menu file has more than one list that looks like the preset names, so it was left alone."),
        };
    }

}
