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
    public virtual string Series => "";
    public virtual string LengthText => "";
    public virtual string LoopText => "";
    public virtual string UsedByText => "";
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

    public override string DisplayText => Name;
    public override string Series => Group.DisplayName;
    public override string AddedText => Added.ToString("d");
    public override string UsedByText => UsedBy.Count == 0 ? "Not used by any build" : "Used by " + string.Join(", ", UsedBy);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText), nameof(LoopText))]
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
    public sealed record Option(string Label, Func<LibrarySongItem, bool> Matches)
    {
        public override string ToString() => Label;
    }

    public sealed record SortOption(string Label, Comparison<LibrarySongItem> Compare)
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
        PickForStage = pickForStage;
        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is LibraryRow r && Shows(r);
        _sort = SortOptions[0]; // before CustomSort: the comparer reads it
        RowsView.CustomSort = new Comparer(this);
        Reload();
    }

    public PreviewPlayer Player { get; }
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
        new("Name (A to Z)", (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)),
        new("Longest first", (a, b) => Nullable.Compare(b.Length, a.Length)),
        new("Shortest first", (a, b) => CompareShortest(a.Length, b.Length)),
        new("Recently added", (a, b) => b.Added.CompareTo(a.Added)),
        new("Used by the most builds", (a, b) => b.UsedBy.Count.CompareTo(a.UsedBy.Count)),
    ];

    private static int CompareShortest(TimeSpan? a, TimeSpan? b) =>
        (a, b) switch { (null, null) => 0, (null, _) => 1, (_, null) => -1, _ => a.Value.CompareTo(b.Value) }; // unknown lengths last

    [ObservableProperty] private SortOption _sort;
    partial void OnSortChanged(SortOption value) => RefreshView();

    [ObservableProperty] private bool _groupBySeries = true;
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
                if (g != 0) return g;
                if (a.IsHeader != b.IsHeader) return a.IsHeader ? -1 : 1;
            }
            if (a is not LibrarySongItem sa || b is not LibrarySongItem sb) return 0;
            int c = vm.Sort.Compare(sa, sb);
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

        // Filter choices follow the current builds.
        string? keepUsage = Usage?.Label;
        UsageOptions.Clear();
        UsageOptions.Add(new Option("All songs", _ => true));
        UsageOptions.Add(new Option("Not used by any build", s => s.UsedBy.Count == 0));
        foreach (var build in builds)
        {
            string name = build.Name;
            UsageOptions.Add(new Option($"Used by \"{name}\"", s => s.UsedBy.Contains(name)));
            UsageOptions.Add(new Option($"Not used by \"{name}\"", s => !s.UsedBy.Contains(name)));
        }
        _usage = UsageOptions.FirstOrDefault(o => o.Label == keepUsage) ?? UsageOptions[0];
        OnPropertyChanged(nameof(Usage));

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
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(PlayCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedDetails))]
    private LibraryRow? _selected;

    private LibrarySongItem? SelectedSong => Selected as LibrarySongItem;

    public string SelectedDetails => SelectedSong is not { } s ? "" :
        $"{s.Name} ({s.Series}). {(s.Info is { } i ? $"{s.LengthText}, {(i.ChannelCount == 1 ? "mono" : "stereo")}, {s.LoopText.ToLowerInvariant()}. " : "")}{s.UsedByText}.";

    private bool HasSong => SelectedSong is not null;

    [RelayCommand(CanExecute = nameof(HasSong))]
    private async Task Play()
    {
        if (SelectedSong is not { } row) return;
        if (Player.IsPlaying && string.Equals(Player.CurrentFile, row.FilePath, StringComparison.OrdinalIgnoreCase)) { Player.Stop(); return; }
        await Player.PlayAsync(row.FilePath, row.Name, StageEditSession.DefaultVolume);
    }

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
