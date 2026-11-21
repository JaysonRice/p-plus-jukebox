using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>The song list with search/filter/sort/grouping. Hosted by the "Song Library" tab and the add-songs picker.</summary>
public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            SongGrid.SelectionMode = Vm?.IsPicking == true ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
            if (e.OldValue is LibraryViewModel old) old.PropertyChanged -= OnVmChanged;
            if (Vm is { } vm) { vm.PropertyChanged += OnVmChanged; UpdateSeriesColumn(); UpdateSortArrow(); }
        };
    }

    private LibraryViewModel? Vm => DataContext as LibraryViewModel;

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.GroupBySeries)) UpdateSeriesColumn();
        if (e.PropertyName is nameof(LibraryViewModel.Sort) or nameof(LibraryViewModel.Descending)) UpdateSortArrow();
    }

    /// <summary>Files dragged over the list can be added (not in the add-to-stage picker, which only chooses).</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = Vm is { IsPicking: false } && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is not { IsPicking: false } vm || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        e.Handled = true;
        // Let the drag finish before the dialog opens, so the file manager isn't left waiting on us.
        Dispatcher.BeginInvoke(() => vm.ImportFilesRequested?.Invoke(paths));
    }

    /// <summary>A column heading was clicked: the view model flips the direction or switches to that column. (We sort ourselves.)</summary>
    private void SongGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        Vm?.SortByColumn(e.Column.Header as string ?? "");
    }

    /// <summary>The arrow in the heading of the column being sorted by.</summary>
    private void UpdateSortArrow()
    {
        if (Vm is not { } vm) return;
        foreach (var column in SongGrid.Columns)
            column.SortDirection = column.Header as string == vm.Sort.ColumnHeader
                ? (vm.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending)
                : null;
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
