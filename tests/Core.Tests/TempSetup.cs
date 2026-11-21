using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Tests;

/// <summary>
/// A throwaway fake SD card (<c>SD\Project+\pf\sound\...</c>), an app library folder, and a "downloads" folder,
/// all in %TEMP% and deleted on dispose.
/// </summary>
internal sealed class TempSetup : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("pplus-test").FullName;
    public string SdCard => Path.Combine(Root, "SD");
    public string ProjectPlusRoot => Path.Combine(SdCard, "Project+");
    public string Sound => Path.Combine(ProjectPlusRoot, "pf", "sound");
    public string LibraryRoot => Path.Combine(Root, "App", "Library");
    public string Downloads => Path.Combine(Root, "downloads");

    public TempSetup()
    {
        Directory.CreateDirectory(Path.Combine(Sound, "strm"));
        Directory.CreateDirectory(Path.Combine(Sound, "tracklist"));
        Directory.CreateDirectory(Path.Combine(ProjectPlusRoot, "pf", "fighter", "yoshi")); // something that must never be touched
        File.WriteAllText(Path.Combine(ProjectPlusRoot, "pf", "fighter", "yoshi", "FitYoshi.pac"), "moveset");
        Directory.CreateDirectory(Downloads);
    }

    public ProjectPlusFolder ProjectPlus => ProjectPlusFolder.Resolve(ProjectPlusRoot);

    public AppLibrary CreateLibrary() => AppLibrary.CreateFromProjectPlus(LibraryRoot, ProjectPlus);

    /// <summary>Writes a fake BRSTM (passes the "RSTM" check). <paramref name="content"/> makes files differ.</summary>
    public string Brstm(string strmDir, string songPath, byte content = 1)
    {
        string file = Path.Combine(strmDir, songPath.Replace('/', '\\') + ".brstm");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [.. "RSTM"u8, content, content, content]);
        return file;
    }

    public string SdSong(string songPath, byte content = 1) => Brstm(Path.Combine(Sound, "strm"), songPath, content);

    public string DownloadedBrstm(string name, byte content = 9)
    {
        string file = Path.Combine(Downloads, name);
        File.WriteAllBytes(file, [.. "RSTM"u8, content, content]);
        return file;
    }

    public void SdTracklist(string stage, params TlstEntry[] entries) => Tracklist(Path.Combine(Sound, "tracklist"), stage, entries);

    public static void Tracklist(string dir, string stage, params TlstEntry[] entries)
    {
        Directory.CreateDirectory(dir);
        var t = new TlstFile();
        t.Entries.AddRange(entries);
        t.Save(Path.Combine(dir, stage + ".tlst"));
    }

    public static TlstEntry Song(uint id, string? path, string? title = null, byte volume = 80) =>
        new() { SongId = id, Path = path, Title = title, Volume = volume, Frequency = 40 };

    public bool SdHasSong(string songPath) => File.Exists(Path.Combine(Sound, "strm", songPath.Replace('/', '\\') + ".brstm"));

    /// <summary>A small stock-like Project+: 2 stages, 3 songs, a "_b" hurry-up file, a disc song.</summary>
    public void StockProjectPlus()
    {
        SdSong("Zelda/Temple of Time 1");
        SdSong("Zelda/Temple of Time 2");
        SdSong("Super Mario/Overworld");
        SdSong("Super Mario/Overworld_b");   // hurry-up version: no tracklist names it
        SdTracklist("Temple_of_Time",
            Song(0xF000, "Zelda/Temple of Time 1", "ToT 1"),
            Song(0xF001, "Zelda/Temple of Time 2", "ToT 2"),
            Song(0x26FC, null));             // disc song
        SdTracklist("Mushroom_Kingdom", Song(0xF000, "Super Mario/Overworld", "Overworld"), Song(0xF001, "Z01"));
    }

    /// <summary>
    /// A small stage list preset: one page, stages given as (slot id, on, hazards). Same layout as the game's .rss
    /// (masks at 0x00/0x1E, page at 0x3C, stage table at 0x104).
    /// </summary>
    public static byte[] Rss(params (int Slot, bool On, bool Hazards)[] stages)
    {
        var b = new byte[800];
        uint on = 0, hz = 0;
        b[0x3C] = (byte)stages.Length;
        for (int i = 0; i < stages.Length; i++)
        {
            b[0x3C + 1 + i] = (byte)i;               // page position i -> table index i
            b[0x104 + i * 2] = (byte)stages[i].Slot;  // stage slot
            b[0x104 + i * 2 + 1] = (byte)(i + 1);     // cosmetic id
            if (stages[i].On) on |= 1u << i;
            if (stages[i].Hazards) hz |= 1u << i;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(2), on);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0x1E + 2), hz);
        b[799] = 0xEE; // something outside the masks, to prove it survives a save
        return b;
    }

    public string StageListDir => Path.Combine(ProjectPlusRoot, "pf", "stage", "switch");

    /// <summary>8 presets in the fake Project+ (preset n has stage slots 1..3, slot n+1 hazards on) plus stage names.</summary>
    public void StockStageLists()
    {
        Directory.CreateDirectory(StageListDir);
        for (int n = 0; n < 8; n++)
            File.WriteAllBytes(Path.Combine(StageListDir, $"Switch{n:00}.rss"), Rss((1, true, n == 0), (2, n % 2 == 0, n == 1), (3, false, false)));
        string names = Path.Combine(ProjectPlusRoot, "pf", "stage", "stageslot");
        Directory.CreateDirectory(names);
        File.WriteAllLines(Path.Combine(names, "Stageslot IDs.txt"), ["01 - Battlefield", "02 - Final Destination", "03 - Delfino Secret"]);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
