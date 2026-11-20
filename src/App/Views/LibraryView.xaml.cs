using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>The song list with search/filter/sort/grouping. Hosted by the "Song library" tab and the add-songs picker.</summary>
public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Timeline.SeekRequested += (_, sample) => Vm?.Preview.SeekTo(sample);
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

    /// <summary>Header rows already name the series, so the column only shows when not grouping.</summary>
    private void UpdateSeriesColumn() =>
        SeriesColumn.Visibility = Vm?.GroupBySeries == true ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Raised on double-click of a song in picker mode (= add it).</summary>
    public event EventHandler? PickRequested;

    /// <summary>Selected songs (not headers) in the order shown.</summary>
    public IReadOnlyList<string> SelectedSongPaths =>
        SongGrid.SelectedItems.OfType<LibrarySongItem>()
            .OrderBy(r => SongGrid.Items.IndexOf(r))
            .Select(r => r.SongPath)
            .ToList();

    public void FocusSearch() => SearchBox.Focus();

    private static LibraryRow? RowAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow row) return row.Item as LibraryRow;
        return null;
    }

    /// <summary>Clicking a series header shows or hides its songs.</summary>
    private void SongGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && RowAt(e.OriginalSource) is SeriesGroup group) vm.ToggleGroup(group);
    }

    private void SongGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || RowAt(e.OriginalSource) is not LibrarySongItem) return;
        if (vm.IsPicking) PickRequested?.Invoke(this, EventArgs.Empty);
        else if (vm.PlayCommand.CanExecute(null)) vm.PlayCommand.Execute(null);
    }

    /// <summary>Space: play/stop a song, or open/close a series header. Left/Right also close/open a header.</summary>
    private void SongGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (SongGrid.SelectedItem is SeriesGroup group)
        {
            if (e.Key == Key.Space || (e.Key == Key.Left && group.IsExpanded) || (e.Key == Key.Right && !group.IsExpanded))
            {
                vm.ToggleGroup(group);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Space && vm.PlayCommand.CanExecute(null)) { vm.PlayCommand.Execute(null); e.Handled = true; }
    }
}
