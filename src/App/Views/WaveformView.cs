using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Audio;

namespace PPlusTracklistEditor.App.Views;

public enum LoopMarker { Start, End }

public sealed class MarkerMovedEventArgs(LoopMarker marker, int sample, bool finished) : EventArgs
{
    public LoopMarker Marker { get; } = marker;
    public int Sample { get; } = sample;
    /// <summary>True when the mouse button was let go (end of a drag).</summary>
    public bool Finished { get; } = finished;
}

/// <summary>
/// Draws a song's waveform with the loop start/end markers and the playback position.
/// Mouse: drag a flag to move it; drag the blue line (or click/drag in the time strip) to move the playback position;
/// drag the background to scroll; click it to move the blue line there; wheel to zoom.
///
/// The view (which part of the song is visible) is <see cref="ViewStart"/> + <see cref="ViewLength"/> in samples,
/// both bindable so the window's zoom buttons can set them. The markers are one-way: dragging raises
/// <see cref="MarkerMoved"/> and the view model decides the final position (snapping, limits) and binds it back.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const double RulerHeight = 18;
    private const double HandleWidth = 64, HandleHeight = 18;
    private const double GrabDistance = 7;
    // Colors come from the current theme (ThemeService); the second argument is only a fallback.
    private Brush StartBrush => ThemeService.Brush(this, "LagoonAttentionBrush", Brushes.SteelBlue);
    private Brush EndBrush => ThemeService.Brush(this, "LagoonEndBrush", Brushes.IndianRed);
    private Brush PlayheadBrush => ThemeService.Brush(this, "LagoonPlaybackBrush", Brushes.Teal);
    private Brush OnStartBrush => ThemeService.Brush(this, "LagoonOnAttentionBrush", Brushes.White);
    private Brush OnEndBrush => ThemeService.Brush(this, "LagoonOnEndBrush", Brushes.White);
    // Before the loop start: the intro, which plays once (light). After the loop end: never plays (dark).
    private static readonly Brush IntroBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)));
    private static readonly Brush CutBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)));

    private Geometry? _waveGeometry;
    private (WaveformData?, double, double, double, double) _waveKey;
    private LoopMarker? _dragging;
    private bool _draggingPlayhead;
    private Point _pressPoint;
    private double _pressViewStart;
    private bool _panning, _pressed;

    public WaveformView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
        // Repaint when light/dark changes (the colors are looked up each time it draws).
        Loaded += (_, _) => ThemeService.Changed += Repaint;
        Unloaded += (_, _) => ThemeService.Changed -= Repaint;
    }

    private void Repaint() => Dispatcher.BeginInvoke(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Background);

    // ---- properties ----

    public static readonly DependencyProperty WaveformProperty = Dp<WaveformData?>(nameof(Waveform), null, OnWaveformChanged);
    public WaveformData? Waveform { get => (WaveformData?)GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }

    public static readonly DependencyProperty LoopStartProperty = Dp(nameof(LoopStart), 0);
    public int LoopStart { get => (int)GetValue(LoopStartProperty); set => SetValue(LoopStartProperty, value); }

    public static readonly DependencyProperty LoopEndProperty = Dp(nameof(LoopEnd), 0);
    public int LoopEnd { get => (int)GetValue(LoopEndProperty); set => SetValue(LoopEndProperty, value); }

    public static readonly DependencyProperty ShowLoopProperty = Dp(nameof(ShowLoop), true);
    public bool ShowLoop { get => (bool)GetValue(ShowLoopProperty); set => SetValue(ShowLoopProperty, value); }

    /// <summary>Playback position in samples; -1 hides it.</summary>
    public static readonly DependencyProperty PlayheadProperty = Dp(nameof(Playhead), -1, OnPlayheadChanged);
    public int Playhead { get => (int)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }

    public static readonly DependencyProperty ViewStartProperty = Dp(nameof(ViewStart), 0.0, bindsTwoWay: true);
    public double ViewStart { get => (double)GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }

    public static readonly DependencyProperty ViewLengthProperty = Dp(nameof(ViewLength), 1.0, bindsTwoWay: true);
    public double ViewLength { get => (double)GetValue(ViewLengthProperty); set => SetValue(ViewLengthProperty, value); }

    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(WaveformView),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public event EventHandler<MarkerMovedEventArgs>? MarkerMoved;
    /// <summary>Clicked (without dragging) at this sample.</summary>
    public event EventHandler<int>? SeekRequested;

    private static DependencyProperty Dp<T>(string name, T fallback, PropertyChangedCallback? changed = null, bool bindsTwoWay = false)
    {
        var options = FrameworkPropertyMetadataOptions.AffectsRender;
        if (bindsTwoWay) options |= FrameworkPropertyMetadataOptions.BindsTwoWayByDefault;
        return DependencyProperty.Register(name, typeof(T), typeof(WaveformView), new FrameworkPropertyMetadata(fallback, options, changed));
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static void OnWaveformChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (WaveformView)d;
        if (e.NewValue is WaveformData w) { v.ViewStart = 0; v.ViewLength = Math.Max(1, w.Length); }
    }

    private static void OnPlayheadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Follow playback: when the position runs off the visible part (or jumps back at the loop), scroll to it.
        var v = (WaveformView)d;
        int p = (int)e.NewValue;
        if (p < 0 || v._pressed) return;
        if (p < v.ViewStart || p > v.ViewStart + v.ViewLength) v.SetView(p - v.ViewLength * 0.1, v.ViewLength);
    }

    // ---- coordinates ----

    private double Total => Math.Max(1, Waveform?.Length ?? 1);
    private Rect WaveArea => new(0, RulerHeight, ActualWidth, Math.Max(1, ActualHeight - RulerHeight));
    private double XOf(double sample) => (sample - ViewStart) / ViewLength * ActualWidth;
    private double SampleOf(double x) => ViewStart + x / Math.Max(1, ActualWidth) * ViewLength;

    /// <summary>Sets the visible range, kept inside the song. Shows at least 200 samples (a few milliseconds).</summary>
    public void SetView(double start, double length)
    {
        length = Math.Clamp(length, Math.Min(200, Total), Total);
        start = Math.Clamp(start, 0, Total - length);
        ViewStart = start;
        ViewLength = length;
    }

    // ---- drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < RulerHeight + 4) return;
        var area = WaveArea;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // so the whole area takes mouse input
        dc.DrawRectangle(Tint(0.06), null, area);
        if (Waveform is not { } wave) return;

        DrawRuler(dc, wave.Audio.SampleRate, w);

        dc.DrawGeometry(Foreground, null, WaveGeometry(wave, area));
        dc.DrawLine(new Pen(Tint(0.25), 1), new Point(0, area.Top + area.Height / 2), new Point(w, area.Top + area.Height / 2));

        if (ShowLoop)
        {
            double xs = XOf(LoopStart), xe = XOf(LoopEnd);
            if (xs > 0) dc.DrawRectangle(IntroBrush, null, new Rect(0, area.Top, Math.Min(w, xs), area.Height));
            if (xe < w) dc.DrawRectangle(CutBrush, null, new Rect(Math.Max(0, xe), area.Top, w - Math.Max(0, xe), area.Height));
            DrawMarker(dc, xs, StartBrush, OnStartBrush, "Loop start", alignRight: false, area);
            DrawMarker(dc, xe, EndBrush, OnEndBrush, "Loop end", alignRight: true, area);
        }

        if (Playhead >= 0)
        {
            double x = XOf(Playhead);
            if (x >= -8 && x <= w + 8)
            {
                dc.DrawLine(new Pen(PlayheadBrush, 2), new Point(x, RulerHeight), new Point(x, h));
                // A grab tab in the time strip, so the line can be dragged even where it sits on a flag.
                var tab = new StreamGeometry();
                using (var ctx = tab.Open())
                {
                    ctx.BeginFigure(new Point(x - 6, 2), isFilled: true, isClosed: true);
                    ctx.PolyLineTo([new Point(x + 6, 2), new Point(x + 6, RulerHeight - 6), new Point(x, RulerHeight), new Point(x - 6, RulerHeight - 6)], isStroked: false, isSmoothJoin: false);
                }
                tab.Freeze();
                dc.DrawGeometry(PlayheadBrush, null, tab);
            }
        }
    }

    private Brush Tint(double opacity)
    {
        var b = Foreground.Clone();
        b.Opacity = opacity;
        b.Freeze();
        return b;
    }

    private Geometry WaveGeometry(WaveformData wave, Rect area)
    {
        var key = (wave, ViewStart, ViewLength, area.Width, area.Height);
        if (_waveGeometry is not null && _waveKey == key) return _waveGeometry;

        // One vertical bar per pixel column, from the column's lowest to its highest sample.
        int columns = Math.Max(1, (int)Math.Ceiling(area.Width));
        var min = new float[columns];
        var max = new float[columns];
        wave.Peaks(ViewStart, ViewStart + ViewLength, min, max);
        double mid = area.Top + area.Height / 2, half = area.Height / 2 - 2;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            for (int i = 0; i < columns; i++)
            {
                double top = mid - max[i] * half, bottom = mid - min[i] * half;
                if (bottom - top < 1) { top -= 0.5; bottom += 0.5; }
                ctx.BeginFigure(new Point(i, top), isFilled: true, isClosed: true);
                ctx.PolyLineTo([new Point(i + 1, top), new Point(i + 1, bottom), new Point(i, bottom)], isStroked: false, isSmoothJoin: false);
            }
        }
        g.Freeze();
        _waveGeometry = g;
        _waveKey = key;
        return g;
    }

    private void DrawRuler(DrawingContext dc, int rate, double w)
    {
        double seconds = ViewLength / rate;
        double pixelsPerSecond = w / seconds;
        // Pick the smallest "nice" step that leaves ~80 px between labels.
        double[] steps = [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        double step = steps.FirstOrDefault(s => s * pixelsPerSecond >= 80, 600);
        double first = Math.Ceiling(ViewStart / rate / step) * step;
        var pen = new Pen(Tint(0.4), 1);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double t = first; t * rate <= ViewStart + ViewLength; t += step)
        {
            double x = XOf(t * rate);
            dc.DrawLine(pen, new Point(x, RulerHeight - 5), new Point(x, RulerHeight));
            var text = new FormattedText(RulerLabel(t, step), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, Tint(0.75), dpi);
            dc.DrawText(text, new Point(x + 3, 1));
        }
    }

    private static string RulerLabel(double t, double step)
    {
        var ts = TimeSpan.FromSeconds(Math.Round(t, 3));
        string s = $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
        if (step < 1) s += step < 0.01 ? $".{ts.Milliseconds:000}" : step < 0.1 ? $".{ts.Milliseconds / 10:00}" : $".{ts.Milliseconds / 100}";
        return s;
    }

    private void DrawMarker(DrawingContext dc, double x, Brush brush, Brush textBrush, string label, bool alignRight, Rect area)
    {
        if (x < -HandleWidth || x > ActualWidth + HandleWidth) return;
        dc.DrawLine(new Pen(brush, 2), new Point(x, area.Top), new Point(x, area.Bottom));
        var handle = HandleRect(x, alignRight, area);
        dc.DrawRectangle(brush, null, handle);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, textBrush, dpi);
        dc.DrawText(text, new Point(handle.Left + (handle.Width - text.Width) / 2, handle.Top + (handle.Height - text.Height) / 2));
    }

    // The flag hangs off the marker toward the inside of the loop, so the two never cover each other's line.
    private static Rect HandleRect(double x, bool alignRight, Rect area) =>
        new(alignRight ? x - HandleWidth : x, area.Top, HandleWidth, HandleHeight);

    // ---- mouse ----

    private LoopMarker? MarkerAt(Point p, bool flagsOnly = false)
    {
        if (!ShowLoop || Waveform is null) return null;
        var area = WaveArea;
        double xs = XOf(LoopStart), xe = XOf(LoopEnd);
        if (HandleRect(xe, true, area).Contains(p)) return LoopMarker.End;
        if (HandleRect(xs, false, area).Contains(p)) return LoopMarker.Start;
        if (flagsOnly) return null;
        double ds = Math.Abs(p.X - xs), de = Math.Abs(p.X - xe);
        if (Math.Min(ds, de) > GrabDistance) return null;
        return de <= ds ? LoopMarker.End : LoopMarker.Start;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Waveform is null) return;
        Focus();
        _pressPoint = e.GetPosition(this);
        _pressViewStart = ViewStart;
        // What gets grabbed, in order: a flag, the blue line (its tab, the line itself, or anywhere in the time strip),
        // a flag's line, else the background (drag = scroll, click = move the blue line).
        _dragging = MarkerAt(_pressPoint, flagsOnly: true);
        _draggingPlayhead = _dragging is null && PlayheadAt(_pressPoint);
        if (_dragging is null && !_draggingPlayhead) _dragging = MarkerAt(_pressPoint);
        if (_draggingPlayhead && _pressPoint.Y < RulerHeight) Seek(_pressPoint.X); // clicking the time strip jumps there
        _pressed = true;
        _panning = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (!_pressed)
        {
            Cursor = MarkerAt(p) is not null || PlayheadAt(p) ? Cursors.SizeWE : Cursors.Arrow;
            return;
        }
        if (_draggingPlayhead)
        {
            Seek(p.X);
            return;
        }
        if (_dragging is { } m)
        {
            MarkerMoved?.Invoke(this, new MarkerMovedEventArgs(m, (int)Math.Round(SampleOf(Math.Clamp(p.X, 0, ActualWidth))), finished: false));
            return;
        }
        if (_panning || Math.Abs(p.X - _pressPoint.X) > SystemParameters.MinimumHorizontalDragDistance)
        {
            _panning = true;
            Cursor = Cursors.ScrollWE;
            SetView(_pressViewStart - (p.X - _pressPoint.X) / Math.Max(1, ActualWidth) * ViewLength, ViewLength);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        var p = e.GetPosition(this);
        if (_dragging is { } m)
            MarkerMoved?.Invoke(this, new MarkerMovedEventArgs(m, (int)Math.Round(SampleOf(Math.Clamp(p.X, 0, ActualWidth))), finished: true));
        else if (_draggingPlayhead || !_panning)
            Seek(p.X);
        _pressed = false;
        _draggingPlayhead = false;
        _panning = false;
        _dragging = null;
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
    }

    private bool PlayheadAt(Point p)
    {
        if (Waveform is null || Playhead < 0) return false;
        if (p.Y < RulerHeight) return true;
        return Math.Abs(p.X - XOf(Playhead)) <= GrabDistance;
    }

    private void Seek(double x) => SeekRequested?.Invoke(this, (int)Math.Clamp(Math.Round(SampleOf(Math.Clamp(x, 0, ActualWidth))), 0, Total));

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _draggingPlayhead = false;
        _pressed = false;
        _panning = false;
        _dragging = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Waveform is null) return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SetView(ViewStart - Math.Sign(e.Delta) * ViewLength * 0.15, ViewLength); // Shift+wheel scrolls
            return;
        }
        // Zoom around the mouse, so the spot under the pointer stays put.
        double factor = e.Delta > 0 ? 1 / 1.4 : 1.4;
        double anchor = SampleOf(e.GetPosition(this).X);
        double length = ViewLength * factor;
        double fraction = (anchor - ViewStart) / ViewLength;
        SetView(anchor - fraction * length, length);
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
