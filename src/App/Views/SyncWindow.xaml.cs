using System.Windows;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

public partial class SyncWindow : Window
{
    private readonly SyncViewModel _vm;

    public SyncWindow(SyncViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Closing += (_, e) => e.Cancel = vm.IsBusy; // don't close halfway through copying
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        string? picked = Dialogs.PickFolder(this, "Choose your Project+ folder (or the SD card)", _vm.Folder);
        if (picked is not null) await _vm.ChooseFolderAsync(picked);
    }
}
