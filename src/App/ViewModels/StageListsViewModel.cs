using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Menu;
using PPlusTracklistEditor.Core.Stages;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// The Stage Lists tab for one build: its 8 stage list presets (the lists you cycle with L/R on Random in the game).
/// Pick a preset, switch stages on or off (on = in the list: Random picks from them, and X on Random bans the rest)
/// and hazards on or off, then Save. Sync puts changed presets on the card.
/// </summary>
public sealed partial class StageListsViewModel : ObservableObject
{
    private readonly Build _build;
    private readonly StageSlotNames _names;
    private StageListFile? _file;

    public StageListsViewModel(Build build)
    {
        _build = build;
        _names = build.Library.StageNames;
        foreach (int preset in build.StageListPresets()) Presets.Add(new StagePresetItem(preset));
        CanRename = build.CanRenamePresets;
        foreach (var p in Presets) p.Refresh(build);
        if (Presets.Count > 0) { _selectedPreset = Presets[0]; Load(); }
    }

    private static Window? Owner => Application.Current?.MainWindow;

    public string BuildName => _build.Name;
    public ObservableCollection<StagePresetItem> Presets { get; } = [];
    public bool HasPresets => Presets.Count > 0;
    public ObservableCollection<StagePageViewModel> Pages { get; } = [];

    // ---- which preset (asks about unsaved changes first) ----

    private StagePresetItem? _selectedPreset;
    public StagePresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is null || value == _selectedPreset) return;
            if (!ConfirmLeave()) { Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedPreset))); return; }
            _selectedPreset = value;
            OnPropertyChanged();
            Load();
        }
    }

    public string Title => SelectedPreset is { } p ? p.Name : "";

    // ---- renaming (written into the game's menu file on sync) ----

    public bool CanRename { get; }

    public string RenameHint => CanRename
        ? "Rename this preset. The game shows the new name the next time you put this build on your Project+."
        : "Preset names can't be changed: your Project+ folder's menu file (pf\\menu2\\mu_menumain.pac) couldn't be read.";

    [RelayCommand]
    private void Rename()
    {
        if (!CanRename || SelectedPreset is not { } preset) return;
        string current = _build.PresetNamesOrStock[preset.Index];
        string prompt = $"New name for preset {preset.Index + 1} (up to {PresetNames.MaxLength} characters):" +
                        (preset.Index == 0 ? "\n\nThis one loads first, so its name usually ends with \"(L/R to swap preset)\" as a hint for players." : "");
        while (true)
        {
            string? name = Dialogs.PromptText(Owner, "Rename preset", prompt, current);
            if (name is null || name == current) return;
            try
            {
                _build.RenamePreset(preset.Index, name.Trim());
                preset.Refresh(_build);
                OnPropertyChanged(nameof(Title));
                return;
            }
            catch (MusicLibraryException e)
            {
                Dialogs.Error(Owner, e.Message);
                current = name; // let them fix it rather than retype it
            }
        }
    }

    private void Load()
    {
        Pages.Clear();
        IsDirty = false;
        if (SelectedPreset is not { } preset) return;
        try { _file = _build.OpenStageList(preset.Index); }
        catch (Exception e) when (e is StageListFormatException or IOException)
        {
            _file = null;
            Dialogs.Error(Owner, $"This stage list couldn't be read: {e.Message}");
            return;
        }
        foreach (var page in _file.Entries.GroupBy(e => e.Page))
            Pages.Add(new StagePageViewModel(page.Key, page.Select(e => new StageListRowViewModel(this, _file, e, _names.NameOf(e.SlotId))).ToList()));
        OnPropertyChanged(nameof(Title));
        RefreshSummary();
    }

    // ---- editing ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    private bool _isDirty;

    internal void OnRowChanged()
    {
        IsDirty = true;
        RefreshSummary();
        foreach (var page in Pages) page.Refresh();
    }

    [ObservableProperty] private string _summary = "";

    /// <summary>"Advanced: hazards": shows each stage's hazard box. Off by default, since what hazards do differs per stage.</summary>
    [ObservableProperty] private bool _showHazards;

    partial void OnShowHazardsChanged(bool value) => RefreshSummary();

    private void RefreshSummary()
    {
        if (_file is null) { Summary = ""; return; }
        var on = _file.OnStages.ToList();
        Summary = on.Count == 0
            ? "No stages are on, so Random has nothing to pick from."
            : $"{(on.Count == 1 ? "1 stage" : $"{on.Count} stages")} on: " +
              string.Join(", ", on.Select(e => _names.NameOf(e.SlotId) + (ShowHazards && _file.HasHazards(e) ? " (hazards on)" : "")));
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save() => TrySave();

    public bool TrySave()
    {
        if (_file is null || SelectedPreset is not { } preset) return true;
        try
        {
            _build.SaveStageList(preset.Index, _file);
            IsDirty = false;
            preset.Refresh(_build);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(Owner, $"The stage list couldn't be saved: {e.Message}");
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Discard()
    {
        if (!Dialogs.Confirm(Owner, "Throw away your unsaved changes to this stage list?")) return;
        Load();
    }

    /// <summary>Before switching preset or build, or closing: save, discard or stay. True = go ahead.</summary>
    public bool ConfirmLeave()
    {
        if (!IsDirty || SelectedPreset is not { } preset) return true;
        switch (Dialogs.AskSave(Owner, $"the stage list \"{preset.Name}\" in \"{_build.Name}\""))
        {
            case MessageBoxResult.Yes: return TrySave();
            case MessageBoxResult.No: IsDirty = false; return true;
            default: return false;
        }
    }
}

/// <summary>One of the 8 presets in the list on the left.</summary>
public sealed partial class StagePresetItem(int index) : ObservableObject
{
    public int Index { get; } = index;
    /// <summary>The name the game shows for it (this build's).</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>"Preset 1 · 9 stages on · loads first": its place in the L/R order and what's in it.</summary>
    [ObservableProperty] private string _details = "";

    public void Refresh(Build build)
    {
        Name = build.PresetNamesOrStock[Index];
        string where = $"Preset {Index + 1}";
        try
        {
            int on = build.OpenStageList(Index).OnStages.Count();
            Details = $"{where} · " + (on == 1 ? "1 stage on" : $"{on} stages on") + (Index == 0 ? " · loads first" : "");
        }
        catch (Exception e) when (e is StageListFormatException or IOException) { Details = $"{where} · can't be read"; }
    }

    public override string ToString() => $"{Name}, {Details}";
}

/// <summary>One stage select page and its stages.</summary>
public sealed class StagePageViewModel(int page, IReadOnlyList<StageListRowViewModel> stages) : ObservableObject
{
    public string Title => $"Page {page + 1}";
    public string Details => $"{stages.Count(s => s.IsOn)} of {stages.Count} on";
    public IReadOnlyList<StageListRowViewModel> Stages { get; } = stages;
    public void Refresh() => OnPropertyChanged(nameof(Details));
}

/// <summary>One stage in the preset: on/off and hazards.</summary>
public sealed partial class StageListRowViewModel(StageListsViewModel owner, StageListFile file, StageListEntry entry, string name) : ObservableObject
{
    public string Name { get; } = name;

    public bool IsOn
    {
        get => file.IsOn(entry);
        set
        {
            if (value == IsOn) return;
            file.SetOnWithDefaultHazards(entry, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHazards));
            owner.OnRowChanged();
        }
    }

    public bool HasHazards
    {
        get => file.HasHazards(entry);
        set
        {
            if (value == HasHazards) return;
            file.SetHazards(entry, value);
            OnPropertyChanged();
            owner.OnRowChanged();
        }
    }

    public override string ToString() => Name; // what screen readers call it
}
