using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using PPlusTracklistEditor.App.Services;

namespace PPlusTracklistEditor.App.Views;

/// <summary>
/// A simple playback timeline for the song library: a bar split into intro (plays once), loop (repeats) and the part
/// that never plays, a tick where the loop starts, and a dot for the playback position. Click or drag to skip.
/// Positions are in samples; <see cref="Length"/> is the whole song.
/// </summary>
public sealed class SongTimeline : FrameworkElement
{
    private const double BarHeight = 8, DotRadius = 7;
    // Colors come from the current theme (ThemeService); the second argument is only a fallback.
    private Brush LoopBrush => ThemeService.Brush(this, "LagoonPlaybackBrush", Brushes.Teal);
    private Brush IntroBrush => LoopBrush is SolidColorBrush b ? new SolidColorBrush(Color.FromArgb(0x70, b.Color.R, b.Color.G, b.Color.B)) : LoopBrush;
    private Brush StartBrush => ThemeService.Brush(this, "LagoonAttentionBrush", Brushes.SteelBlue);
    private Brush DotBrush => LoopBrush;
    private static readonly Brush CutBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)));
    private bool _dragging;

    public SongTimeline()
    {
        Focusable = false;
        Cursor = Cursors.Hand;
        MinHeight = 2 * DotRadius + 4;
        Loaded += (_, _) => ThemeService.Changed += Repaint; // colors are looked up each time it draws
        Unloaded += (_, _) => ThemeService.Changed -= Repaint;
    }

    private void Repaint() => Dispatcher.BeginInvoke(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Background);

    public static readonly DependencyProperty LengthProperty = Dp(nameof(Length), 0);
    public int Length { get => (int)GetValue(LengthProperty); set => SetValue(LengthProperty, value); }

    public static readonly DependencyProperty HasLoopProperty = Dp(nameof(HasLoop), false);
    public bool HasLoop { get => (bool)GetValue(HasLoopProperty); set => SetValue(HasLoopProperty, value); }

    public static readonly DependencyProperty LoopStartProperty = Dp(nameof(LoopStart), 0);
    public int LoopStart { get => (int)GetValue(LoopStartProperty); set => SetValue(LoopStartProperty, value); }

    public static readonly DependencyProperty LoopEndProperty = Dp(nameof(LoopEnd), 0);
    public int LoopEnd { get => (int)GetValue(LoopEndProperty); set => SetValue(LoopEndProperty, value); }

    public static readonly DependencyProperty PlayheadProperty = Dp(nameof(Playhead), 0);
    public int Playhead { get => (int)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }

    public event EventHandler<int>? SeekRequested;

    private static DependencyProperty Dp<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(SongTimeline), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // The bar leaves room for the dot at both ends.
    private double Left => DotRadius;
    private double Width_ => Math.Max(1, ActualWidth - 2 * DotRadius);
    private double XOf(int sample) => Left + (Length > 0 ? (double)sample / Length : 0) * Width_;
    private int SampleOf(double x) => Length <= 0 ? 0 : (int)Math.Clamp(Math.Round((x - Left) / Width_ * Length), 0, Length - 1);

    protected override void OnRender(DrawingContext dc)
    {
        double mid = ActualHeight / 2, top = mid - BarHeight / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // whole area clickable
        if (Length <= 0)
        {
            dc.DrawRoundedRectangle(CutBrush, null, new Rect(Left, top, Width_, BarHeight), 4, 4);
            return;
        }
        if (HasLoop)
        {
            double xs = XOf(LoopStart), xe = XOf(LoopEnd);
            Segment(dc, Left, xs, top, IntroBrush);
            Segment(dc, xs, xe, top, LoopBrush);
            Segment(dc, xe, Left + Width_, top, CutBrush);
            dc.DrawRectangle(StartBrush, null, new Rect(xs - 1.5, top - 6, 3, BarHeight + 12)); // loop start tick
        }
        else Segment(dc, Left, Left + Width_, top, LoopBrush);

        double x = XOf(Playhead);
        dc.DrawEllipse(DotBrush, new Pen(ThemeService.Brush(this, "LagoonSurfaceBrush", Brushes.White), 2), new Point(x, mid), DotRadius, DotRadius);
    }

    private static void Segment(DrawingContext dc, double from, double to, double top, Brush brush)
    {
        if (to - from > 0.5) dc.DrawRectangle(brush, null, new Rect(from, top, to - from, BarHeight));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Length <= 0) return;
        _dragging = true;
        CaptureMouse();
        SeekRequested?.Invoke(this, SampleOf(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SeekRequested?.Invoke(this, SampleOf(e.GetPosition(this).X));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => _dragging = false;

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
