using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PPlusTracklistEditor.Core.Stages;

/// <summary>How a player gets a version of a stage.</summary>
public enum StageAltKind { Default, HoldR, HoldL, HoldZ, LMenu, RMenu, Special }

/// <summary>
/// One version of a stage slot: what loads when the player holds <see cref="Button"/> (P+'s alternate stage loader),
/// and which song list (tracklist name, no extension) it plays. <see cref="Tracklist"/> is null if the version's
/// settings file couldn't be read.
/// </summary>
public sealed record StageAlt(int Button, string Param, string? Tracklist)
{
    public StageAltKind Kind => Button switch
    {
        0 => StageAltKind.Default,
        0x20 => StageAltKind.HoldR,
        0x40 => StageAltKind.HoldL,
        0x10 => StageAltKind.HoldZ,
        >= 0xC000 => StageAltKind.Special,
        >= 0x8000 => StageAltKind.LMenu,
        >= 0x4000 => StageAltKind.RMenu,
        _ => StageAltKind.Special,
    };

    /// <summary>Position in the L or R alt menu (0 = first).</summary>
    public int MenuIndex => Button & 0x3FFF;

    /// <summary>How the player gets it, in plain words.</summary>
    public string HowToPick => Kind switch
    {
        StageAltKind.Default => "Default",
        StageAltKind.HoldR => "Hold R",
        StageAltKind.HoldL => "Hold L",
        StageAltKind.HoldZ => "Hold Z",
        StageAltKind.LMenu => "L alt menu",
        StageAltKind.RMenu => "R alt menu",
        _ => "Special",
    };
}

/// <summary>
/// Every stage slot's versions, read from a Project+ build's <c>pf/stage/stageslot/*.asl</c> (which version loads for
/// which button) and <c>pf/stage/stageinfo/*.param</c> (which song list each version plays). Only ever read; the
/// library keeps the result as one small text file.
/// </summary>
/// <remarks>
/// <c>.asl</c> (big-endian): "ASLS", u16 count, u16 string table offset, then (from +8) count × {u16 button mask,
/// u16 name offset from the string table}; names are zero-terminated and name a <c>.param</c> file. Masks: 0 default,
/// 0x20 R, 0x40 L, 0x10 Z (GameCube button bits), 0x8000 + n / 0x4000 + n = item n of the L / R alt menu (L or R +
/// Start on the stage select), 0xC000 and up = special modes such as events.
/// <c>.param</c>: "STEX", u32 string table offset; the table's first string is the tracklist name.
/// </remarks>
public sealed class StageAltTable
{
    private readonly Dictionary<int, IReadOnlyList<StageAlt>> _slots;

    private StageAltTable(Dictionary<int, IReadOnlyList<StageAlt>> slots) => _slots = slots;

    public static StageAltTable Empty { get; } = new([]);

    public int Count => _slots.Count;

    /// <summary>The versions of a stage slot, default first; empty if the build has no alt rules for it.</summary>
    public IReadOnlyList<StageAlt> For(int slotId) => _slots.GetValueOrDefault(slotId) ?? [];

    /// <summary>Reads a build's <c>pf/stage</c> folder; slots whose files can't be read are left out.</summary>
    public static StageAltTable FromStageFolder(string stageDir)
    {
        var slots = new Dictionary<int, IReadOnlyList<StageAlt>>();
        string slotDir = Path.Combine(stageDir, "stageslot"), infoDir = Path.Combine(stageDir, "stageinfo");
        if (!Directory.Exists(slotDir)) return new(slots);
        foreach (string asl in Directory.EnumerateFiles(slotDir, "*.asl"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(asl), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int slot)) continue;
            try
            {
                slots[slot] = ParseAsl(File.ReadAllBytes(asl))
                    .Select(e => new StageAlt(e.Button, e.Param, TracklistOf(Path.Combine(infoDir, e.Param + ".param"))))
                    .ToList();
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
        }
        return new(slots);
    }

    private static string? TracklistOf(string paramFile)
    {
        try { return File.Exists(paramFile) ? TracklistFromParam(File.ReadAllBytes(paramFile)) : null; }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>The (button mask, param name) entries of an <c>.asl</c> file, in file order.</summary>
    public static IReadOnlyList<(int Button, string Param)> ParseAsl(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual("ASLS"u8)) throw new InvalidDataException("Not an alt stage file.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        int strings = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
        var entries = new List<(int, string)>(count);
        for (int i = 0; i < count; i++)
        {
            int at = 8 + i * 4;
            int button = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            int name = strings + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2));
            entries.Add((button, CString(data, name)));
        }
        return entries;
    }

    /// <summary>The song list a stage settings (<c>.param</c>) file plays.</summary>
    public static string TracklistFromParam(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual("STEX"u8)) throw new InvalidDataException("Not a stage settings file.");
        return CString(data, (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)));
    }

    private static string CString(byte[] data, int at)
    {
        if (at < 0 || at >= data.Length) throw new InvalidDataException("Name is outside the file.");
        int end = Array.IndexOf(data, (byte)0, at);
        return Encoding.ASCII.GetString(data, at, (end < 0 ? data.Length : end) - at);
    }

    // Saved as one line per version: slot (hex), button (hex), param name, tracklist name (empty if unknown).

    public void Save(string path) => File.WriteAllLines(path, _slots.OrderBy(s => s.Key).SelectMany(s =>
        s.Value.Select(a => $"{s.Key:X2}\t{a.Button:X4}\t{a.Param}\t{a.Tracklist}")));

    public static StageAltTable Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        var slots = new Dictionary<int, List<StageAlt>>();
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                string[] f = line.Split('\t');
                if (f.Length < 4 || !int.TryParse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int slot)
                    || !int.TryParse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int button)) continue;
                if (!slots.TryGetValue(slot, out var list)) slots[slot] = list = [];
                list.Add(new StageAlt(button, f[2], f[3].Length == 0 ? null : f[3]));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Empty; }
        return new(slots.ToDictionary(s => s.Key, s => (IReadOnlyList<StageAlt>)s.Value));
    }
}
