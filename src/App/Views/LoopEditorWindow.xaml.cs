using System.Windows;
using System.Windows.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>Make a looping BRSTM for a stage. DialogResult true = <see cref="LoopEditorViewModel.OutputFile"/> is ready.</summary>
public partial class LoopEditorWindow : Window
{
    private readonly LoopEditorViewModel _vm;

    public LoopEditorWindow(LoopEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            Editor.FocusTitle();
            await _vm.LoadAsync();
        };
        Closing += (_, e) => { if (_vm.IsWorking) e.Cancel = true; }; // let the encode finish
        Closed += (_, _) => _vm.Dispose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && LoopEditorView.SpaceMeansPlay) { _vm.TogglePlay(); e.Handled = true; }
        };
    }

    private async void Make_Click(object sender, RoutedEventArgs e)
    {
        LoopEditorView.CommitTypedTime();
        string? error = await _vm.MakeAsync();
        if (error is not null) { Dialogs.Info(this, error); return; }
        DialogResult = true;
    }
}
