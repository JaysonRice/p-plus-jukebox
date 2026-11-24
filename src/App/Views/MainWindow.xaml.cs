using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        ThemeService.UseGreenCaption(this); // the caption matches the green band below it
        DataContext = _vm = vm;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += (_, _) => _vm.Player.Dispose();
    }

    /// <summary>"Every stage" opened: show the open stage, which may have been picked from a preset's page.</summary>
    private void ScrollToSelected(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ListBox { IsVisible: true, SelectedItem: { } item } list)
            Dispatcher.BeginInvoke(() => list.ScrollIntoView(item), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmLeaveAll() || !_vm.LoopMaker.ConfirmDiscard()) e.Cancel = true;
    }

    /// <summary>Space = play/stop, Page Up/Down = previous/next song. Not while typing.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.SelectedTab == MainViewModel.StageListsTabIndex)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && _vm.StageLists?.SaveCommand.CanExecute(null) == true)
            {
                _vm.StageLists.SaveCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
        if (_vm.SelectedTab == MainViewModel.LoopMakerTabIndex)
        {
            if (e.Key == Key.Space && LoopEditorView.SpaceMeansPlay && _vm.LoopMaker.Editor is { } loop) { loop.TogglePlay(); e.Handled = true; }
            return;
        }
        if (_vm.SelectedTab != MainViewModel.StagesTabIndex || _vm.Editor is not { } editor) return;
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

    private void SongGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Editor?.PlayCommand.CanExecute(null) == true) _vm.Editor.PlayCommand.Execute(null);
    }
}
