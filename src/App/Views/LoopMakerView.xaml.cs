using System.Windows;
using System.Windows.Controls;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>The Loop Maker tab. DataContext is a <see cref="LoopMakerViewModel"/>.</summary>
public partial class LoopMakerView : UserControl
{
    public LoopMakerView() => InitializeComponent();

    private LoopMakerViewModel? Vm => DataContext as LoopMakerViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is null || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;
        if (Vm.Editor is { IsWorking: true }) return; // busy making the current one
        await Vm.OpenAsync(files[0]);
    }
}
