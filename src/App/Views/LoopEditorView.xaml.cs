using System.Windows.Controls;
using System.Windows.Input;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>The loop editor controls. DataContext is a <see cref="LoopEditorViewModel"/>.</summary>
public partial class LoopEditorView : UserControl
{
    public LoopEditorView()
    {
        InitializeComponent();
        Wave.MarkerMoved += (_, e) => Vm?.MoveMarker(e.Marker, e.Sample);
        Wave.SeekRequested += (_, sample) => Vm?.SeekTo(sample);
    }

    private LoopEditorViewModel? Vm => DataContext as LoopEditorViewModel;

    public void FocusTitle()
    {
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    /// <summary>Applies a typed time that hasn't been confirmed yet (box still focused), before making the song.</summary>
    public static void CommitTypedTime()
    {
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>True if Space should play/pause rather than type (focus isn't in a text field).</summary>
    public static bool SpaceMeansPlay => Keyboard.FocusedElement is not (TextBox or ComboBox or ComboBoxItem);

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); // Enter applies the typed time
        box.SelectAll();
        e.Handled = true; // and doesn't press the default button
    }
}
