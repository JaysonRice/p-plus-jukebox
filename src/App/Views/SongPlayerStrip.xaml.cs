using System.Windows.Controls;
using PPlusTracklistEditor.App.ViewModels;

namespace PPlusTracklistEditor.App.Views;

/// <summary>The player bar for the selected song. DataContext is a <see cref="SongPreviewViewModel"/>.</summary>
public partial class SongPlayerStrip : UserControl
{
    public SongPlayerStrip()
    {
        InitializeComponent();
        Timeline.SeekRequested += (_, sample) => (DataContext as SongPreviewViewModel)?.SeekTo(sample);
    }
}
