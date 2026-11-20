using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>
/// Asks about songs that are about to be added to the library (picked with "Add BRSTM files…" or dropped on the library):
/// which to add, their titles and series, and whether to recycle the originals afterwards.
/// </summary>
public partial class ImportSongsWindow : Window
{
    public ImportSongsWindow(IEnumerable<ImportSongItem> items, IReadOnlyList<string> seriesOptions)
    {
        InitializeComponent();
        Items = new ObservableCollection<ImportSongItem>(items);
        SeriesOptions = seriesOptions;
        DataContext = this;
        Grid.ItemsSource = Items;
        AllSeriesBox.ItemsSource = seriesOptions;
        foreach (var item in Items) item.PropertyChanged += (_, _) => UpdateCount();
        UpdateCount();
    }

    public ObservableCollection<ImportSongItem> Items { get; }
    public IReadOnlyList<string> SeriesOptions { get; }

    public IReadOnlyList<ImportSongItem> Chosen => Items.Where(i => i.Include).ToList();
    public bool RemoveOriginals => RemoveOriginalsBox.IsChecked == true;

    private void UpdateCount()
    {
        int n = Items.Count(i => i.Include);
        CountText.Text = n == 1 ? "1 song will be added" : $"{n} songs will be added";
        AddButton.IsEnabled = n > 0;
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        string series = AllSeriesBox.Text.Trim();
        foreach (var item in Items) item.Series = series;
        Grid.Items.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true); // a title still being typed counts
        var blank = Chosen.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Title));
        if (blank is not null)
        {
            Dialogs.Info(this, $"Please give \"{blank.FileName}\" a title.");
            Grid.SelectedItem = blank;
            return;
        }
        DialogResult = true;
    }
}
