using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>Pick the stage a library song should go on. <see cref="Chosen"/> is set when "Add to this stage" is pressed.</summary>
public partial class StagePickerWindow : Window
{
    private readonly ICollectionView _view;

    public StagePickerWindow(string songName, IReadOnlyList<StageCardViewModel> stages, StageCardViewModel? current)
    {
        InitializeComponent();
        Prompt.Text = $"Which stage should \"{songName}\" be added to?";
        _view = CollectionViewSource.GetDefaultView(stages);
        _view.Filter = o => o is StageCardViewModel s &&
            (string.IsNullOrWhiteSpace(SearchBox.Text) || s.DisplayName.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        List.ItemsSource = _view;
        List.SelectedItem = current;
        Loaded += (_, _) =>
        {
            if (current is not null) List.ScrollIntoView(current);
            SearchBox.Focus();
        };
    }

    public StageCardViewModel? Chosen { get; private set; }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not StageCardViewModel stage)
        {
            Services.Dialogs.Info(this, "Click the stage you want first.");
            return;
        }
        Chosen = stage;
        DialogResult = true;
    }

    private void List_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (List.SelectedItem is not null && e.OriginalSource is DependencyObject) Ok_Click(sender, e);
    }
}
