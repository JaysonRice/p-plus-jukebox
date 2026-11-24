using System.Windows;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App.Views;

/// <summary>First run: "Where is your Project+ folder?" then import its music (kept as the Project+ original, plus "My build" to edit).</summary>
public partial class SetupWindow : Window
{
    private readonly string _libraryRoot;
    private ProjectPlusFolder? _pplus;
    private bool _working;

    public SetupWindow(string libraryRoot, bool usedFallback)
    {
        InitializeComponent();
        _libraryRoot = libraryRoot;
        LibraryText.Text = usedFallback
            ? $"Your songs will be kept in {libraryRoot} (the app's own folder can't be written to)."
            : $"Your songs will be kept next to the app, in {libraryRoot}. Keep that folder with the app if you move it.";
        Closing += (_, e) => e.Cancel = _working;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        string? picked = Dialogs.PickFolder(this, "Choose your Project+ folder");
        if (picked is null) return;
        FolderBox.Text = picked;
        try
        {
            _pplus = ProjectPlusFolder.Resolve(picked);
            int stages = _pplus.TracklistNames().Count();
            StatusText.Text = $"✓ Found Project+ with {stages} stage song lists.";
            StartButton.IsEnabled = true;
        }
        catch (MusicLibraryException ex)
        {
            _pplus = null;
            StatusText.Text = ex.Message;
            StartButton.IsEnabled = false;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_pplus is null) return;
        bool existedBefore = Directory.Exists(_libraryRoot);
        SetWorking(true);
        StatusText.Text = "Copying your songs… this can take a minute.";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Progress.Maximum = Math.Max(1, p.Total);
            Progress.Value = p.Done;
            StatusText.Text = $"Copying songs… {p.Done} of {p.Total}";
        });
        try
        {
            var pplus = _pplus;
            await Task.Run(() => AppLibrary.CreateFromProjectPlus(_libraryRoot, pplus, progress));
            _working = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            // Don't leave a half-made library behind (only if this run created the folder).
            if (!existedBefore) try { Directory.Delete(_libraryRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            SetWorking(false);
            StatusText.Text = $"Setup didn't finish: {ex.Message}";
        }
    }

    private void SetWorking(bool working)
    {
        _working = working;
        Progress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !working;
        BrowseButton.IsEnabled = !working;
        CancelButton.IsEnabled = !working;
    }
}
