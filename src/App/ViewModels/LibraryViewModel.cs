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

/// <summary>A series folder in the library list (a collapsible group). Songs with no folder share the "No series" group.</summary>
public sealed partial class SeriesGroup(string key) : ObservableObject
{
    public string Key { get; } = key;
    public string DisplayName => Key.Length == 0 ? "No series" : Key;
    /// <summary>Groups sort by name, with "No series" last.</summary>
    public string SortKey => Key.Length == 0 ? "￿" : Key;
    [ObservableProperty] private bool _isExpanded = true;
    public override string ToString() => DisplayName;
}

/// <summary>One song in the library list.</summary>
public sealed partial class LibrarySongItem : ObservableObject
{
    public LibrarySongItem(string songPath, string filePath, SeriesGroup group, IReadOnlyList<string> usedBy, DateTime added)
    {
        SongPath = songPath;
        FilePath = filePath;
        Group = group;
        UsedBy = usedBy;
        Added = added;
        int slash = songPath.LastIndexOf('/');
        Name = slash < 0 ? songPath : songPath[(slash + 1)..];
    }

    public string SongPath { get; }
    public string Name { get; }
    public string FilePath { get; }
    public SeriesGroup Group { get; }
    public string Series => Group.DisplayName;
    public IReadOnlyList<string> UsedBy { get; }
    public DateTime Added { get; }
    public string AddedText => Added.ToString("d");
    public string UsedByText => UsedBy.Count == 0 ? "Not used by any build" : "Used by " + string.Join(", ", UsedBy);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText), nameof(LoopText))]
    private BrstmInfo? _info;

    public TimeSpan? Length => Info?.Duration;
    public string LengthText => Info is { } i ? i.Duration.ToString(i.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss") : "";
    public string LoopText => Info is null ? "" : Info.Loop is null ? "Plays once" : "Loops";

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
    private int _loadGeneration;

    public LibraryViewModel(AppLibrary library, PreviewPlayer player, string? pickForStage)
    {
        _library = library;
        Player = player;
        PickForStage = pickForStage;
        SongsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Songs);
        SongsView.Filter = o => o is LibrarySongItem s && Matches(s);
        _sort = SortOptions[0];
        Reload();
        ApplyView();
    }

    public PreviewPlayer Player { get; }
    public string? PickForStage { get; }
    public bool IsPicking => PickForStage is not null;
    public string Heading => IsPicking ? $"Add songs to {PickForStage}" : "Your song library";

    public ObservableCollection<LibrarySongItem> Songs { get; } = [];
    public ListCollectionView SongsView { get; }

    // ---- filters ----

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => RefreshFilter();

    public ObservableCollection<Option> UsageOptions { get; } = [];

    [ObservableProperty] private Option? _usage;
    partial void OnUsageChanged(Option? value) => RefreshFilter();

    private bool Matches(LibrarySongItem s)
    {
        if (Usage is { } u && !u.Matches(s)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        string haystack = s.Name + " " + s.Series;
        return SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

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
    partial void OnSortChanged(SortOption value) => ApplyView();

    [ObservableProperty] private bool _groupBySeries = true;
    partial void OnGroupBySeriesChanged(bool value) => ApplyView();

    private void ApplyView()
    {
        using (SongsView.DeferRefresh())
        {
            SongsView.GroupDescriptions.Clear();
            if (GroupBySeries) SongsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LibrarySongItem.Group)));
            SongsView.CustomSort = new Comparer(this);
        }
        UpdateCount();
    }

    /// <summary>Groups appear in the order of their first item, so when grouping, sort by series first.</summary>
    private sealed class Comparer(LibraryViewModel vm) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (LibrarySongItem)x!;
            var b = (LibrarySongItem)y!;
            if (vm.GroupBySeries)
            {
                int g = string.Compare(a.Group.SortKey, b.Group.SortKey, StringComparison.OrdinalIgnoreCase);
                if (g != 0) return g;
            }
            int c = vm.Sort.Compare(a, b);
            return c != 0 ? c : string.Compare(a.SongPath, b.SongPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private void ExpandAll() { foreach (var g in _groups.Values) g.IsExpanded = true; }

    [RelayCommand]
    private void CollapseAll() { foreach (var g in _groups.Values) g.IsExpanded = false; }

    [ObservableProperty] private string _countText = "";

    private void RefreshFilter()
    {
        SongsView.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        int shown = SongsView.Count;
        CountText = shown == Songs.Count ? $"{Songs.Count} songs" : $"{shown} of {Songs.Count} songs";
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

        string? keepSelected = Selected?.SongPath;
        Songs.Clear();
        foreach (var path in _library.Songs.All())
        {
            int slash = path.LastIndexOf('/');
            string series = slash < 0 ? "" : path[..slash];
            if (!_groups.TryGetValue(series, out var group)) _groups[series] = group = new SeriesGroup(series);
            IReadOnlyList<string> usedBy = usage.TryGetValue(path, out var b) ? b : [];
            // A "_b" hurry-up file counts as used when its main song is.
            if (usedBy.Count == 0 && SongStore.HurryUpBase(path) is { } main && usage.TryGetValue(main, out var mb)) usedBy = mb;
            string file = _library.Songs.FileFor(path);
            var item = new LibrarySongItem(path, file, group, usedBy, File.GetCreationTime(file));
            if (InfoCache.TryGetValue((file, File.GetLastWriteTimeUtc(file)), out var cached)) item.Info = cached;
            Songs.Add(item);
        }

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
        Usage = UsageOptions.FirstOrDefault(o => o.Label == keepUsage) ?? UsageOptions[0];

        RefreshFilter();
        Selected = Songs.FirstOrDefault(s => s.SongPath == keepSelected);
        _ = LoadLengthsAsync();
    }

    private async Task LoadLengthsAsync()
    {
        int generation = ++_loadGeneration;
        var todo = Songs.Where(s => s.Info is null).ToList();
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
        if (Sort != SortOptions[0]) SongsView.Refresh(); // lengths affect the order
        OnPropertyChanged(nameof(SelectedDetails));
    }

    // ---- selection, playback, delete ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(PlayCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedDetails))]
    private LibrarySongItem? _selected;

    public string SelectedDetails => Selected is not { } s ? "" :
        $"{s.Name} ({s.Series}). {(s.Info is { } i ? $"{s.LengthText}, {(i.ChannelCount == 1 ? "mono" : "stereo")}, {s.LoopText.ToLowerInvariant()}. " : "")}{s.UsedByText}.";

    private bool HasSelection => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Play()
    {
        if (Selected is not { } row) return;
        if (Player.IsPlaying && string.Equals(Player.CurrentFile, row.FilePath, StringComparison.OrdinalIgnoreCase)) { Player.Stop(); return; }
        await Player.PlayAsync(row.FilePath, row.Name, StageEditSession.DefaultVolume);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (Selected is not { } row) return;
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
    private void ShowInExplorer() => Dialogs.ShowInExplorer(Selected?.FilePath ?? _library.SongsDir);
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
