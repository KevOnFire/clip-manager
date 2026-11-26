using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClipManager.Core.Editing;
using ClipManager.Core.Media;

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

    /// <summary>Wo die Spur-Zeilen anfangen und wie hoch jede ist (der Editor richtet die Spur-Köpfe danach aus).</summary>
    public const double LanesTop = 6 + 64 + 18 + 4;
    public const double LaneHeight = 34;

    /// <summary>Höhe einer Objekt-Reihe (Boxen, Text … als Balken).</summary>
    public const double ObjectLaneHeight = 26;

    private static readonly Brush LaneBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)));
    private static readonly Brush WaveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0xB0, 0xFF)));
    private static readonly Brush WaveOffBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5A)));
    private static readonly Brush LaneMutedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0x30, 0x10, 0x14)));
    private static readonly Pen LaneMutedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0x40, 0x4C)), 1.5));
    private const double HandleWidth = 10;
    private const double GrabDistance = 9;
    private const double EdgeGrab = 6;

    // Stellen mit geänderter Lautstärke: blau = leiser, orange = lauter, Linie zeigt den Verlauf.
    private static readonly Brush LaneDuckBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x20, 0x40, 0x90)));
    private static readonly Pen DuckLinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x6C, 0xB4, 0xFF)), 2));
    private static readonly Brush DuckGripBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6C, 0xB4, 0xFF)));
    private static readonly Brush LaneBoostBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x90, 0x50, 0x10)));
    private static readonly Pen BoostLinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x50)), 2));
    private static readonly Brush BoostGripBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x50)));
    private static readonly Pen DuckMidPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush DuckTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xCF, 0xE6, 0xFF)));
    private static readonly Brush MutedGripBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x40, 0x4C)));
    private static readonly Brush RemovedGripBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));
    private static readonly Pen GripOutlinePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x10, 0x10)), 1));
    private static readonly Pen GripLinePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x00, 0x00)), 1));
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xC8, 0x3D)));
    private static readonly Pen SelectionPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)), 1.5));

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
    private double _downX;
    private bool _moved;
    private int _dragTrack;
    private int _dragRange;
    private bool _dragStart;
    private int _selTrack = -1;
    private TimeSpan _selStart;
    private TimeSpan _selEnd;
    private IReadOnlyList<float[]?>? _peaks;
    private readonly Dictionary<int, (double Width, Geometry Geometry)> _waveCache = new();

    // Objekt-Reihen
    private bool _showObjects;
    private string? _selectedOverlay;
    private string? _hoverOverlay;
    private Dictionary<string, int> _overlayLanes = new();
    private int _overlayLaneCount;
    private string? _dragOverlay;
    private double _dragOverlayStart;
    private double _dragOverlayEnd;
    private TimeSpan _dragDownTime;

    /// <summary>Auswahl per Rechtsklick-Ziehen auf dem Filmstreifen (statt in einer Spur).</summary>
    private const int StripSelection = -2;

    public TimelineControl()
    {
        Height = LanesTop;
        Focusable = false;
        ClipToBounds = true;
    }

    private enum DragMode
    {
        None,
        Playhead,
        In,
        Out,
        LaneSelect,
        SplitEdge,
        MutedEdge,
        DuckEdge,
        StripSelect,
        OverlayMove,
        OverlayStart,
        OverlayEnd,
    }

    /// <summary>Mit Rechtsklick auf dem Filmstreifen ein Bereich aufgezogen (Anfang, Ende).</summary>
    public event EventHandler<(TimeSpan Start, TimeSpan End)>? StripRangeSelected;

    /// <summary>Balken eines Objekts angeklickt (Id), oder null für Klick auf eine leere Stelle der Objekt-Reihen.</summary>
    public event EventHandler<string?>? OverlayPressed;

    /// <summary>Maus über einem Objekt-Balken (Id) oder nicht mehr (null).</summary>
    public event EventHandler<string?>? OverlayHovered;

    /// <summary>Unter dem Filmstreifen die Objekt-Reihen statt der Tonspuren zeigen.</summary>
    public bool ShowObjects
    {
        get => _showObjects;
        set
        {
            if (_showObjects != value)
            {
                _showObjects = value;
                UpdateHeight();
                InvalidateVisual();
            }
        }
    }

    public string? SelectedOverlay
    {
        get => _selectedOverlay;
        set
        {
            if (_selectedOverlay != value)
            {
                _selectedOverlay = value;
                InvalidateVisual();
            }
        }
    }

    public string? HoverOverlay
    {
        get => _hoverOverlay;
        set
        {
            if (_hoverOverlay != value)
            {
                _hoverOverlay = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>Anzahl der Objekt-Reihen (immer eine leere mehr als nötig, mindestens 3).</summary>
    public int ObjectLaneRows => Math.Max(3, _overlayLaneCount + 1);

    /// <summary>Höhe des Bereichs unter dem Filmstreifen (Tonspuren oder Objekt-Reihen).</summary>
    public double LanesHeight => _showObjects
        ? ObjectLaneRows * ObjectLaneHeight
        : TrackCount * LaneHeight;

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

            UpdateOverlayLanes();
            UpdateHeight();
            InvalidateVisual();
        }
    }

    /// <summary>In einer Spur wurde ein Bereich aufgezogen (Spur, Anfang, Ende).</summary>
    public event EventHandler<(int Track, TimeSpan Start, TimeSpan End)>? RangeSelected;

    /// <summary>Klick auf einen stummen Bereich einer Spur.</summary>
    public event EventHandler<(int Track, TimeSpan Time)>? MutedClicked;

    /// <summary>Klick auf eine leiser gemachte Stelle einer Spur.</summary>
    public event EventHandler<(int Track, TimeSpan Time)>? DuckClicked;

    /// <summary>Klick auf einen rausgeschnittenen Teil.</summary>
    public event EventHandler<TimeSpan>? RemovedClicked;

    /// <summary>Lautstärke-Spitzen pro Spur (0..1) für die Wellenform.</summary>
    public IReadOnlyList<float[]?>? Peaks
    {
        get => _peaks;
        set
        {
            _peaks = value;
            _waveCache.Clear();
            InvalidateVisual();
        }
    }

    /// <summary>Farbe pro Spur für die Wellenform.</summary>
    public IReadOnlyList<Color>? TrackColors
    {
        get => _trackColors;
        set
        {
            _trackColors = value;
            _waveBrushes.Clear();
            InvalidateVisual();
        }
    }

    private IReadOnlyList<Color>? _trackColors;
    private readonly Dictionary<int, Brush> _waveBrushes = new();

    private Brush WaveBrushFor(int track)
    {
        if (_trackColors == null || track >= _trackColors.Count)
        {
            return WaveBrush;
        }

        if (!_waveBrushes.TryGetValue(track, out var brush))
        {
            brush = Frozen(new SolidColorBrush(_trackColors[track]));
            _waveBrushes[track] = brush;
        }

        return brush;
    }

    private int TrackCount => _timeline?.Tracks.Count ?? 0;

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

        foreach (var edge in tl.RemovedEdges)
        {
            var x = X(edge);
            if (x > inX && x < outX)
            {
                DrawEdgeGrip(dc, x, StripTop, StripHeight, RemovedGripBrush);
            }
        }

        DrawHandle(dc, inX, isIn: true);
        DrawHandle(dc, outX, isIn: false);
        DrawRuler(dc, tl.Duration, width);
        if (_showObjects)
        {
            DrawObjectLanes(dc, tl, strip, inX, outX);
        }
        else
        {
            DrawLanes(dc, tl, strip, inX, outX);
        }

        if (_selTrack == StripSelection)
        {
            var a = X(Min(_selStart, _selEnd));
            var b = X(Max(_selStart, _selEnd));
            dc.DrawRectangle(SelectionBrush, SelectionPen, new Rect(a, StripTop, Math.Max(1, b - a), StripHeight));
            if (_showObjects)
            {
                dc.DrawRectangle(SelectionBrush, null, new Rect(a, LanesTop, Math.Max(1, b - a), LanesHeight - 4));
            }
        }
        else if (!_showObjects && _selTrack >= 0 && _selTrack < tl.Tracks.Count)
        {
            var a = X(Min(_selStart, _selEnd));
            var b = X(Max(_selStart, _selEnd));
            var sel = new Rect(a, LanesTop + _selTrack * LaneHeight, Math.Max(1, b - a), LaneHeight - 4);
            dc.DrawRectangle(SelectionBrush, SelectionPen, sel);
            // Oben im Filmstreifen andeuten, was das im Bild wäre
            dc.DrawRectangle(SelectionBrush, null, new Rect(a, StripTop, Math.Max(1, b - a), StripHeight));
        }

        // Abspielkopf: nur innerhalb des Filmstreifens, nicht darüber
        var px = X(_position);
        var lanesHeight = LanesHeight;
        var playheadBottom = lanesHeight > 0 ? LanesTop + lanesHeight - 4 : StripTop + StripHeight;
        dc.DrawLine(PlayheadPen, new Point(px, StripTop), new Point(px, StripTop + StripHeight));
        if (lanesHeight > 0)
        {
            dc.DrawLine(PlayheadPen, new Point(px, LanesTop), new Point(px, playheadBottom));
        }
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

        var point = e.GetPosition(this);
        var x = point.X;
        _downX = x;
        _moved = false;
        ClearSelection();

        if (_showObjects && point.Y >= LanesTop)
        {
            if (OverlayBarAt(point) is { } bar)
            {
                OverlayPressed?.Invoke(this, bar.Id);
                if (_timeline.FindOverlay(bar.Id) is { } item)
                {
                    _drag = bar.Edge < 0 ? DragMode.OverlayStart : bar.Edge > 0 ? DragMode.OverlayEnd : DragMode.OverlayMove;
                    _dragOverlay = item.Id;
                    _dragOverlayStart = item.Start;
                    _dragOverlayEnd = item.End;
                    _dragDownTime = TimeAt(x);
                    _timeline.BeginChange();
                    CaptureMouse();
                    ScrubbingChanged?.Invoke(this, true);
                    e.Handled = true;
                    return;
                }
            }
            else
            {
                OverlayPressed?.Invoke(this, null);
            }
        }

        var lane = LaneAt(point.Y);
        if (lane is { } track && MutedEdgeAt(track, x) is { } edge)
        {
            // Rand eines stummen Bereichs ziehen
            _drag = DragMode.MutedEdge;
            _dragTrack = track;
            _dragRange = edge.Range;
            _dragStart = edge.Start;
            _timeline.BeginChange();
        }
        else if (lane is { } duckTrack && DuckEdgeAt(duckTrack, x) is { } duckEdge)
        {
            // Rand einer leiser gemachten Stelle ziehen
            _drag = DragMode.DuckEdge;
            _dragTrack = duckTrack;
            _dragRange = duckEdge.Range;
            _dragStart = duckEdge.Start;
            _timeline.BeginChange();
        }
        else if (RemovedEdgeAt(x) is { } split)
        {
            // Rand eines rausgeschnittenen Teils ziehen (in Spur oder oben)
            _drag = DragMode.SplitEdge;
            _dragValue = split;
            _timeline.BeginChange();
        }
        else if (lane is { } selectTrack)
        {
            // In einer Spur ziehen = Bereich auswählen
            _drag = DragMode.LaneSelect;
            _selTrack = selectTrack;
            _selStart = _selEnd = TimeAt(x);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        else
        {
            _drag = HitTest(x);
            _dragValue = TimeAt(x);
            if (_drag == DragMode.In)
            {
                _dragValue = _timeline.In;
            }
            else if (_drag == DragMode.Out)
            {
                _dragValue = _timeline.Out;
            }
        }

        CaptureMouse();
        ScrubbingChanged?.Invoke(this, true);
        if (_drag == DragMode.Playhead)
        {
            SeekRequested?.Invoke(this, _dragValue);
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

        var point = e.GetPosition(this);
        var x = point.X;
        if (_drag == DragMode.None)
        {
            Cursor = HoverCursor(point);
            if (_showObjects)
            {
                var hover = point.Y >= LanesTop ? OverlayBarAt(point)?.Id : null;
                if (hover != _hoverOverlay)
                {
                    HoverOverlay = hover;
                    OverlayHovered?.Invoke(this, hover);
                }
            }

            return;
        }

        if (Math.Abs(x - _downX) >= 3)
        {
            _moved = true;
        }

        var t = TimeAt(x);
        switch (_drag)
        {
            case DragMode.Playhead:
                SeekRequested?.Invoke(this, t);
                return;

            case DragMode.LaneSelect:
            case DragMode.StripSelect:
                _selEnd = t;
                InvalidateVisual();
                return;

            case DragMode.OverlayMove:
            case DragMode.OverlayStart:
            case DragMode.OverlayEnd:
                MoveOverlayBar(t);
                return;

            case DragMode.SplitEdge:
                if (_timeline.MoveSplit(_dragValue, t, recordUndo: false) is { } moved)
                {
                    _dragValue = moved;
                    SeekRequested?.Invoke(this, moved);
                }

                return;

            case DragMode.MutedEdge:
                if (_timeline.MoveMutedEdge(_dragTrack, _dragRange, _dragStart, t, recordUndo: false) is { } edge)
                {
                    // Nach dem Zusammenlegen kann sich die Nummer des Bereichs ändern.
                    var muted = _timeline.Tracks[_dragTrack].Muted;
                    for (var i = 0; i < muted.Count; i++)
                    {
                        if ((_dragStart ? muted[i].Start : muted[i].End) == edge)
                        {
                            _dragRange = i;
                            break;
                        }
                    }

                    SeekRequested?.Invoke(this, edge);
                }

                return;

            case DragMode.DuckEdge:
                if (_timeline.MoveDuckEdge(_dragTrack, _dragRange, _dragStart, t, recordUndo: false) is { } duckEdge)
                {
                    var ducked = _timeline.Tracks[_dragTrack].Ducked;
                    for (var i = 0; i < ducked.Count; i++)
                    {
                        if ((_dragStart ? ducked[i].Range.Start : ducked[i].Range.End) == duckEdge)
                        {
                            _dragRange = i;
                            break;
                        }
                    }

                    SeekRequested?.Invoke(this, duckEdge);
                }

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
        FinishDrag(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverOverlay != null && _drag == DragMode.None)
        {
            HoverOverlay = null;
            OverlayHovered?.Invoke(this, null);
        }
    }

    /// <summary>Rechtsklick gedrückt halten und ziehen (Filmstreifen oder Objekt-Reihen): Bereich markieren.</summary>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var point = e.GetPosition(this);
        if (_timeline == null || _drag != DragMode.None || (point.Y >= LanesTop && !_showObjects))
        {
            return;
        }

        _downX = point.X;
        _moved = false;
        _drag = DragMode.StripSelect;
        _selTrack = StripSelection;
        _selStart = _selEnd = TimeAt(point.X);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_drag == DragMode.StripSelect)
        {
            FinishDrag(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishDrag(null);
    }

    /// <summary>Entfernt die markierte Auswahl (nachdem das Menü dazu zu ist).</summary>
    public void ClearSelection()
    {
        // Schließt sich das alte Menü erst, während schon neu gezogen wird, die neue Auswahl behalten.
        if (_selTrack != -1 && _drag != DragMode.LaneSelect && _drag != DragMode.StripSelect)
        {
            _selTrack = -1;
            InvalidateVisual();
        }
    }

    private void FinishDrag(Point? up)
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

        var t = up is { } p ? TimeAt(p.X) : _selEnd;
        switch (mode)
        {
            case DragMode.In:
                _timeline?.SetIn(_dragValue);
                break;
            case DragMode.Out:
                _timeline?.SetOut(_dragValue);
                break;
            case DragMode.SplitEdge:
            case DragMode.MutedEdge:
            case DragMode.DuckEdge:
                if (!_moved)
                {
                    // Nur geklickt, nichts verschoben: den leeren Rückgängig-Schritt wieder weg.
                    _timeline?.Undo();
                    if (mode == DragMode.MutedEdge)
                    {
                        MutedClicked?.Invoke(this, (_dragTrack, t));
                    }
                    else if (mode == DragMode.DuckEdge)
                    {
                        DuckClicked?.Invoke(this, (_dragTrack, t));
                    }
                    else if (_timeline?.SegmentAt(t) is { Removed: true })
                    {
                        RemovedClicked?.Invoke(this, t);
                    }
                }

                break;
            case DragMode.StripSelect:
                if (_moved && up != null && (_selEnd - _selStart).Duration() >= EditTimeline.MinSegment)
                {
                    InvalidateVisual();
                    StripRangeSelected?.Invoke(this, (Min(_selStart, _selEnd), Max(_selStart, _selEnd)));
                    return; // Auswahl bleibt sichtbar, bis das Menü zu ist.
                }

                ClearSelection();
                return;

            case DragMode.OverlayMove:
            case DragMode.OverlayStart:
            case DragMode.OverlayEnd:
                if (!_moved)
                {
                    // Nur angeklickt: leeren Rückgängig-Schritt wieder weg.
                    _timeline?.Undo();
                }

                _dragOverlay = null;
                break;

            case DragMode.Playhead:
                if (!_moved && up != null && _timeline?.SegmentAt(t) is { Removed: true })
                {
                    RemovedClicked?.Invoke(this, t);
                }

                break;
            case DragMode.LaneSelect:
                var track = _selTrack;
                if (track < 0 || track >= TrackCount)
                {
                    ClearSelection();
                    return;
                }

                if (_moved && up != null && (_selEnd - _selStart).Duration() >= EditTimeline.MinSegment)
                {
                    InvalidateVisual();
                    RangeSelected?.Invoke(this, (track, Min(_selStart, _selEnd), Max(_selStart, _selEnd)));
                    return; // Auswahl bleibt sichtbar, bis das Menü zu ist.
                }

                ClearSelection();
                if (up == null)
                {
                    return;
                }

                // Nur geklickt: auf stummem Bereich → Menü, auf entferntem Teil → Menü, sonst springen.
                if (_timeline?.MutedRangeIndexAt(track, t) >= 0)
                {
                    MutedClicked?.Invoke(this, (track, t));
                }
                else if (_timeline?.DuckRangeIndexAt(track, t) >= 0)
                {
                    DuckClicked?.Invoke(this, (track, t));
                }
                else if (_timeline?.SegmentAt(t) is { Removed: true })
                {
                    RemovedClicked?.Invoke(this, t);
                }
                else
                {
                    SeekRequested?.Invoke(this, t);
                }

                return;
        }

        if (mode != DragMode.LaneSelect)
        {
            ScrubbingChanged?.Invoke(this, false);
        }

        InvalidateVisual();
    }

    private Cursor HoverCursor(Point point)
    {
        if (_showObjects && point.Y >= LanesTop)
        {
            return OverlayBarAt(point) is { } bar ? (bar.Edge != 0 ? Cursors.SizeWE : Cursors.SizeAll) : Cursors.Hand;
        }

        var lane = LaneAt(point.Y);
        if ((lane is { } track && (MutedEdgeAt(track, point.X) != null || DuckEdgeAt(track, point.X) != null)) || RemovedEdgeAt(point.X) != null)
        {
            return Cursors.SizeWE;
        }

        if (lane != null)
        {
            return Cursors.IBeam;
        }

        return HitTest(point.X) == DragMode.Playhead ? Cursors.Hand : Cursors.SizeWE;
    }

    /// <summary>Rand einer leiser gemachten Stelle in einer Spur unter x (Bereich-Nummer, Anfang/Ende).</summary>
    private (int Range, bool Start)? DuckEdgeAt(int track, double x)
    {
        if (_timeline is not { } tl || track >= tl.Tracks.Count || !tl.Tracks[track].Enabled)
        {
            return null;
        }

        var ducked = tl.Tracks[track].Ducked;
        for (var i = 0; i < ducked.Count; i++)
        {
            if (Math.Abs(x - X(ducked[i].Range.Start)) <= EdgeGrab)
            {
                return (i, true);
            }

            if (Math.Abs(x - X(ducked[i].Range.End)) <= EdgeGrab)
            {
                return (i, false);
            }
        }

        return null;
    }

    /// <summary>Rand eines stummen Bereichs in einer Spur unter x (Bereich-Nummer, Anfang/Ende).</summary>
    private (int Range, bool Start)? MutedEdgeAt(int track, double x)
    {
        if (_timeline is not { } tl || track >= tl.Tracks.Count || !tl.Tracks[track].Enabled)
        {
            return null;
        }

        var muted = tl.Tracks[track].Muted;
        for (var i = 0; i < muted.Count; i++)
        {
            if (Math.Abs(x - X(muted[i].Start)) <= EdgeGrab)
            {
                return (i, true);
            }

            if (Math.Abs(x - X(muted[i].End)) <= EdgeGrab)
            {
                return (i, false);
            }
        }

        return null;
    }

    /// <summary>Schnittpunkt am Rand eines rausgeschnittenen Teils unter x.</summary>
    private TimeSpan? RemovedEdgeAt(double x)
    {
        if (_timeline is not { } tl)
        {
            return null;
        }

        foreach (var edge in tl.RemovedEdges)
        {
            if (Math.Abs(x - X(edge)) <= EdgeGrab)
            {
                return edge;
            }
        }

        return null;
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

    /// <summary>
    /// Stelle mit geänderter Lautstärke: Fläche getönt (blau = leiser, orange = lauter), darüber die Lautstärke
    /// als Linie (Mitte = normal, tiefer = leiser, höher = lauter, mit Übergängen), Griffe an den Rändern und dB.
    /// </summary>
    private void DrawDuck(DrawingContext dc, DuckRange duck, Rect lane, Rect strip)
    {
        var startX = X(duck.Range.Start);
        var endX = X(duck.Range.End);
        var left = Math.Max(startX, strip.Left);
        var right = Math.Min(endX, strip.Right);
        if (right <= left)
        {
            return;
        }

        var louder = duck.Db > 0;
        dc.DrawRectangle(louder ? LaneBoostBrush : LaneDuckBrush, null, new Rect(left, lane.Top, right - left, lane.Height));

        var mid = lane.Top + lane.Height / 2;
        var half = lane.Height / 2 - 3;
        var level = louder
            ? mid - Math.Clamp(duck.Db / DuckRange.MaxDb, 0.15, 1) * half
            : mid + Math.Clamp(duck.Db / DuckRange.MinDb, 0.15, 1) * half;
        var (fadeIn, fadeOut) = duck.Fades;
        var inPx = X(duck.Range.Start + TimeSpan.FromSeconds(fadeIn)) - startX;
        var outPx = endX - X(duck.Range.End - TimeSpan.FromSeconds(fadeOut));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(startX, mid), false, false);
            ctx.LineTo(new Point(startX + inPx, level), true, true);
            ctx.LineTo(new Point(endX - outPx, level), true, true);
            ctx.LineTo(new Point(endX, mid), true, true);
        }

        geometry.Freeze();
        var pen = louder ? BoostLinePen : DuckLinePen;
        dc.PushClip(new RectangleGeometry(new Rect(left, lane.Top, right - left, lane.Height)));
        dc.DrawLine(DuckMidPen, new Point(startX, mid), new Point(endX, mid));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();

        if (right - left > 40)
        {
            var text = new FormattedText(
                (louder ? "+" : "") + duck.Db.ToString("0.#", CultureInfo.InvariantCulture) + " dB",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Font,
                10,
                DuckTextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var y = louder ? Math.Min(level + 2, lane.Bottom - text.Height) : Math.Max(level - text.Height - 1, lane.Top);
            dc.DrawText(text, new Point(left + Math.Max(inPx, 4) + 2, y));
        }

        var grip = louder ? BoostGripBrush : DuckGripBrush;
        if (startX >= strip.Left)
        {
            DrawEdgeGrip(dc, startX, lane.Top, lane.Height, grip);
        }

        if (endX <= strip.Right)
        {
            DrawEdgeGrip(dc, endX, lane.Top, lane.Height, grip);
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

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        UpdateOverlayLanes();
        UpdateHeight();
        InvalidateVisual();
    }

    private void UpdateHeight()
    {
        var lanes = LanesHeight;
        var height = lanes > 0 ? LanesTop + lanes : LanesTop;
        if (Math.Abs(Height - height) > 0.5)
        {
            Height = height;
        }
    }

    private int? LaneAt(double y)
    {
        if (_showObjects || TrackCount == 0 || y < LanesTop)
        {
            return null;
        }

        var lane = (int)((y - LanesTop) / LaneHeight);
        return lane < TrackCount ? lane : null;
    }

    /// <summary>Spur-Zeilen: Wellenform, stumme Stellen, abgedunkelt was nicht im Export ist.</summary>
    private void DrawLanes(DrawingContext dc, EditTimeline tl, Rect strip, double inX, double outX)
    {
        for (var i = 0; i < tl.Tracks.Count; i++)
        {
            var track = tl.Tracks[i];
            var lane = new Rect(strip.Left, LanesTop + i * LaneHeight, strip.Width, LaneHeight - 4);
            dc.DrawRoundedRectangle(LaneBrush, null, lane, 4, 4);

            if (WaveGeometry(i, strip.Width) is { } wave)
            {
                dc.PushTransform(new TranslateTransform(lane.Left, lane.Top + lane.Height / 2));
                dc.PushTransform(new ScaleTransform(1, lane.Height / 2 - 2));
                dc.DrawGeometry(track.Enabled ? WaveBrushFor(i) : WaveOffBrush, null, wave);
                dc.Pop();
                dc.Pop();
            }

            if (track.Enabled)
            {
                foreach (var duck in track.Ducked)
                {
                    DrawDuck(dc, duck, lane, strip);
                }

                foreach (var muted in track.Muted)
                {
                    var start = Math.Max(X(muted.Start), strip.Left);
                    var end = Math.Min(X(muted.End), strip.Right);
                    if (end <= start)
                    {
                        continue;
                    }

                    var rect = new Rect(start, lane.Top, end - start, lane.Height);
                    dc.DrawRectangle(LaneMutedBrush, null, rect);
                    dc.DrawLine(LaneMutedPen, new Point(start, lane.Top + lane.Height / 2), new Point(end, lane.Top + lane.Height / 2));

                    // Griffe an beiden Rändern: zeigen, dass man sie ziehen kann
                    if (X(muted.Start) >= strip.Left)
                    {
                        DrawEdgeGrip(dc, X(muted.Start), lane.Top, lane.Height, MutedGripBrush);
                    }

                    if (X(muted.End) <= strip.Right)
                    {
                        DrawEdgeGrip(dc, X(muted.End), lane.Top, lane.Height, MutedGripBrush);
                    }
                }
            }
            else
            {
                dc.DrawRoundedRectangle(OutsideBrush, null, lane, 4, 4);
            }

            // Was nicht im Export landet, wie oben abdunkeln
            if (inX > strip.Left)
            {
                dc.DrawRectangle(OutsideBrush, null, new Rect(strip.Left, lane.Top, inX - strip.Left, lane.Height));
            }

            if (outX < strip.Right)
            {
                dc.DrawRectangle(OutsideBrush, null, new Rect(outX, lane.Top, strip.Right - outX, lane.Height));
            }

            foreach (var segment in tl.Segments.Where(s => s.Removed))
            {
                var start = Math.Max(X(segment.Start), inX);
                var end = Math.Min(X(segment.End), outX);
                if (end > start)
                {
                    dc.DrawRectangle(OutsideBrush, null, new Rect(start, lane.Top, end - start, lane.Height));
                }
            }

            foreach (var split in tl.VisibleSplits)
            {
                var x = X(split);
                dc.DrawLine(TickPen, new Point(x, lane.Top), new Point(x, lane.Bottom));
            }
        }
    }

    private void UpdateOverlayLanes()
    {
        if (_timeline == null)
        {
            _overlayLanes = new();
            _overlayLaneCount = 0;
            return;
        }

        (_overlayLanes, _overlayLaneCount) = OverlayLayout.AssignLanes(_timeline.Overlays);
    }

    /// <summary>Balken eines Objekts verschieben oder an einem Ende länger/kürzer ziehen.</summary>
    private void MoveOverlayBar(TimeSpan t)
    {
        if (_timeline is not { } tl || _dragOverlay is not { } id)
        {
            return;
        }

        var duration = tl.Duration.TotalSeconds;
        var delta = (t - _dragDownTime).TotalSeconds;
        var min = EditTimeline.MinSegment.TotalSeconds * 3;
        double start = _dragOverlayStart, end = _dragOverlayEnd;
        switch (_drag)
        {
            case DragMode.OverlayMove:
                var length = end - start;
                start = Math.Clamp(start + delta, 0, Math.Max(0, duration - length));
                end = start + length;
                break;
            case DragMode.OverlayStart:
                start = Math.Clamp(start + delta, 0, end - min);
                break;
            default:
                end = Math.Clamp(end + delta, start + min, duration);
                break;
        }

        tl.UpdateOverlay(id, o =>
        {
            o.Start = start;
            o.End = end;
        }, recordUndo: false);
        // Kein Springen: der Abspielkopf bleibt stehen, während der Balken verschoben wird.
    }

    private Rect OverlayBarRect(OverlayItem item)
    {
        var lane = _overlayLanes.TryGetValue(item.Id, out var l) ? l : 0;
        var left = X(TimeSpan.FromSeconds(item.Start));
        var right = X(TimeSpan.FromSeconds(item.End));
        return new Rect(left, LanesTop + lane * ObjectLaneHeight + 1, Math.Max(6, right - left), ObjectLaneHeight - 6);
    }

    /// <summary>Objekt-Balken unter dem Punkt; Edge −1 = linker Rand, 1 = rechter Rand, 0 = Mitte.</summary>
    private (string Id, int Edge)? OverlayBarAt(Point p)
    {
        if (_timeline is not { } tl)
        {
            return null;
        }

        // Vorne liegende zuerst (falls sich Balken berühren)
        for (var i = tl.Overlays.Count - 1; i >= 0; i--)
        {
            var item = tl.Overlays[i];
            var rect = OverlayBarRect(item);
            var hit = new Rect(rect.Left - 3, rect.Top, rect.Width + 6, rect.Height);
            if (!hit.Contains(p))
            {
                continue;
            }

            var edge = rect.Width >= 18 && p.X <= rect.Left + EdgeGrab ? -1
                : rect.Width >= 18 && p.X >= rect.Right - EdgeGrab ? 1
                : 0;
            return (item.Id, edge);
        }

        return null;
    }

    /// <summary>Farbe des Balkens: Objektfarbe, zu dunkle/helle etwas angepasst, damit man sie sieht.</summary>
    public static Color BarColor(OverlayItem item)
    {
        if (item.Kind == OverlayKind.Blur)
        {
            return Color.FromRgb(0x6B, 0x7A, 0x90);
        }

        if (item.Kind == OverlayKind.Image)
        {
            return Color.FromRgb(0x4F, 0x8A, 0x8B);
        }

        var c = (Color)ColorConverter.ConvertFromString(item.Color);
        var lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        if (lum < 0.18)
        {
            return Color.FromRgb(0x4A, 0x44, 0x52);
        }

        return lum > 0.88 ? Color.FromRgb(0x8D, 0x87, 0x96) : c;
    }

    /// <summary>Objekt-Reihen: pro Objekt ein Balken in seiner Farbe, mit Ebenen-Nummer (1 = ganz vorne) und Namen.</summary>
    private void DrawObjectLanes(DrawingContext dc, EditTimeline tl, Rect strip, double inX, double outX)
    {
        var rows = ObjectLaneRows;
        for (var i = 0; i < rows; i++)
        {
            var lane = new Rect(strip.Left, LanesTop + i * ObjectLaneHeight, strip.Width, ObjectLaneHeight - 4);
            dc.DrawRoundedRectangle(LaneBrush, null, lane, 4, 4);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var count = tl.Overlays.Count;
        for (var i = 0; i < count; i++)
        {
            var item = tl.Overlays[i];
            var rect = OverlayBarRect(item);
            var color = BarColor(item);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var selected = item.Id == _selectedOverlay;
            var hovered = item.Id == _hoverOverlay;
            var pen = selected ? new Pen(Brushes.White, 2) : hovered ? new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1.5) : new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
            dc.DrawRoundedRectangle(brush, pen, rect, 4, 4);

            var lum = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
            var textBrush = lum > 0.6 ? Brushes.Black : Brushes.White;
            var text = new FormattedText(
                $"{count - i}  {item.Label}",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(Font.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                11,
                textBrush,
                dpi);
            dc.PushClip(new RectangleGeometry(new Rect(rect.Left + 4, rect.Top, Math.Max(0, rect.Width - 8), rect.Height)));
            dc.DrawText(text, new Point(rect.Left + 7, rect.Top + (rect.Height - text.Height) / 2));
            dc.Pop();

            // Kleine Griffe an den Enden, wenn ausgewählt
            if (selected && rect.Width >= 18)
            {
                var grip = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 1);
                dc.DrawLine(grip, new Point(rect.Left + 3, rect.Top + 4), new Point(rect.Left + 3, rect.Bottom - 4));
                dc.DrawLine(grip, new Point(rect.Right - 3, rect.Top + 4), new Point(rect.Right - 3, rect.Bottom - 4));
            }
        }

        // Was nicht im Export landet, wie oben abdunkeln
        var top = LanesTop;
        var height = rows * ObjectLaneHeight - 4;
        if (inX > strip.Left)
        {
            dc.DrawRectangle(OutsideBrush, null, new Rect(strip.Left, top, inX - strip.Left, height));
        }

        if (outX < strip.Right)
        {
            dc.DrawRectangle(OutsideBrush, null, new Rect(outX, top, strip.Right - outX, height));
        }
    }

    /// <summary>Kleiner Griff (Balken mit Rillen) an einem ziehbaren Rand.</summary>
    private static void DrawEdgeGrip(DrawingContext dc, double x, double top, double height, Brush brush)
    {
        const double width = 8;
        var h = Math.Min(height - 4, 26);
        var rect = new Rect(x - width / 2, top + (height - h) / 2, width, h);
        dc.DrawRoundedRectangle(brush, GripOutlinePen, rect, 3, 3);
        var mid = rect.Top + rect.Height / 2;
        dc.DrawLine(GripLinePen, new Point(x - 1.5, mid - 5), new Point(x - 1.5, mid + 5));
        dc.DrawLine(GripLinePen, new Point(x + 1.5, mid - 5), new Point(x + 1.5, mid + 5));
    }

    /// <summary>Wellenform als Fläche, 0..width breit und -1..1 hoch. Wird pro Breite zwischengespeichert.</summary>
    private Geometry? WaveGeometry(int track, double width)
    {
        if (_peaks == null || track >= _peaks.Count || _peaks[track] is not { Length: > 0 } peaks || width < 2)
        {
            return null;
        }

        if (_waveCache.TryGetValue(track, out var cached) && Math.Abs(cached.Width - width) < 0.5)
        {
            return cached.Geometry;
        }

        var columns = (int)width;
        var values = new double[columns];
        for (var x = 0; x < columns; x++)
        {
            var from = (int)((long)x * peaks.Length / columns);
            var to = Math.Max(from + 1, (int)((long)(x + 1) * peaks.Length / columns));
            float max = 0;
            for (var b = from; b < to && b < peaks.Length; b++)
            {
                max = Math.Max(max, peaks[b]);
            }

            values[x] = Math.Max(0.02, Waveform.DisplayHeight(max));
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(0, -values[0]), true, true);
            for (var x = 1; x < columns; x++)
            {
                g.LineTo(new Point(x, -values[x]), false, false);
            }

            for (var x = columns - 1; x >= 0; x--)
            {
                g.LineTo(new Point(x, values[x]), false, false);
            }
        }

        geometry.Freeze();
        _waveCache[track] = (width, geometry);
        return geometry;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
