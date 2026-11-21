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
/// Each stage is a starter, a counterpick, or not in the list. Saving lays out page 1 of the stage select screen as
/// the counterpicks (top row) then the starters (bottom row), and makes exactly those stages the list (Random picks
/// from them; X on Random bans the rest). Sync puts changed presets on the card.
/// </summary>
public sealed partial class StageListsViewModel : ObservableObject
{
    private readonly Build _build;
    private readonly StageSlotNames _names;
    private StageListFile? _file;
    private string _loadedState = "";

    public StageListsViewModel(Build build)
    {
        _build = build;
        _names = build.Library.StageNames;
        foreach (int preset in build.StageListPresets()) Presets.Add(new StagePresetItem(preset));
        foreach (var p in Presets) p.Refresh(build);
        CanRename = build.CanRenamePresets;
        if (Presets.Count > 0) { _selectedPreset = Presets[0]; Load(); }
    }

    private static Window? Owner => Application.Current?.MainWindow;

    public string BuildName => _build.Name;
    public ObservableCollection<StagePresetItem> Presets { get; } = [];
    public bool HasPresets => Presets.Count > 0;

    /// <summary>Every stage, A to Z, each with its role picker.</summary>
    public ObservableCollection<StageTileViewModel> Stages { get; } = [];

    /// <summary>The list in page order: counterpicks, then starters.</summary>
    private readonly List<StageTileViewModel> _counterpicks = [], _starters = [];

    /// <summary>Page 1 as the game will draw it: rows of stages, top to bottom.</summary>
    public ObservableCollection<IReadOnlyList<StageTileViewModel>> PreviewRows { get; } = [];

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

    private void Load()
    {
        Stages.Clear();
        _counterpicks.Clear();
        _starters.Clear();
        SelectedStage = null;
        if (SelectedPreset is not { } preset) return;
        try { _file = _build.OpenStageList(preset.Index); }
        catch (Exception e) when (e is StageListFormatException or IOException)
        {
            _file = null;
            Dialogs.Error(Owner, $"This stage list couldn't be read: {e.Message}");
            return;
        }

        var layout = StageListLayout.Read(_file, _build.StageRolesFor(preset.Index));
        var tiles = _file.Entries
            .GroupBy(e => e.TableIndex).Select(g => g.First())
            .Select(e => new StageTileViewModel(this, _file, e, _names.NameOf(e.SlotId)))
            .ToDictionary(t => t.Entry.TableIndex);
        foreach (var e in layout.Counterpicks) { tiles[e.TableIndex].SetLoaded(StageRole.Counterpick, _counterpicks.Count); _counterpicks.Add(tiles[e.TableIndex]); }
        foreach (var e in layout.Starters) { tiles[e.TableIndex].SetLoaded(StageRole.Starter, _starters.Count); _starters.Add(tiles[e.TableIndex]); }
        foreach (var t in tiles.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)) Stages.Add(t);

        LayoutNote = layout.FromRows ? null
            : "Page 1 of this preset also shows stages that aren't in its list. Saving lays page 1 out as just the list: counterpicks on top, starters below.";
        _loadedState = State();
        OnPropertyChanged(nameof(Title));
        Refresh();
    }

    // ---- editing ----

    public bool IsDirty => _file is not null && State() != _loadedState;

    /// <summary>Everything the user can change, as text: equal to the loaded state = nothing to save (so undoing a change clears it).</summary>
    private string State() =>
        _file is null ? "" :
        string.Join(",", _counterpicks.Select(t => t.Entry.TableIndex)) + "|" + string.Join(",", _starters.Select(t => t.Entry.TableIndex)) + "|" +
        string.Concat(Stages.Select(t => t.HasHazards ? '1' : '0'));

    /// <summary>A tile asks to change role. False = refused (the list is full).</summary>
    internal bool TryChangeRole(StageTileViewModel tile, StageRole role)
    {
        bool joining = tile.Role == StageRole.NotInList && role != StageRole.NotInList;
        if (joining && _counterpicks.Count + _starters.Count >= StageListLayout.MaxStages)
        {
            Dialogs.Info(Owner, $"A list can have up to {StageListLayout.MaxStages} stages. Take one out first.");
            return false;
        }
        _counterpicks.Remove(tile);
        _starters.Remove(tile);
        var group = role == StageRole.Counterpick ? _counterpicks : role == StageRole.Starter ? _starters : null;
        // Back where it was loaded (undoing a change leaves nothing to save), else at the end of its row.
        if (group is not null) group.Insert(role == tile.LoadedRole ? Math.Min(tile.LoadedPosition, group.Count) : group.Count, tile);
        if (joining) tile.ResetHazards();
        if (role == StageRole.NotInList && SelectedStage == tile) SelectedStage = null;
        return true;
    }

    internal void Refresh()
    {
        PreviewRows.Clear();
        var list = _counterpicks.Concat(_starters).ToList();
        if (list.Count is > 0 and <= StageListFile.MaxPerPage)
        {
            int at = 0;
            foreach (int size in StageSelectLayout.Rows(list.Count)) { PreviewRows.Add(list.Skip(at).Take(size).ToList()); at += size; }
        }
        Counts = list.Count == 0 ? "No stages in this list yet."
            : $"{Plural(_counterpicks.Count, "counterpick")} on top, {Plural(_starters.Count, "starter")} below";
        RowWarning = list.Count > StageListLayout.MaxStages
            ? $"This preset has {list.Count} stages. Lists here hold up to {StageListLayout.MaxStages}: take some out to save it."
            : StageListLayout.RowProblem(_counterpicks.Count, _starters.Count);
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        MoveLeftCommand.NotifyCanExecuteChanged();
        MoveRightCommand.NotifyCanExecuteChanged();
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    [ObservableProperty] private string _counts = "";
    [ObservableProperty] private string? _rowWarning;
    [ObservableProperty] private string? _layoutNote;

    /// <summary>"Advanced: hazards": shows each stage's hazard box. Off by default, since what hazards do differs per stage.</summary>
    [ObservableProperty] private bool _showHazards;

    // ---- order within a row (the stage picked in the preview) ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveLeftCommand), nameof(MoveRightCommand))]
    private StageTileViewModel? _selectedStage;

    partial void OnSelectedStageChanged(StageTileViewModel? oldValue, StageTileViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    [RelayCommand]
    private void SelectStage(StageTileViewModel tile) => SelectedStage = tile;

    private List<StageTileViewModel>? GroupOf(StageTileViewModel? t) =>
        t is null ? null : _counterpicks.Contains(t) ? _counterpicks : _starters.Contains(t) ? _starters : null;

    private bool CanMoveLeft() => GroupOf(SelectedStage) is { } g && g.IndexOf(SelectedStage!) > 0;
    private bool CanMoveRight() => GroupOf(SelectedStage) is { } g && g.IndexOf(SelectedStage!) < g.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveLeft))]
    private void MoveLeft() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveRight))]
    private void MoveRight() => Move(1);

    private void Move(int by)
    {
        if (GroupOf(SelectedStage) is not { } g) return;
        int i = g.IndexOf(SelectedStage!);
        g.RemoveAt(i);
        g.Insert(i + by, SelectedStage!);
        Refresh();
    }

    // ---- save / discard ----

    private bool CanSave() => IsDirty && _counterpicks.Count + _starters.Count <= StageListLayout.MaxStages;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => TrySave();

    public bool TrySave()
    {
        if (_file is null || SelectedPreset is not { } preset) return true;
        if (_counterpicks.Count + _starters.Count > StageListLayout.MaxStages)
        {
            Dialogs.Error(Owner, RowWarning ?? $"Lists hold up to {StageListLayout.MaxStages} stages.");
            return false;
        }
        try
        {
            _file.LayOutPageOne([.. _counterpicks.Select(t => t.Entry), .. _starters.Select(t => t.Entry)]);
            _build.SaveStageList(preset.Index, _file, new PresetRoles
            {
                Counterpicks = _counterpicks.Select(t => t.Entry.TableIndex).ToList(),
                Starters = _starters.Select(t => t.Entry.TableIndex).ToList(),
            });
            preset.Refresh(_build);
            Load();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
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
            case MessageBoxResult.No: Load(); return true;
            default: return false;
        }
    }

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
}

/// <summary>One of the 8 presets in the list on the left.</summary>
public sealed partial class StagePresetItem(int index) : ObservableObject
{
    public int Index { get; } = index;
    /// <summary>The name the game shows for it (this build's).</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>"Preset 1 · 9 stages · loads first": its place in the L/R order and what's in it.</summary>
    [ObservableProperty] private string _details = "";

    public void Refresh(Build build)
    {
        Name = build.PresetNamesOrStock[Index];
        string where = $"Preset {Index + 1}";
        try
        {
            int on = build.OpenStageList(Index).OnStages.Count();
            Details = $"{where} · " + (on == 1 ? "1 stage" : $"{on} stages") + (Index == 0 ? " · loads first" : "");
        }
        catch (Exception e) when (e is StageListFormatException or IOException) { Details = $"{where} · can't be read"; }
    }

    public override string ToString() => $"{Name}, {Details}";
}

/// <summary>A choice in a stage's role picker.</summary>
public sealed record StageRoleOption(StageRole Role, string Label)
{
    public static IReadOnlyList<StageRoleOption> All { get; } =
        [new(StageRole.NotInList, "Not in list"), new(StageRole.Counterpick, "Counterpick"), new(StageRole.Starter, "Starter")];

    public override string ToString() => Label; // what screen readers say for the choice
}

/// <summary>One stage: its role in the list, and hazards.</summary>
public sealed partial class StageTileViewModel(StageListsViewModel owner, StageListFile file, StageListEntry entry, string name) : ObservableObject
{
    public StageListEntry Entry { get; } = entry;
    public string Name { get; } = name;
    public IReadOnlyList<StageRoleOption> RoleOptions => StageRoleOption.All;

    private StageRole _role;
    public StageRole Role
    {
        get => _role;
        set
        {
            if (value == _role) return;
            if (!owner.TryChangeRole(this, value))
            {
                Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(Role))); // put the picker back
                return;
            }
            _role = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InList));
            owner.Refresh();
        }
    }

    public bool InList => _role != StageRole.NotInList;

    internal StageRole LoadedRole { get; private set; }
    internal int LoadedPosition { get; private set; }
    private bool _loadedHazards;

    /// <summary>Its role and place as loaded, so taking it out and putting it back restores both (and its hazards).</summary>
    internal void SetLoaded(StageRole role, int position)
    {
        _role = LoadedRole = role;
        LoadedPosition = position;
        _loadedHazards = file.HasHazards(Entry);
    }

    public bool HasHazards
    {
        get => file.HasHazards(Entry);
        set
        {
            if (value == HasHazards) return;
            file.SetHazards(Entry, value);
            OnPropertyChanged();
            owner.Refresh();
        }
    }

    /// <summary>
    /// A stage joining the list starts with the normal hazard setting (on for Fountain of Dreams only), unless it was in
    /// the list when loaded: then it gets its setting back.
    /// </summary>
    internal void ResetHazards()
    {
        file.SetHazards(Entry, LoadedRole != StageRole.NotInList ? _loadedHazards : StageListFile.StartsWithHazards(Entry.SlotId));
        OnPropertyChanged(nameof(HasHazards));
    }

    /// <summary>Picked in the page 1 preview (for moving left/right).</summary>
    [ObservableProperty] private bool _isSelected;

    public override string ToString() => Name; // what screen readers call it
}
