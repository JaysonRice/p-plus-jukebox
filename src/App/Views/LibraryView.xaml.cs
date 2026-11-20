using System.Windows.Controls;
using System.Windows.Input;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>The song list with search/filter/sort/grouping. Hosted by the "Song library" tab and the add-songs picker.</summary>
public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            SongGrid.SelectionMode = Vm?.IsPicking == true ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
            if (e.OldValue is LibraryViewModel old) old.PropertyChanged -= OnVmChanged;
            if (Vm is { } vm) { vm.PropertyChanged += OnVmChanged; UpdateSeriesColumn(); }
        };
    }

    private LibraryViewModel? Vm => DataContext as LibraryViewModel;

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.GroupBySeries)) UpdateSeriesColumn();
    }

    /// <summary>The group header already names the series, so the column only shows when not grouping.</summary>
    private void UpdateSeriesColumn() =>
        SeriesColumn.Visibility = Vm?.GroupBySeries == true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>Raised on double-click in picker mode (= add the song).</summary>
    public event EventHandler? PickRequested;

    /// <summary>Selected songs in the order shown.</summary>
    public IReadOnlyList<string> SelectedSongPaths =>
        SongGrid.SelectedItems.OfType<LibrarySongItem>()
            .OrderBy(r => SongGrid.Items.IndexOf(r))
            .Select(r => r.SongPath)
            .ToList();

    public void FocusSearch() => SearchBox.Focus();

    private void SongGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || SongGrid.SelectedItem is null) return;
        if (vm.IsPicking) PickRequested?.Invoke(this, EventArgs.Empty);
        else if (vm.PlayCommand.CanExecute(null)) vm.PlayCommand.Execute(null);
    }

    private void SongGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Vm?.PlayCommand.CanExecute(null) == true) { Vm.PlayCommand.Execute(null); e.Handled = true; }
    }
}
