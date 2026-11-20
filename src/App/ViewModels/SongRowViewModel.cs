using CommunityToolkit.Mvvm.ComponentModel;
using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// One song on the stage being edited. Setting a property records an undo step (merged while the same field of
/// the same song keeps changing, e.g. a slider drag), changes the entry, then re-checks the stage.
/// </summary>
public sealed partial class SongRowViewModel : ObservableObject
{
    private readonly StageEditorViewModel _editor;

    public SongRowViewModel(StageEditorViewModel editor, TlstEntry entry, int index)
    {
        _editor = editor;
        Entry = entry;
        Index = index;
    }

    public TlstEntry Entry { get; }
    public int Index { get; }
    public int Number => Index + 1;

    private SongLocation Location => _editor.Session.Locate(Entry);
    public bool IsMissing => Location.Source == SongSource.Missing;
    public bool IsDiscSong => Location.Source == SongSource.Disc;
    public string? FilePath => Location.Source == SongSource.Library ? Location.FilePath : null;
    public bool IsQueued => _editor.Session.PendingSongs.Any(p => p.SongPath.Equals(Entry.Path, StringComparison.OrdinalIgnoreCase));

    public string DisplayTitle => Build.DisplayTitle(Entry);

    public string SourceText => Location.Source switch
    {
        SongSource.Disc => "Original Brawl song (from the disc)",
        SongSource.Missing => "Missing! The song file can't be found",
        _ when IsQueued => $"New: {Entry.Path} (added when you save)",
        _ => Entry.Path ?? "",
    };

    public string HowOftenText
    {
        get
        {
            double chance = SongFrequency.Chance(_editor.Session.Tracklist.Entries, Entry);
            return Entry.Frequency == 0 ? SongFrequency.Label(0) : $"{SongFrequency.Label(Entry.Frequency)} ({chance:P0})";
        }
    }

    // ---- editable fields ----

    public string Title
    {
        get => Entry.Title ?? "";
        set => Edit("title", () => Entry.Title = string.IsNullOrWhiteSpace(value) ? null : value, Entry.Title != value);
    }

    public int Volume
    {
        get => Entry.Volume;
        set
        {
            byte v = (byte)Math.Clamp(value, 0, 255);
            Edit("volume", () => Entry.Volume = v, Entry.Volume != v);
            _editor.OnVolumeChanged(this);
        }
    }

    /// <summary>Slider range. Real songs go up to 130; a bigger stored value widens it so the slider never clips it.</summary>
    public int VolumeMax => Math.Max(150, (int)Entry.Volume);

    public SongFrequency.Choice? FrequencyChoice
    {
        get => SongFrequency.Choices.FirstOrDefault(c => c.Value == Entry.Frequency);
        set { if (value is not null) Frequency = value.Value; }
    }

    public int Frequency
    {
        get => Entry.Frequency;
        set
        {
            byte v = (byte)Math.Clamp(value, 0, 255);
            Edit("frequency", () => Entry.Frequency = v, Entry.Frequency != v);
        }
    }

    // ---- advanced ----

    public string SongIdText => $"0x{Entry.SongId:X4}";

    public int Delay
    {
        get => Entry.Delay;
        set { short v = (short)Math.Clamp(value, short.MinValue, short.MaxValue); Edit("delay", () => Entry.Delay = v, Entry.Delay != v); }
    }

    public int SongSwitch
    {
        get => Entry.SongSwitch;
        set { ushort v = (ushort)Math.Clamp(value, 0, ushort.MaxValue); Edit("switch", () => Entry.SongSwitch = v, Entry.SongSwitch != v); }
    }

    public bool DisableStockPinch
    {
        get => Entry.DisableStockPinch;
        set => Edit("pinch", () => Entry.DisableStockPinch = value, Entry.DisableStockPinch != value);
    }

    public bool HiddenFromTracklist
    {
        get => Entry.HiddenFromTracklist;
        set => Edit("hidden", () => Entry.HiddenFromTracklist = value, Entry.HiddenFromTracklist != value);
    }

    private void Edit(string field, Action apply, bool changed)
    {
        if (!changed) return;
        _editor.History.BeforeChange($"{field}:{Index}");
        apply();
        _editor.AfterRowEdit();
        RefreshAll();
    }

    /// <summary>What screen readers (and UI Automation) call this row.</summary>
    public override string ToString() => DisplayTitle;

    /// <summary>Re-reads everything from the entry (after any change, since chances depend on the whole stage).</summary>
    public void RefreshAll() => OnPropertyChanged(string.Empty);
}
