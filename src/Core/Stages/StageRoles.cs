namespace PPlusTracklistEditor.Core.Stages;

/// <summary>What a stage is in a tournament list.</summary>
public enum StageRole
{
    NotInList,
    /// <summary>Can be picked after game 1. Shown on the top row of page 1.</summary>
    Counterpick,
    /// <summary>Can be picked for game 1. Shown on the bottom row of page 1.</summary>
    Starter,
}

/// <summary>
/// A preset's starters and counterpicks as saved in build.json (the game has no such setting: page 1 just lists the
/// counterpicks, then the starters, and the row layout does the rest). Stages are stage-table indexes, in row order.
/// </summary>
public sealed class PresetRoles
{
    public int Preset { get; set; }
    public List<int> Counterpicks { get; set; } = [];
    public List<int> Starters { get; set; } = [];
}

/// <summary>A preset read as counterpicks + starters.</summary>
/// <param name="FromRows">
/// False when page 1 isn't just the list (e.g. it shows every stage and only marks the legal ones): then every listed
/// stage is read as a counterpick, and saving lays page 1 out as rows.
/// </param>
public sealed record StageListLayout(IReadOnlyList<StageListEntry> Counterpicks, IReadOnlyList<StageListEntry> Starters, bool FromRows)
{
    /// <summary>Lists made in the app hold at most this many stages (8–10 split cleanly into 3–5 counterpicks + 5 starters).</summary>
    public const int MaxStages = 10;

    public int Count => Counterpicks.Count + Starters.Count;

    /// <summary>
    /// Reads a preset: the saved roles if they still describe the file, else from page 1's rows (top row = counterpicks,
    /// the rest = starters), else every listed stage as a counterpick.
    /// </summary>
    public static StageListLayout Read(StageListFile file, PresetRoles? saved)
    {
        var pageOne = file.Entries.Where(e => e.Page == 0).ToList();
        var on = file.OnStages.ToList();
        bool pageOneIsTheList = pageOne.Count == on.Count && pageOne.All(file.IsOn);
        var byIndex = file.Entries.GroupBy(e => e.TableIndex).ToDictionary(g => g.Key, g => g.First());

        if (saved is not null && pageOneIsTheList
            && saved.Counterpicks.Concat(saved.Starters).SequenceEqual(pageOne.Select(e => e.TableIndex)))
            return new(saved.Counterpicks.Select(i => byIndex[i]).ToList(), saved.Starters.Select(i => byIndex[i]).ToList(), true);

        if (pageOneIsTheList && pageOne.Count <= StageListFile.MaxPerPage)
        {
            var rows = StageSelectLayout.Rows(pageOne.Count);
            int top = rows.Count >= 2 ? rows[0] : 0; // one row: no top/bottom split, read them all as starters
            return new(pageOne.Take(top).ToList(), pageOne.Skip(top).ToList(), true);
        }
        return new(on, [], false);
    }

    /// <summary>
    /// Null if this many counterpicks and starters show as counterpicks on top and starters below; else a plain
    /// explanation of how the game will arrange them.
    /// </summary>
    public static string? RowProblem(int counterpicks, int starters)
    {
        int total = counterpicks + starters;
        if (total == 0 || total > StageListFile.MaxPerPage) return null;
        var rows = StageSelectLayout.Rows(total);
        if (rows.Count == 1)
            return counterpicks > 0 && starters > 0 ? $"With {total} stages the game shows them all in one row, counterpicks first." : null;
        if (rows[0] == counterpicks) return null;
        string layout = string.Join(" / ", rows);
        return counterpicks > rows[0]
            ? $"With {total} stages the game shows {layout} (top to bottom), so {counterpicks - rows[0]} counterpick(s) will sit on the starters' row."
            : $"With {total} stages the game shows {layout} (top to bottom), so {rows[0] - counterpicks} starter(s) will sit on the counterpicks' row.";
    }
}
