using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

public partial class LibraryWindow : Window
{
    private readonly LibraryViewModel _vm;

    public LibraryWindow(LibraryViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        SongGrid.SelectionMode = vm.IsPicking ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
        Loaded += (_, _) => SearchBox.Focus();
        Closed += (_, _) => vm.Player.Stop();
    }

    /// <summary>Songs chosen in "add to stage" mode, in the order shown.</summary>
    public IReadOnlyList<string> PickedSongs { get; private set; } = [];

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        PickedSongs = SongGrid.SelectedItems.OfType<LibrarySongRow>()
            .OrderBy(r => SongGrid.Items.IndexOf(r))
            .Select(r => r.SongPath)
            .ToList();
        if (PickedSongs.Count == 0) { Services.Dialogs.Info(this, "Select one or more songs first (Ctrl+click to pick several)."); return; }
        DialogResult = true;
    }

    private void SongGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsPicking) Add_Click(sender, e);
        else if (_vm.PlayCommand.CanExecute(null)) _vm.PlayCommand.Execute(null);
    }

    private void SongGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _vm.PlayCommand.CanExecute(null)) { _vm.PlayCommand.Execute(null); e.Handled = true; }
    }
}
