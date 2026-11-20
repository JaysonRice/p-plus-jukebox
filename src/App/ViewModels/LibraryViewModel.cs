using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Audio;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App.ViewModels;

public sealed record LibrarySongRow(string SongPath, string Series, string Name, string FilePath, IReadOnlyList<string> UsedBy)
{
    public string UsedByText => UsedBy.Count == 0 ? "Not used by any build" : "Used by " + string.Join(", ", UsedBy);
    public override string ToString() => $"{Name} ({Series})";
}

/// <summary>
/// Every song in the shared library. Two uses: managing it (preview, delete unused songs, show in Explorer),
/// and picking songs to add to a stage (<c>pickForStage</c> set).
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppLibrary _library;

    public LibraryViewModel(AppLibrary library, PreviewPlayer player, string? pickForStage)
    {
        _library = library;
        Player = player;
        PickForStage = pickForStage;
        SongsView = CollectionViewSource.GetDefaultView(Songs);
        SongsView.Filter = Matches;
        Load();
    }

    public PreviewPlayer Player { get; }
    public string? PickForStage { get; }
    public bool IsPicking => PickForStage is not null;
    public string Heading => IsPicking ? $"Add songs to {PickForStage}" : "Your song library";
    public string LibraryRoot => _library.Root;

    public ObservableCollection<LibrarySongRow> Songs { get; } = [];
    public ICollectionView SongsView { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _onlyUnused;
    [ObservableProperty] private string _countText = "";
    partial void OnSearchTextChanged(string value) => RefreshFilter();
    partial void OnOnlyUnusedChanged(bool value) => RefreshFilter();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(PlayCommand), nameof(ShowInExplorerCommand))]
    private LibrarySongRow? _selected;

    [ObservableProperty] private string _selectedDetails = "";

    partial void OnSelectedChanged(LibrarySongRow? value) => _ = LoadDetailsAsync(value);

    private async Task LoadDetailsAsync(LibrarySongRow? row)
    {
        if (row is null) { SelectedDetails = ""; return; }
        SelectedDetails = row.UsedByText;
        try
        {
            var info = await Task.Run(() => BrstmFile.ReadInfo(row.FilePath));
            if (Selected != row) return;
            string loop = info.Loop is null ? "plays once (no loop)" : "loops";
            SelectedDetails = $"{info.Duration:m\\:ss}, {(info.ChannelCount == 1 ? "mono" : "stereo")}, {loop}. {row.UsedByText}.";
        }
        catch (Exception)
        {
            if (Selected == row) SelectedDetails = $"This file couldn't be read; it may be damaged. {row.UsedByText}.";
        }
    }

    private void Load()
    {
        // Which builds use which songs: read each build's tracklists once (not once per song).
        var usage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var build in _library.Builds())
            foreach (var path in build.SongPathsUsed())
            {
                if (!usage.TryGetValue(path, out var list)) usage[path] = list = [];
                list.Add(build.Name);
            }

        Songs.Clear();
        foreach (var path in _library.Songs.All().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            int slash = path.LastIndexOf('/');
            string series = slash < 0 ? "" : path[..slash];
            string name = slash < 0 ? path : path[(slash + 1)..];
            IReadOnlyList<string> usedBy = usage.TryGetValue(path, out var builds) ? builds : [];
            // A "_b" hurry-up file counts as used when its main song is.
            if (usedBy.Count == 0 && SongStore.HurryUpBase(path) is { } main && usage.TryGetValue(main, out var mainBuilds)) usedBy = mainBuilds;
            Songs.Add(new LibrarySongRow(path, series, name, _library.Songs.FileFor(path), usedBy));
        }
        RefreshFilter();
    }

    private bool Matches(object o)
    {
        if (o is not LibrarySongRow row) return false;
        if (OnlyUnused && row.UsedBy.Count > 0) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => row.SongPath.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshFilter()
    {
        SongsView.Refresh();
        int shown = SongsView.Cast<object>().Count();
        CountText = shown == Songs.Count ? $"{Songs.Count} songs" : $"{shown} of {Songs.Count} songs";
    }

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
        var owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
        if (row.UsedBy.Count > 0)
        {
            Dialogs.Info(owner, $"\"{row.Name}\" can't be deleted because it's still used by: {string.Join(", ", row.UsedBy)}.\n\nRemove it from those stages first.");
            return;
        }
        if (!Dialogs.Confirm(owner, $"Delete \"{row.Name}\" from your library for good?\n\nNo build uses it. This can't be undone.")) return;
        if (string.Equals(Player.CurrentFile, row.FilePath, StringComparison.OrdinalIgnoreCase)) Player.Stop();
        _library.DeleteSong(row.SongPath);
        Load();
    }

    [RelayCommand]
    private void ShowInExplorer() => Dialogs.ShowInExplorer(Selected?.FilePath ?? _library.SongsDir);
}
