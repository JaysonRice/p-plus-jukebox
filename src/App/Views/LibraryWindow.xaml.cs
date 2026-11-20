using System.Windows;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

public partial class LibraryWindow : Window
{
    public LibraryWindow(LibraryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        List.PickRequested += (_, _) => Add_Click(this, new RoutedEventArgs());
        Loaded += (_, _) => List.FocusSearch();
        Closed += (_, _) => vm.Player.Stop();
    }

    /// <summary>Songs chosen, in the order shown.</summary>
    public IReadOnlyList<string> PickedSongs { get; private set; } = [];

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        PickedSongs = List.SelectedSongPaths;
        if (PickedSongs.Count == 0) { Services.Dialogs.Info(this, "Select one or more songs first (Ctrl+click to pick several)."); return; }
        DialogResult = true;
    }
}
