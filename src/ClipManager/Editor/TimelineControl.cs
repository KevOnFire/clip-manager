using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClipManager.Core.Editing;

namespace ClipManager.Editor;

/// <summary>
/// Die Zeitleiste im Editor. Zeichnet Filmstreifen, behaltene und entfernte Teile,
/// Anfang/Ende-Griffe, Schnittpunkte und den Abspielkopf. Selbst gezeichnet (OnRender),
/// weil das schneller und einfacher ist als viele einzelne Steuerelemente.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double StripTop = 6;
    private const double StripHeight = 64;
    private const double RulerHeight = 18;
    private const double HandleWidth = 10;
    private const double GrabDistance = 9;

    /// <summary>Rand links/rechts, damit die Griffe außerhalb des Filmstreifens Platz haben.</summary>
    private const double Pad = HandleWidth;

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
    private static readonly Brush OutsideBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x10)));
    private static readonly Brush RemovedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x5A, 0x10, 0x14)));
    private static readonly Pen RemovedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0x40, 0x4C)), 2));
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)));
    private static readonly Pen AccentPen = Frozen(new Pen(AccentBrush, 3));
    private static readonly Pen SplitPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Brush PlayheadBrush = Brushes.White;
    private static readonly Pen PlayheadPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Pen TickPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)), 1));
    private static readonly Brush TickTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)));
    private static readonly Typeface Font = new("Segoe UI");

    private EditTimeline? _timeline;
    private ImageSource? _filmstrip;
    private TimeSpan _position;
    private DragMode _drag;
    private TimeSpan _dragValue;

    public TimelineControl()
    {
        Height = StripTop + StripHeight + RulerHeight;
        Focusable = false;
        ClipToBounds = true;
    }

    private enum DragMode
    {
        None,
        Playhead,
        In,
        Out,
    }

    /// <summary>Der Nutzer hat auf der Zeitleiste geklickt oder gezogen.</summary>
    public event EventHandler<TimeSpan>? SeekRequested;

    /// <summary>Ziehen des Abspielkopfs beginnt/endet (der Player pausiert dabei).</summary>
    public event EventHandler<bool>? ScrubbingChanged;

    public EditTimeline? Timeline
    {
        get => _timeline;
        set
        {
            if (_timeline != null)
            {
                _timeline.Changed -= OnTimelineChanged;
            }

            _timeline = value;
            if (_timeline != null)
            {
                _timeline.Changed += OnTimelineChanged;
            }

            InvalidateVisual();
        }
    }

    public ImageSource? Filmstrip
    {
        get => _filmstrip;
        set
        {
            _filmstrip = value;
            InvalidateVisual();
        }
    }

    public TimeSpan Position
    {
        get => _position;
        set
        {
            if (_position != value)
            {
                _position = value;
                InvalidateVisual();
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var strip = new Rect(Pad, StripTop, Math.Max(0, width - 2 * Pad), StripHeight);
        dc.DrawRectangle(Background, null, new Rect(0, 0, width, ActualHeight));
        if (_timeline is not { } tl || tl.Duration <= TimeSpan.Zero || width <= 0)
        {
            return;
        }

        if (_filmstrip != null)
        {
            dc.DrawImage(_filmstrip, strip);
        }

        var inTime = _drag == DragMode.In ? _dragValue : tl.In;
        var outTime = _drag == DragMode.Out ? _dragValue : tl.Out;
        var inX = X(inTime);
        var outX = X(outTime);

        // Weggeschnittener Anfang und Ende abdunkeln
        if (inX > strip.Left)
        {
            dc.DrawRectangle(OutsideBrush, null, new Rect(strip.Left, StripTop, inX - strip.Left, StripHeight));
        }

        if (outX < strip.Right)
        {
            dc.DrawRectangle(OutsideBrush, null, new Rect(outX, StripTop, strip.Right - outX, StripHeight));
        }

        // Entfernte Teile rot mit Kreuz
        foreach (var segment in tl.Segments.Where(s => s.Removed))
        {
            var start = Math.Max(X(segment.Start), inX);
            var end = Math.Min(X(segment.End), outX);
            if (end <= start)
            {
                continue;
            }

            var rect = new Rect(start, StripTop, end - start, StripHeight);
            dc.DrawRectangle(RemovedBrush, null, rect);
            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawLine(RemovedPen, rect.TopLeft, rect.BottomRight);
            dc.DrawLine(RemovedPen, rect.BottomLeft, rect.TopRight);
            dc.Pop();
        }

        // Rahmen um den behaltenen Bereich
        dc.DrawLine(AccentPen, new Point(inX, StripTop + 1.5), new Point(outX, StripTop + 1.5));
        dc.DrawLine(AccentPen, new Point(inX, StripTop + StripHeight - 1.5), new Point(outX, StripTop + StripHeight - 1.5));

        foreach (var split in tl.VisibleSplits)
        {
            var x = X(split);
            if (x > inX && x < outX)
            {
                dc.DrawLine(SplitPen, new Point(x, StripTop), new Point(x, StripTop + StripHeight));
            }
        }

        DrawHandle(dc, inX, isIn: true);
        DrawHandle(dc, outX, isIn: false);
        DrawRuler(dc, tl.Duration, width);

        // Abspielkopf: nur innerhalb des Filmstreifens, nicht darüber
        var px = X(_position);
        dc.DrawLine(PlayheadPen, new Point(px, StripTop), new Point(px, StripTop + StripHeight));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(px - 6, StripTop), true, true);
            g.LineTo(new Point(px + 6, StripTop), true, false);
            g.LineTo(new Point(px, StripTop + 8), true, false);
        }

        head.Freeze();
        dc.DrawGeometry(PlayheadBrush, null, head);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_timeline == null)
        {
            return;
        }

        var x = e.GetPosition(this).X;
        _drag = HitTest(x);
        _dragValue = TimeAt(x);
        CaptureMouse();
        ScrubbingChanged?.Invoke(this, true);
        if (_drag == DragMode.Playhead)
        {
            SeekRequested?.Invoke(this, _dragValue);
        }
        else
        {
            _dragValue = _drag == DragMode.In ? _timeline.In : _timeline.Out;
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_timeline == null)
        {
            return;
        }

        var x = e.GetPosition(this).X;
        if (_drag == DragMode.None)
        {
            Cursor = HitTest(x) == DragMode.Playhead ? Cursors.Hand : Cursors.SizeWE;
            return;
        }

        var t = TimeAt(x);
        if (_drag == DragMode.Playhead)
        {
            SeekRequested?.Invoke(this, t);
            return;
        }

        // Anfang/Ende beim Ziehen nur in der Vorschau verschieben, übernommen wird beim Loslassen
        // (sonst landet jeder Pixel einzeln im Rückgängig-Verlauf).
        _dragValue = _drag == DragMode.In
            ? Min(t, _timeline.Out - EditTimeline.MinSegment)
            : Max(t, _timeline.In + EditTimeline.MinSegment);
        SeekRequested?.Invoke(this, _dragValue);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        FinishDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishDrag();
    }

    private void FinishDrag()
    {
        var mode = _drag;
        if (mode == DragMode.None)
        {
            return;
        }

        _drag = DragMode.None;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (mode == DragMode.In)
        {
            _timeline?.SetIn(_dragValue);
        }
        else if (mode == DragMode.Out)
        {
            _timeline?.SetOut(_dragValue);
        }

        ScrubbingChanged?.Invoke(this, false);
        InvalidateVisual();
    }

    private DragMode HitTest(double x)
    {
        if (_timeline == null)
        {
            return DragMode.None;
        }

        var inDist = Math.Abs(x - (X(_timeline.In) - HandleWidth / 2));
        var outDist = Math.Abs(x - (X(_timeline.Out) + HandleWidth / 2));
        if (Math.Min(inDist, outDist) > GrabDistance)
        {
            return DragMode.Playhead;
        }

        return inDist <= outDist ? DragMode.In : DragMode.Out;
    }

    private void DrawHandle(DrawingContext dc, double x, bool isIn)
    {
        // Griffe liegen außen am behaltenen Bereich, damit der Abspielkopf am Anfang
        // innerhalb des gelben Rahmens steht und nicht unter dem Griff.
        var left = isIn ? x - HandleWidth : x;
        dc.DrawRoundedRectangle(AccentBrush, null, new Rect(left, StripTop, HandleWidth, StripHeight), 3, 3);
        var mid = left + HandleWidth / 2;
        var grip = new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0x30, 0x00)), 1.5);
        dc.DrawLine(grip, new Point(mid - 1.5, StripTop + 24), new Point(mid - 1.5, StripTop + StripHeight - 24));
        dc.DrawLine(grip, new Point(mid + 1.5, StripTop + 24), new Point(mid + 1.5, StripTop + StripHeight - 24));
    }

    private void DrawRuler(DrawingContext dc, TimeSpan duration, double width)
    {
        // Abstand der Markierungen so wählen, dass ca. alle 80 Pixel eine Zahl steht.
        var seconds = duration.TotalSeconds;
        double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        var usable = Math.Max(1, width - 2 * Pad);
        var step = steps.FirstOrDefault(s => usable / (seconds / s) >= 80, 3600);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var y = StripTop + StripHeight;
        for (var s = 0.0; s <= seconds; s += step)
        {
            var x = X(TimeSpan.FromSeconds(s));
            dc.DrawLine(TickPen, new Point(x, y), new Point(x, y + 4));
            var text = new FormattedText(
                TimeText.Format(TimeSpan.FromSeconds(s), tenths: false),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Font,
                10,
                TickTextBrush,
                dpi);
            var tx = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, width - text.Width));
            dc.DrawText(text, new Point(tx, y + 3));
        }
    }

    private double X(TimeSpan t) =>
        _timeline is { Duration.TotalSeconds: > 0 } tl
            ? Pad + t.TotalSeconds / tl.Duration.TotalSeconds * Math.Max(0, ActualWidth - 2 * Pad)
            : Pad;

    private TimeSpan TimeAt(double x)
    {
        if (_timeline is not { } tl || ActualWidth <= 0)
        {
            return TimeSpan.Zero;
        }

        var ratio = Math.Clamp((x - Pad) / Math.Max(1, ActualWidth - 2 * Pad), 0, 1);
        return TimeSpan.FromSeconds(ratio * tl.Duration.TotalSeconds);
    }

    private void OnTimelineChanged(object? sender, EventArgs e) => InvalidateVisual();

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
