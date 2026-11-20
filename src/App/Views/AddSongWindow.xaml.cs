using System.Windows;
using PPlusTracklistEditor.App.Services;

namespace PPlusTracklistEditor.App.Views;

/// <summary>Asks for a new song's title and series. The file name is made from them automatically (safe and unique).</summary>
public partial class AddSongWindow : Window
{
    private readonly string _file;
    private readonly PreviewPlayer _player;

    public AddSongWindow(string file, IReadOnlyList<string> seriesFolders, PreviewPlayer player)
    {
        InitializeComponent();
        _file = file;
        _player = player;
        FileText.Text = $"File: {Path.GetFileName(file)}";
        TitleBox.Text = Path.GetFileNameWithoutExtension(file);
        SeriesBox.ItemsSource = seriesFolders;
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
        Closed += (_, _) => { if (string.Equals(_player.CurrentFile, _file, StringComparison.OrdinalIgnoreCase)) _player.Stop(); };
    }

    public string SongTitle => TitleBox.Text.Trim();
    public string Series => SeriesBox.Text.Trim();

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(_player.CurrentFile, _file, StringComparison.OrdinalIgnoreCase)) { _player.Stop(); return; }
        try { await _player.PlayAsync(_file, SongTitle, 80); }
        catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (SongTitle.Length == 0) { Dialogs.Info(this, "Please give the song a title."); TitleBox.Focus(); return; }
        DialogResult = true;
    }
}
