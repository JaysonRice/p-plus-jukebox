using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += (_, _) => _vm.Player.Dispose();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmLeaveStage()) e.Cancel = true;
    }

    /// <summary>Space = play/stop, Page Up/Down = previous/next song. Not while typing.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.Editor is not { } editor) return;
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem or Slider or ButtonBase) return;
        switch (e.Key)
        {
            case Key.Space when editor.PlayCommand.CanExecute(null):
                editor.PlayCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.PageUp:
                editor.SelectRelative(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                editor.SelectRelative(+1);
                e.Handled = true;
                break;
            case Key.Delete when Keyboard.FocusedElement is DependencyObject d && IsInside(d, SongGrid) && editor.RemoveCommand.CanExecute(null):
                editor.RemoveCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var d = element; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d == container) return true;
        return false;
    }

    private void SongGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void SongGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _vm.Editor?.AddFiles(files);
    }

    private void SongGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Editor?.PlayCommand.CanExecute(null) == true) _vm.Editor.PlayCommand.Execute(null);
    }
}
