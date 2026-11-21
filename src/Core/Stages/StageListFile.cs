using System.Buffers.Binary;

namespace PPlusTracklistEditor.Core.Stages;

/// <summary>A stage list file isn't what we expected. The message can be shown to the user.</summary>
public sealed class StageListFormatException(string message) : Exception(message);

/// <summary>One stage in a stage list preset: where it sits on the stage select screen and which stage it is.</summary>
/// <param name="Page">0-based stage select page.</param>
/// <param name="Position">0-based position on that page (also its bit in the page's masks).</param>
/// <param name="SlotId">The stage slot (names in <c>pf/stage/stageslot/Stageslot IDs.txt</c>).</param>
/// <param name="CosmeticId">Which stage icon/name the game shows for it.</param>
public sealed record StageListEntry(int Page, int Position, int TableIndex, int SlotId, int CosmeticId);

/// <summary>
/// A Project+ stage list preset: <c>pf/stage/switch/SwitchNN.rss</c> (800 bytes, big-endian). The game has 8 of them;
/// L/R on Random in the stage select cycles through them, and Switch00 loads first.
///
/// Layout (see pplus-wii MODDING_NOTES.md):
/// - 0x00: "on" masks, 5 pages x 6 bytes (2 flag bytes, then a u32). Bit i = the page's i-th stage is in the list
///   (picked by Random, and kept when "X on Random" bans everything else). That set is what players call the list's
///   legal stages.
/// - 0x1E: hazard masks, same layout (bit set = hazards on).
/// - 0x3C: pages, 40 bytes each: a stage count (0 = page unused), then that many stage-table indexes.
/// - 0x104: stage table, 2 bytes per index: stage slot ID, cosmetic ID.
///
/// Only the two masks are edited; every other byte is kept exactly as read, so an unedited file saves unchanged.
/// </summary>
public sealed class StageListFile
{
    public const int Size = 800;
    public const int MaxPages = 5;
    private const int OnMasksAt = 0x00, HazardMasksAt = 0x1E, MaskStride = 6, MaskOffset = 2;
    private const int PagesAt = 0x3C, PageBytes = 40, TableAt = 0x104;

    private readonly byte[] _bytes;

    private StageListFile(byte[] bytes)
    {
        _bytes = bytes;
        var entries = new List<StageListEntry>();
        for (int page = 0; page < MaxPages; page++)
        {
            int count = _bytes[PagesAt + page * PageBytes];
            if (count > 32) throw new StageListFormatException($"Page {page + 1} lists {count} stages; a page can hold at most 32.");
            for (int pos = 0; pos < count; pos++)
            {
                int index = _bytes[PagesAt + page * PageBytes + 1 + pos];
                int at = TableAt + index * 2;
                if (at + 1 >= Size) throw new StageListFormatException("The stage list points past its stage table.");
                entries.Add(new StageListEntry(page, pos, index, _bytes[at], _bytes[at + 1]));
            }
        }
        Entries = entries;
    }

    /// <summary>Every stage on every used page, in screen order.</summary>
    public IReadOnlyList<StageListEntry> Entries { get; }

    public int PageCount => Entries.Count == 0 ? 0 : Entries.Max(e => e.Page) + 1;

    public static StageListFile Load(string path) => FromBytes(File.ReadAllBytes(path));

    public static StageListFile FromBytes(byte[] bytes)
    {
        if (bytes.Length != Size) throw new StageListFormatException($"A stage list file is {Size} bytes; this one is {bytes.Length}.");
        return new StageListFile((byte[])bytes.Clone());
    }

    public byte[] ToBytes() => (byte[])_bytes.Clone();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", _bytes);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>In the list: Random can pick it, and it survives "X on Random".</summary>
    public bool IsOn(StageListEntry e) => GetBit(OnMasksAt, e);
    public void SetOn(StageListEntry e, bool on) => SetBit(OnMasksAt, e, on);

    public bool HasHazards(StageListEntry e) => GetBit(HazardMasksAt, e);
    public void SetHazards(StageListEntry e, bool on) => SetBit(HazardMasksAt, e, on);

    /// <summary>
    /// Fountain of Dreams' stage slot. It's the one stage whose hazards (rising and sinking platforms) are normally on:
    /// stock presets disagree on Dream Land, Green Hill Zone and Smashville, but every one that has Fountain of Dreams
    /// in its list has its hazards on.
    /// </summary>
    public const int FountainOfDreamsSlot = 0x1F;

    /// <summary>The hazard setting a stage gets when it's added to a list: on for Fountain of Dreams only.</summary>
    public static bool StartsWithHazards(int slotId) => slotId == FountainOfDreamsSlot;

    /// <summary>Puts a stage in or out of the list. Adding it also resets its hazards to the normal setting.</summary>
    public void SetOnWithDefaultHazards(StageListEntry e, bool on)
    {
        SetOn(e, on);
        if (on) SetHazards(e, StartsWithHazards(e.SlotId));
    }

    /// <summary>The stages in the list (on), in screen order.</summary>
    public IEnumerable<StageListEntry> OnStages => Entries.Where(IsOn);

    private int MaskAt(int start, int page) => start + page * MaskStride + MaskOffset;

    private bool GetBit(int start, StageListEntry e) =>
        ((BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(MaskAt(start, e.Page))) >> e.Position) & 1) != 0;

    private void SetBit(int start, StageListEntry e, bool value)
    {
        var span = _bytes.AsSpan(MaskAt(start, e.Page));
        uint mask = BinaryPrimitives.ReadUInt32BigEndian(span);
        mask = value ? mask | (1u << e.Position) : mask & ~(1u << e.Position);
        BinaryPrimitives.WriteUInt32BigEndian(span, mask);
    }
}

/// <summary>
/// Stage slot names from <c>pf/stage/stageslot/Stageslot IDs.txt</c> ("01 - Battlefield"), plus the stock preset names.
/// </summary>
public sealed class StageSlotNames
{
    private readonly Dictionary<int, string> _names;

    private StageSlotNames(Dictionary<int, string> names) => _names = names;

    public static StageSlotNames Empty { get; } = new([]);

    public static StageSlotNames Load(string path) => File.Exists(path) ? Parse(File.ReadAllLines(path)) : Empty;

    public static StageSlotNames Parse(IEnumerable<string> lines)
    {
        var names = new Dictionary<int, string>();
        foreach (string line in lines)
        {
            int dash = line.IndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0 || !int.TryParse(line.AsSpan(0, dash).Trim(), System.Globalization.NumberStyles.HexNumber, null, out int id)) continue;
            string name = line[(dash + 3)..].Trim();
            // Some lines explain the file name, e.g. "Shadow Moses Island (written as Shadow Moses)": keep the real name.
            int note = name.IndexOf(" (written as", StringComparison.OrdinalIgnoreCase);
            if (note > 0) name = name[..note];
            if (name.Length > 0) names[id] = name;
        }
        return new StageSlotNames(names);
    }

    public string NameOf(int slotId) => _names.TryGetValue(slotId, out var n) ? n : $"Stage slot {slotId:X2}";

    /// <summary>
    /// What stock Project+ calls its 8 presets in the game's menu (preset 1 = Switch00). The real names live in
    /// pf/menu2/mu_menumain.pac; a build may have changed them.
    /// </summary>
    public static readonly IReadOnlyList<string> StockPresetNames =
        ["Default", "PMBR", "2023 Proposed", "2024 Proposed", "Midwest", "Australia", "Japan", "All Stages"];
}
