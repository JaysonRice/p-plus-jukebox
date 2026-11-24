using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Stages;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// The left side of the Stage Music tab: pick a preset and a stage on its page as the game draws it, then one of that
/// stage's versions (default, hold R/L/Z, the L/R alt menus); or "Every stage", the searchable list of every song list.
/// Picking a version opens its song list through <see cref="MainViewModel.SelectedStage"/>.
/// </summary>
public sealed partial class StageMapViewModel : ObservableObject
{
    private const double MapWidth = 330, PixelsPerUnit = 8;

    private readonly MainViewModel _main;
    private readonly Build _build;
    private readonly StageSlotNames _names;
    private readonly StageAltTable _alts;
    private readonly Func<int, ImageSource?> _iconFor;
    private StageListFile? _file;

    public StageMapViewModel(MainViewModel main, Build build)
    {
        _main = main;
        _build = build;
        _names = build.Library.StageNames;
        _alts = build.Library.LoadStageAlts();
        _iconFor = StageIconCache.For(build.Library);
        var names = build.PresetNamesOrStock;
        foreach (int preset in build.StageListPresets())
            Views.Add(new StageMapView(preset, preset < names.Count ? names[preset] : $"Preset {preset + 1}"));
        Views.Add(StageMapView.EveryStage);
        _selectedView = Views[0];                       // the preset that loads first (or every stage if there are none)
        LoadView(pickAStage: true);
    }

    /// <summary>The presets, then "Every stage".</summary>
    public ObservableCollection<StageMapView> Views { get; } = [];

    private StageMapView _selectedView;
    public StageMapView SelectedView
    {
        get => _selectedView;
        set
        {
            if (value is null || value == _selectedView) return;
            _selectedView = value;
            OnPropertyChanged();
            LoadView(pickAStage: false);
        }
    }

    public bool ShowsEveryStage => SelectedView.IsEveryStage;
    public bool ShowsMap => !SelectedView.IsEveryStage;

    // ---- pages ----

    public ObservableCollection<StagePageChoice> Pages { get; } = [];
    public bool HasPages => Pages.Count > 1;

    private int _selectedPage;
    public int SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value == _selectedPage || !Pages.Any(p => p.Page == value)) return;
            _selectedPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPageChoice));
            LayOutPage();
            FollowSelectedStage();
        }
    }

    public StagePageChoice? SelectedPageChoice
    {
        get => Pages.FirstOrDefault(p => p.Page == _selectedPage);
        set { if (value is not null) SelectedPage = value.Page; }
    }

    // ---- the page, true to the game ----

    public ObservableCollection<StageMapIcon> Icons { get; } = [];
    [ObservableProperty] private double _mapHeight;
    public double MapWidthPixels => MapWidth;

    /// <summary>Plain-text note when the page can't be drawn (empty, or more stages than the game has spots for).</summary>
    [ObservableProperty] private string? _mapNote;

    private StageMapIcon? _selectedIcon;
    public StageMapIcon? SelectedIcon
    {
        get => _selectedIcon;
        private set
        {
            if (_selectedIcon == value) return;
            if (_selectedIcon is not null) _selectedIcon.IsSelected = false;
            _selectedIcon = value;
            if (value is not null) value.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedIcon));
            FillVersions();
        }
    }

    public bool HasSelectedIcon => SelectedIcon is not null;

    // ---- the picked stage's versions ----

    public ObservableCollection<StageVersionRow> Versions { get; } = [];

    public StageVersionRow? SelectedVersion
    {
        get => Versions.FirstOrDefault(v => v.Card == _main.SelectedStage);
        set
        {
            if (value?.Card is null) return;
            _main.SelectedStage = value.Card;
            // Cancelled (unsaved changes kept): put the list's selection back once the click is done.
            Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedVersion)));
        }
    }

    [RelayCommand]
    private void PickIcon(StageMapIcon? icon)
    {
        if (icon is null) return;
        var before = SelectedIcon;
        SelectedIcon = icon;
        var first = Versions.FirstOrDefault(v => v.Card is not null);
        if (first is null || Versions.Any(v => v.Card == _main.SelectedStage)) { OnPropertyChanged(nameof(SelectedVersion)); return; }
        _main.SelectedStage = first.Card;
        if (_main.SelectedStage != first.Card) SelectedIcon = before;   // they chose to stay on their unsaved changes
        OnPropertyChanged(nameof(SelectedVersion));
    }

    /// <summary>The open stage changed (from here, from "Every stage", or from the Song Library): highlight it.</summary>
    internal void OnStageChanged()
    {
        if (ShowsMap) FollowSelectedStage();
        OnPropertyChanged(nameof(SelectedVersion));
    }

    /// <summary>The preset may have been changed on the Stage Lists tab: read it again, keeping the page and stage.</summary>
    internal void Reload()
    {
        var names = _build.PresetNamesOrStock;
        foreach (var v in Views.Where(v => !v.IsEveryStage)) v.Name = v.Preset < names.Count ? names[v.Preset] : v.Name;
        if (ShowsMap) LoadView(pickAStage: false, keepPage: true);
    }

    private void LoadView(bool pickAStage, bool keepPage = false)
    {
        OnPropertyChanged(nameof(ShowsEveryStage));
        OnPropertyChanged(nameof(ShowsMap));
        _file = null;
        int page = keepPage ? _selectedPage : 0;
        Pages.Clear();
        if (!SelectedView.IsEveryStage)
        {
            try { _file = _build.OpenStageList(SelectedView.Preset); }
            catch (Exception e) when (e is StageListFormatException or IOException) { _file = null; }
            foreach (int p in _file?.Entries.Select(e => e.Page).Distinct().Order() ?? Enumerable.Empty<int>()) Pages.Add(new StagePageChoice(p));
        }
        OnPropertyChanged(nameof(HasPages));
        _selectedPage = Pages.Any(p => p.Page == page) ? page : Pages.FirstOrDefault()?.Page ?? 0;
        OnPropertyChanged(nameof(SelectedPage));
        OnPropertyChanged(nameof(SelectedPageChoice));
        LayOutPage();

        if (pickAStage && ShowsMap && Icons.Count > 0 && !Icons.Any(i => Contains(i, _main.SelectedStage)))
        {
            SelectedIcon = Icons[0];
            if (Versions.FirstOrDefault(v => v.Card is not null) is { } first) _main.SelectedStage = first.Card;
        }
        FollowSelectedStage();
        OnPropertyChanged(nameof(SelectedVersion));
    }

    private void LayOutPage()
    {
        var keep = SelectedIcon?.SlotId;
        _selectedIcon = null;
        Icons.Clear();
        MapNote = null;
        if (_file is null)
        {
            MapHeight = 0;
            if (!SelectedView.IsEveryStage) MapNote = "This preset couldn't be read.";
            FillVersions();
            return;
        }
        var entries = _file.Entries.Where(e => e.Page == SelectedPage).OrderBy(e => e.Position).ToList();
        var (_, height, spots) = StagePageLayout.Place(entries.Count, MapWidth, PixelsPerUnit);
        MapHeight = height;
        if (entries.Count == 0) MapNote = "No stages on this page.";
        else if (spots.Count == 0) MapNote = "This page has more stages than the game can show.";
        for (int i = 0; i < spots.Count; i++)
        {
            var e = entries[i];
            Icons.Add(new StageMapIcon(e.SlotId, _names.NameOf(e.SlotId), _iconFor(e.CosmeticId), VersionsOf(e.SlotId),
                spots[i].Left, spots[i].Top, spots[i].Width, spots[i].Height));
        }
        SelectedIcon = Icons.FirstOrDefault(i => i.SlotId == keep);
        if (SelectedIcon is null) FillVersions();
    }

    /// <summary>Highlights the stage that's open, if it's on this page (keeps the current pick if it already is it).</summary>
    private void FollowSelectedStage()
    {
        if (SelectedIcon is not null && Contains(SelectedIcon, _main.SelectedStage)) return;
        SelectedIcon = Icons.FirstOrDefault(i => Contains(i, _main.SelectedStage));
    }

    private static bool Contains(StageMapIcon icon, StageCardViewModel? card) =>
        card is not null && icon.Versions.Any(v => v.Card == card);

    private void FillVersions()
    {
        Versions.Clear();
        foreach (var v in SelectedIcon?.Versions ?? []) Versions.Add(v);
        OnPropertyChanged(nameof(SelectedVersion));
    }

    /// <summary>
    /// A stage slot's song lists, one row each with every way to get it ("Default · Hold Z"). Versions whose song list
    /// isn't in this build, and special modes, are left out. Without version info, falls back to the list named like the stage.
    /// </summary>
    private IReadOnlyList<StageVersionRow> VersionsOf(int slotId)
    {
        var alts = _alts.For(slotId).Where(a => a.Kind != StageAltKind.Special && a.Tracklist is not null).ToList();
        if (alts.Count == 0)
        {
            string name = _names.NameOf(slotId);
            var card = _main.Stages.FirstOrDefault(c => string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            return card is null ? [] : [new StageVersionRow(card, "Default")];
        }
        return alts
            .GroupBy(a => a.Tracklist!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Card: _main.Stages.FirstOrDefault(c => string.Equals(c.Name, g.Key, StringComparison.OrdinalIgnoreCase)), Ways: g.ToList()))
            .Where(x => x.Card is not null)
            .Select(x => new StageVersionRow(x.Card!, string.Join(" · ", x.Ways.Select(Way).Distinct())))
            .ToList();
    }

    private static string Way(StageAlt a) => a.Kind is StageAltKind.LMenu or StageAltKind.RMenu
        ? $"{a.HowToPick} {a.MenuIndex + 1}" : a.HowToPick;
}

/// <summary>An entry in the Stage Music dropdown: a preset, or every stage.</summary>
public sealed partial class StageMapView(int preset, string name) : ObservableObject
{
    public static StageMapView EveryStage { get; } = new(-1, "Every stage");

    public int Preset { get; } = preset;
    public bool IsEveryStage => Preset < 0;

    [ObservableProperty] private string _name = name;

    public override string ToString() => Name;
}

/// <summary>A stage icon where the game draws it, in screen pixels.</summary>
public sealed partial class StageMapIcon(int slotId, string name, ImageSource? icon, IReadOnlyList<StageVersionRow> versions,
    double left, double top, double width, double height) : ObservableObject
{
    public int SlotId { get; } = slotId;
    public string Name { get; } = name;
    public ImageSource? Icon { get; } = icon;
    public IReadOnlyList<StageVersionRow> Versions { get; } = versions;
    public double Left { get; } = left;
    public double Top { get; } = top;
    public double Width { get; } = width;
    public double Height { get; } = height;

    [ObservableProperty] private bool _isSelected;

    public override string ToString() => Name;
}

/// <summary>One song list of a stage, and every way players get the version(s) that play it.</summary>
public sealed record StageVersionRow(StageCardViewModel Card, string HowToPick)
{
    public override string ToString() => $"{HowToPick}: {Card.DisplayName}";
}

/// <summary>A page of the stage select screen (0 = page 1).</summary>
public sealed record StagePageChoice(int Page)
{
    public override string ToString() => $"Page {Page + 1}";
}
