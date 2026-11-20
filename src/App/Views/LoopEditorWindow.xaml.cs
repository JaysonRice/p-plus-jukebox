using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>Make a looping BRSTM from an audio file. DialogResult true = <see cref="LoopEditorViewModel.OutputFile"/> is ready.</summary>
public partial class LoopEditorWindow : Window
{
    private readonly LoopEditorViewModel _vm;

    public LoopEditorWindow(LoopEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Wave.MarkerMoved += (_, e) => _vm.MoveMarker(e.Marker, e.Sample);
        Wave.SeekRequested += (_, sample) => _vm.SeekTo(sample);
        Loaded += async (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
            await _vm.LoadAsync();
        };
        Closing += (_, e) => { if (_vm.IsWorking) e.Cancel = true; }; // let the encode finish
        Closed += (_, _) => _vm.Dispose();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public LoopEditorViewModel ViewModel => _vm;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Space plays/stops, except while typing.
        if (e.Key == Key.Space && Keyboard.FocusedElement is not (TextBox or ComboBox or ComboBoxItem))
        {
            _vm.TogglePlay();
            e.Handled = true;
        }
    }

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); // Enter applies the typed time
        box.SelectAll();
        e.Handled = true;
    }

    private async void Make_Click(object sender, RoutedEventArgs e)
    {
        // A typed time that hasn't lost focus yet still counts.
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        string? error = await _vm.MakeAsync();
        if (error is not null) { Dialogs.Info(this, error); return; }
        DialogResult = true;
    }
}
