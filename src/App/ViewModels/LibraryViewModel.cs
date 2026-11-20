using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Audio;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// A row in the library list: either a series header or a song. Grouping is done by putting header rows into the
/// same flat list (not WPF's CollectionView grouping, which builds every row and took ~2 s for 400 songs).
/// A flat list only builds the rows on screen, so showing, filtering and collapsing stay fast.
/// </summary>
public abstract class LibraryRow : ObservableObject
{
    public abstract bool IsHeader { get; }
    public abstract SeriesGroup Group { get; }
    public virtual string DisplayText => "";
    /// <summary>Hover text for the name cell; empty for most rows.</summary>
    public virtual string NameTooltip => "";
    public virtual string Series => "";
    public virtual string LengthText => "";
    public virtual string LoopText => "";
    public virtual string UsedByText => "";
    /// <summary>How many builds use the song (the "Builds" column); empty for series header rows.</summary>
    public virtual string BuildCountText => "";
    /// <summary>Hover text for the build count: which builds.</summary>
    public virtual string BuildsTooltip => "";
    public virtual string AddedText => "";
}

/// <summary>A series folder, shown as a clickable header row. Songs with no folder share the "No series" group.</summary>
public sealed partial class SeriesGroup(string key) : LibraryRow
{
    public string Key { get; } = key;
    public string DisplayName => Key.Length == 0 ? "No series" : Key;
    /// <summary>Groups sort by name, with "No series" last.</summary>
    public string SortKey => Key.Length == 0 ? "￿" : Key;

    public override bool IsHeader => true;
    public override SeriesGroup Group => this;
    public override string DisplayText => $"{(IsExpanded ? "▾" : "▸")}  {DisplayName} ({VisibleCount})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private bool _isExpanded = true;

    /// <summary>Songs in this series that pass the current search/filter (whether or not it's collapsed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private int _visibleCount;

    public override string ToString() => $"{DisplayName}, {VisibleCount} songs, {(IsExpanded ? "expanded" : "collapsed")}";
}

/// <summary>One song in the library list.</summary>
public sealed partial class LibrarySongItem : LibraryRow
{
    public LibrarySongItem(string songPath, string filePath, SeriesGroup group, IReadOnlyList<string> usedBy, DateTime added)
    {
        SongPath = songPath;
        FilePath = filePath;
        _group = group;
        UsedBy = usedBy;
        Added = added;
        int slash = songPath.LastIndexOf('/');
        Name = slash < 0 ? songPath : songPath[(slash + 1)..];
    }

    private readonly SeriesGroup _group;
    public override bool IsHeader => false;
    public override SeriesGroup Group => _group;

    public string SongPath { get; }
    public string Name { get; }
    public string FilePath { get; }
    public IReadOnlyList<string> UsedBy { get; }
    public DateTime Added { get; }

    /// <summary>The name, with a * after it for the few songs that don't loop (hover for the explanation).</summary>
    public override string DisplayText => DoesNotLoop ? Name + " *" : Name;
    public override string NameTooltip => DoesNotLoop ? "This song doesn't loop. It plays once, then the stage goes quiet." : "";
    /// <summary>True once the song's header has been read and it has no loop.</summary>
    public bool DoesNotLoop => Info is { Loop: null };
    public override string Series => Group.DisplayName;
    public override string AddedText => Added.ToString("d");
    public override string UsedByText => UsedBy.Count == 0 ? "Not used by any build" : "Used by " + string.Join(", ", UsedBy);
    public override string BuildCountText => UsedBy.Count.ToString();
    public override string BuildsTooltip => UsedBy.Count == 0
        ? "Not used by any build"
        : (UsedBy.Count == 1 ? "Used by 1 build:" : $"Used by {UsedBy.Count} builds:") + string.Concat(UsedBy.Select(b => "\n• " + b));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText), nameof(LoopText), nameof(DisplayText), nameof(NameTooltip), nameof(DoesNotLoop))]
    private BrstmInfo? _info;

    public TimeSpan? Length => Info?.Duration;
    public override string LengthText => Info is { } i ? i.Duration.ToString(i.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss") : "";
    public override string LoopText => Info is null ? "" : Info.Loop is null ? "Plays once" : "Loops";

    public override string ToString() => $"{Name}, {Series}";
}

/// <summary>
/// Every song in the shared library: search, filter by which builds use it, sort, group by series (collapsible).
/// Used as the "Song library" tab, and as the picker for "Add from your library" (<c>pickForStage</c> set).
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    /// <param name="Key">Stays the same when the label changes (it names the current build), so the choice survives switching builds.</param>
    public sealed record Option(string Key, string Label, Func<LibrarySongItem, bool> Matches)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// One thing to sort by. <paramref name="Compare"/> is the ascending order (A to Z, shortest first, oldest first, fewest
    /// first); the direction is chosen separately and flips when the column header is clicked.
    /// <paramref name="DefaultDescending"/>: the direction people usually want first (longest, newest, most builds).
    /// <paramref name="Unknown"/>: songs without a value (a length not read yet) always go last, either direction.
    /// <paramref name="ColumnHeader"/>: the table column this sort belongs to.
    /// </summary>
    public sealed record SortOption(string Label, string ColumnHeader, Comparison<LibrarySongItem> Compare, bool DefaultDescending = false,
                                    Func<LibrarySongItem, bool>? Unknown = null)
    {
        public override string ToString() => Label;
    }

    // Header info (length, loop) is cached by file + timestamp, so reloading the tab doesn't re-read 400 files.
    private static readonly Dictionary<(string, DateTime), BrstmInfo?> InfoCache = new(StringComparer.OrdinalIgnoreCase.ToTupleComparer());

    private readonly AppLibrary _library;
    private readonly Dictionary<string, SeriesGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LibrarySongItem> _songs = [];
    private int _loadGeneration;
    private bool _bulkChange;

    public LibraryViewModel(AppLibrary library, PreviewPlayer player, string? pickForStage)
    {
        _library = library;
        Player = player;
        Preview = new SongPreviewViewModel(player);
        PickForStage = pickForStage;
        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is LibraryRow r && Shows(r);
        _sort = SortOptions[0]; // before CustomSort: the comparer reads it
        RowsView.CustomSort = new Comparer(this);
        Reload();
    }

    public PreviewPlayer Player { get; }
    /// <summary>The player strip above the list (the selected song's timeline).</summary>
    public SongPreviewViewModel Preview { get; }
    public string? PickForStage { get; }
    public bool IsPicking => PickForStage is not null;
    public string Heading => IsPicking ? $"Add songs to {PickForStage}" : "Your song library";

    /// <summary>Header rows and song rows together; <see cref="RowsView"/> filters and orders them.</summary>
    public BulkObservableCollection<LibraryRow> Rows { get; } = [];
    public ListCollectionView RowsView { get; }

    // ---- filters ----

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => RefreshView();

    public ObservableCollection<Option> UsageOptions { get; } = [];

    [ObservableProperty] private Option? _usage;
    partial void OnUsageChanged(Option? value) => RefreshView();

    private string? _currentBuildName;
    /// <summary>
    /// The build being worked on. The "Show" choices are about this build only ("Not in &lt;build&gt;"), however many builds
    /// there are. Set by the main window when the build changes.
    /// </summary>
    public string? CurrentBuildName
    {
        get => _currentBuildName;
        set
        {
            if (_currentBuildName == value) return;
            _currentBuildName = value;
            BuildUsageOptions();
            RefreshView();
        }
    }

    /// <summary>All songs / not in the current build / in the current build / not in any build. Keeps the chosen kind when the build changes.</summary>
    private void BuildUsageOptions()
    {
        string? keep = Usage?.Key;
        UsageOptions.Clear();
        UsageOptions.Add(new Option("all", "All songs", _ => true));
        if (CurrentBuildName is { } name)
        {
            UsageOptions.Add(new Option("notHere", $"Not in \"{name}\"", s => !s.UsedBy.Contains(name)));
            UsageOptions.Add(new Option("here", $"In \"{name}\"", s => s.UsedBy.Contains(name)));
        }
        UsageOptions.Add(new Option("none", "Not in any build", s => s.UsedBy.Count == 0));
        _usage = UsageOptions.FirstOrDefault(o => o.Key == keep) ?? UsageOptions[0];
        OnPropertyChanged(nameof(Usage));
    }

    /// <summary>Search + "Show" filter (not collapsing).</summary>
    private bool Matches(LibrarySongItem s)
    {
        if (Usage is { } u && !u.Matches(s)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        string haystack = s.Name + " " + s.Series;
        return SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private bool Shows(LibraryRow row) => row switch
    {
        SeriesGroup g => GroupBySeries && g.VisibleCount > 0,
        LibrarySongItem s => Matches(s) && (!GroupBySeries || s.Group.IsExpanded),
        _ => false,
    };

    // ---- sorting and grouping ----

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new("Name", "Song", (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)),
        new("Series", "Series", (a, b) => string.Compare(a.Group.SortKey, b.Group.SortKey, StringComparison.OrdinalIgnoreCase)), // "No series" last
        new("Length", "Length", (a, b) => Nullable.Compare(a.Length, b.Length), DefaultDescending: true, Unknown: s => s.Length is null),
        new("Date added", "Added", (a, b) => a.Added.CompareTo(b.Added), DefaultDescending: true),
        new("Build count", "Build count", (a, b) => a.UsedBy.Count.CompareTo(b.UsedBy.Count), DefaultDescending: true),
    ];

    [ObservableProperty] private SortOption _sort;
    partial void OnSortChanged(SortOption value)
    {
        _descending = value.DefaultDescending; // a new sort starts in its usual direction
        OnPropertyChanged(nameof(Descending));
        RefreshView();
    }

    /// <summary>Direction of the current sort. Flipped by clicking the column header (see <see cref="SortByColumn"/>).</summary>
    [ObservableProperty] private bool _descending;
    partial void OnDescendingChanged(bool value) => RefreshView();

    /// <summary>A column header was clicked: flip the direction if it's the current sort, else sort by it. False if it isn't sortable.</summary>
    public bool SortByColumn(string header)
    {
        var option = SortOptions.FirstOrDefault(o => o.ColumnHeader == header);
        if (option is null) return false;
        if (option == Sort) Descending = !Descending;
        else Sort = option;
        return true;
    }

    // Off by default: grouping sorts only within each series, which makes the sorts far less useful. Turn it on to browse by series.
    [ObservableProperty] private bool _groupBySeries;
    partial void OnGroupBySeriesChanged(bool value) => RefreshView();

    /// <summary>When grouping: series name, then the header, then its songs in the chosen order.</summary>
    private sealed class Comparer(LibraryViewModel vm) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (LibraryRow)x!;
            var b = (LibraryRow)y!;
            if (vm.GroupBySeries)
            {
                int g = string.Compare(a.Group.SortKey, b.Group.SortKey, StringComparison.OrdinalIgnoreCase);
                if (vm.Sort.ColumnHeader == "Series" && vm.Descending) g = -g; // sorting by series: the series themselves flip
                if (g != 0) return g;
                if (a.IsHeader != b.IsHeader) return a.IsHeader ? -1 : 1;
            }
            if (a is not LibrarySongItem sa || b is not LibrarySongItem sb) return 0;
            var sort = vm.Sort;
            if (sort.Unknown is { } unknown && unknown(sa) != unknown(sb)) return unknown(sa) ? 1 : -1; // no value: last, either way
            int c = sort.Compare(sa, sb);
            if (vm.Descending) c = -c;
            return c != 0 ? c : string.Compare(sa.SongPath, sb.SongPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void ToggleGroup(SeriesGroup group) => group.IsExpanded = !group.IsExpanded;

    [RelayCommand]
    private void ExpandAll() => SetAllExpanded(true);

    [RelayCommand]
    private void CollapseAll() => SetAllExpanded(false);

    private void SetAllExpanded(bool expanded)
    {
        _bulkChange = true;
        foreach (var g in _groups.Values) g.IsExpanded = expanded;
        _bulkChange = false;
        RefreshView();
    }

    private void OnGroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SeriesGroup.IsExpanded) && !_bulkChange) RefreshView();
    }

    [ObservableProperty] private string _countText = "";

    /// <summary>Recounts each series for its header, then re-filters and re-sorts the list.</summary>
    private void RefreshView()
    {
        foreach (var g in _groups.Values) g.VisibleCount = 0;
        int shown = 0;
        foreach (var s in _songs)
            if (Matches(s)) { s.Group.VisibleCount++; shown++; }
        RowsView.Refresh();
        CountText = shown == _songs.Count ? $"{_songs.Count} songs" : $"{shown} of {_songs.Count} songs";
    }

    // ---- loading ----

    /// <summary>Re-reads the library and which builds use what (call when the tab is shown; builds may have changed).</summary>
    public void Reload()
    {
        var builds = _library.Builds();
        var usage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var build in builds)
            foreach (var path in build.SongPathsUsed())
            {
                if (!usage.TryGetValue(path, out var list)) usage[path] = list = [];
                list.Add(build.Name);
            }

        string? keepSelected = (Selected as LibrarySongItem)?.SongPath;
        _songs.Clear();
        foreach (var path in _library.Songs.All())
        {
            int slash = path.LastIndexOf('/');
            string series = slash < 0 ? "" : path[..slash];
            if (!_groups.TryGetValue(series, out var group))
            {
                _groups[series] = group = new SeriesGroup(series);
                group.PropertyChanged += OnGroupChanged;
            }
            IReadOnlyList<string> usedBy = usage.TryGetValue(path, out var b) ? b : [];
            // A "_b" hurry-up file counts as used when its main song is.
            if (usedBy.Count == 0 && SongStore.HurryUpBase(path) is { } main && usage.TryGetValue(main, out var mb)) usedBy = mb;
            string file = _library.Songs.FileFor(path);
            var item = new LibrarySongItem(path, file, group, usedBy, File.GetCreationTime(file));
            if (InfoCache.TryGetValue((file, File.GetLastWriteTimeUtc(file)), out var cached)) item.Info = cached;
            _songs.Add(item);
        }

        // Swap the whole list in one go (one reset instead of 500 change notifications).
        var usedGroups = _songs.Select(s => s.Group).Distinct();
        Rows.ReplaceAll(usedGroups.Cast<LibraryRow>().Concat(_songs));

        BuildUsageOptions();

        RefreshView();
        Selected = _songs.FirstOrDefault(s => s.SongPath == keepSelected);
        _ = LoadLengthsAsync();
    }

    private async Task LoadLengthsAsync()
    {
        int generation = ++_loadGeneration;
        var todo = _songs.Where(s => s.Info is null).ToList();
        if (todo.Count == 0) return;
        var results = await Task.Run(() => todo.Select(s =>
        {
            BrstmInfo? info;
            DateTime stamp = File.GetLastWriteTimeUtc(s.FilePath);
            try { info = BrstmFile.ReadHeaderInfo(s.FilePath); } catch (IOException) { info = null; }
            return (s, stamp, info);
        }).ToList());
        if (generation != _loadGeneration) return; // reloaded meanwhile
        foreach (var (s, stamp, info) in results)
        {
            InfoCache[(s.FilePath, stamp)] = info;
            s.Info = info;
        }
        if (Sort != SortOptions[0]) RowsView.Refresh(); // lengths affect the order
        OnPropertyChanged(nameof(SelectedDetails));
    }

    // ---- selection, playback, delete ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(PlayCommand), nameof(AddToStageCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedDetails))]
    private LibraryRow? _selected;

    private LibrarySongItem? SelectedSong => Selected as LibrarySongItem;

    partial void OnSelectedChanged(LibraryRow? value) =>
        _ = Preview.ShowAsync((value as LibrarySongItem)?.FilePath, (value as LibrarySongItem)?.Name);

    public string SelectedDetails => SelectedSong is not { } s ? "" :
        $"{s.Name} ({s.Series}). {(s.Info is { } i ? $"{s.LengthText}, {(i.ChannelCount == 1 ? "mono" : "stereo")}, {s.LoopText.ToLowerInvariant()}. " : "")}{s.UsedByText}.";

    private bool HasSong => SelectedSong is not null;

    /// <summary>Set by the main window: files picked with "Add BRSTM files…" or dropped on the list are handed to it.</summary>
    public Action<IReadOnlyList<string>>? ImportFilesRequested { get; set; }

    [RelayCommand]
    private void AddFiles()
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var files = Dialogs.PickFiles(owner, "Choose BRSTM songs to add to your library", "BRSTM songs (*.brstm)|*.brstm|All files|*.*");
        if (files.Length > 0) ImportFilesRequested?.Invoke(files);
    }

    /// <summary>Set by the main window: "Add to a stage…" hands it the selected song (path and display name).</summary>
    public Action<string, string>? AddToStageRequested { get; set; }

    [RelayCommand(CanExecute = nameof(HasSong))]
    private void AddToStage()
    {
        if (SelectedSong is { } s) AddToStageRequested?.Invoke(s.SongPath, s.Name);
    }

    [RelayCommand(CanExecute = nameof(HasSong))]
    private void Play() => Preview.TogglePlay(); // Space / double-click: play or pause in the player strip

    [RelayCommand(CanExecute = nameof(HasSong))]
    private void Delete()
    {
        if (SelectedSong is not { } row) return;
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (row.UsedBy.Count > 0)
        {
            Dialogs.Info(owner, $"\"{row.Name}\" can't be deleted because it's still used by: {string.Join(", ", row.UsedBy)}.\n\nRemove it from those stages first.");
            return;
        }
        if (!Dialogs.Confirm(owner, $"Delete \"{row.Name}\" from your library for good?\n\nNo build uses it. This can't be undone.")) return;
        if (string.Equals(Player.CurrentFile, row.FilePath, StringComparison.OrdinalIgnoreCase)) Player.Stop();
        _library.DeleteSong(row.SongPath);
        Reload();
    }

    [RelayCommand]
    private void ShowInExplorer() => Dialogs.ShowInExplorer(SelectedSong?.FilePath ?? _library.SongsDir);
}

internal static class ComparerExtensions
{
    /// <summary>Case-insensitive path + exact time, for the header cache key.</summary>
    public static IEqualityComparer<(string, DateTime)> ToTupleComparer(this StringComparer paths) => new TupleComparer(paths);

    private sealed class TupleComparer(StringComparer paths) : IEqualityComparer<(string, DateTime)>
    {
        public bool Equals((string, DateTime) x, (string, DateTime) y) => paths.Equals(x.Item1, y.Item1) && x.Item2 == y.Item2;
        public int GetHashCode((string, DateTime) obj) => HashCode.Combine(paths.GetHashCode(obj.Item1), obj.Item2);
    }
}
