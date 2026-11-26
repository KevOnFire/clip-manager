using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ClipManager.Core;
using ClipManager.Core.Editing;
using ClipManager.Core.Media;

namespace ClipManager.Editor;

/// <summary>
/// Zeichen-Werkzeuge im Editor: Objekte (Box, Kreis, Unschärfe, Text, Stift, Bild) ins Video setzen,
/// verschieben, Größe ändern, drehen, Farbe wählen, Reihenfolge (Ebenen) festlegen.
/// Die Objekte selbst liegen in der EditTimeline (gespeichert mit den Schnitten, Rückgängig inklusive).
/// </summary>
public partial class EditorView
{
    private enum OverlayDrag
    {
        None,
        Move,
        Resize,
        Rotate,
        Pen,
    }

    private sealed class OverlayHost
    {
        public required Grid Element { get; init; }

        public required OverlayItem Item { get; set; }

        public string ContentKey { get; set; } = "";
    }

    private static readonly string[] Palette = { "#111111", "#FFFFFF", "#E0569F", "#9B6BFF", "#3FB6FF", "#3DDC84", "#FFCC33", "#FF5A4E" };

    private readonly Dictionary<string, OverlayHost> _overlayHosts = new();
    private string? _selectedOverlay;
    private string? _hoverOverlay;
    private bool _showObjects;
    private OverlayDrag _overlayDrag;
    private Point _dragStartPoint;
    private OverlayItem? _dragOrig;
    private bool _overlayMoved;
    private bool _penMode;
    private string? _penItemId;
    private Polyline? _penLine;
    private List<double[]>? _penPoints;
    private string _penColor = "#E0569F";
    private TextBlock? _rotationLabel;
    private bool _toolsOpen;
    private bool _layerDragging;
    private Border? _layerDragItem;
    private double _layerDragStartY;
    private string _audioHint = "";
    private object? _frameKey;
    private readonly HashSet<string> _gifPending = new(StringComparer.OrdinalIgnoreCase);

    private double FrameW => VideoFrame.ActualWidth;

    private double FrameH => VideoFrame.ActualHeight;

    /// <summary>Ordner für Bilder, die als Objekt eingefügt wurden.</summary>
    private static string OverlayImageFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipManager", "Objekt-Bilder");

    private void InitOverlays()
    {
        VideoFrame.SizeChanged += (_, _) => SyncOverlays(rebuild: true);
        TimelineView.StripRangeSelected += (_, e) => ShowStripMenu(e.Start, e.End);
        // Balken anklicken/ziehen wählt nur aus, die Stelle im Video bleibt wo sie ist (Wunsch Kev).
        TimelineView.OverlayPressed += (_, id) => SelectOverlay(id);
        TimelineView.OverlayMenuRequested += (_, id) => ShowOverlayMenu(id);
        TimelineView.OverlayHovered += (_, id) => SetHoverOverlay(id, fromTimeline: true);
        SetShowObjects(App.Settings.ShowObjectLanes, persist: false);
    }

    // ---------- Videofläche ----------

    private (double Width, double Height) VideoPixelSize()
    {
        if (_info is { Width: > 0, Height: > 0 })
        {
            return (_info.Width, _info.Height);
        }

        return Player.NaturalVideoWidth > 0 && Player.NaturalVideoHeight > 0
            ? (Player.NaturalVideoWidth, Player.NaturalVideoHeight)
            : (0, 0);
    }

    /// <summary>Bildfläche genau aufs Video legen (ohne schwarze Ränder).</summary>
    private void UpdateVideoFrame()
    {
        var (vw, vh) = VideoPixelSize();
        var aw = VideoArea.ActualWidth;
        var ah = VideoArea.ActualHeight;
        if (vw <= 0 || vh <= 0 || aw <= 0 || ah <= 0)
        {
            VideoFrame.Width = double.NaN;
            VideoFrame.Height = double.NaN;
            VideoFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
            VideoFrame.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        var scale = Math.Min(aw / vw, ah / vh);
        VideoFrame.Width = Math.Floor(vw * scale);
        VideoFrame.Height = Math.Floor(vh * scale);
        VideoFrame.HorizontalAlignment = HorizontalAlignment.Center;
        VideoFrame.VerticalAlignment = VerticalAlignment.Center;
    }

    private void VideoArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVideoFrame();

    private void Swallow_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ---------- Objekte anzeigen ----------

    private static string ContentKey(OverlayItem item, double fw, double fh) =>
        string.Join('|',
            item.Kind,
            item.Color,
            item.Fill,
            item.Stroke.ToString("0.#####"),
            item.Text,
            item.ImagePath,
            item.Kind == OverlayKind.Pen ? string.Join(';', (item.Paths ?? new()).Select(p => p.Count + ":" + (p.Count > 0 ? p[0][0].ToString("0.####") + "," + p[0][1].ToString("0.####") : ""))) : "",
            $"{fw:0}x{fh:0}");

    /// <summary>Objekte über dem Video an den Stand der Zeitleiste anpassen.</summary>
    private void SyncOverlays(bool rebuild = false)
    {
        if (_timeline == null || FrameW < 2 || FrameH < 2)
        {
            return;
        }

        if (rebuild)
        {
            OverlayCanvas.Children.Clear();
            _overlayHosts.Clear();
        }

        var fw = FrameW;
        var fh = FrameH;
        var seen = new HashSet<string>();
        foreach (var item in _timeline.Overlays)
        {
            seen.Add(item.Id);
            if (!_overlayHosts.TryGetValue(item.Id, out var host) || host.Item.Kind != item.Kind)
            {
                if (host != null)
                {
                    OverlayCanvas.Children.Remove(host.Element);
                }

                var element = new Grid
                {
                    Tag = item.Id,
                    Cursor = Cursors.SizeAll,
                    // Stift: nur die Striche sind anklickbar, sonst würde er das ganze Bild verdecken.
                    Background = item.Kind == OverlayKind.Pen ? null : Brushes.Transparent,
                };
                element.MouseLeftButtonDown += OverlayElement_MouseLeftButtonDown;
                element.MouseRightButtonUp += (_, e) =>
                {
                    if (!_penMode)
                    {
                        e.Handled = true;
                        ShowOverlayMenu(item.Id);
                    }
                };
                element.MouseEnter += (_, _) => SetHoverOverlay(item.Id, fromTimeline: false);
                element.MouseLeave += (_, _) => SetHoverOverlay(null, fromTimeline: false);
                host = new OverlayHost { Element = element, Item = item };
                _overlayHosts[item.Id] = host;
                OverlayCanvas.Children.Add(element);
            }

            host.Item = item;
            if (item.Kind == OverlayKind.Image && GifFrames.IsGif(item.ImagePath) && OverlayVisuals.GetGif(item.ImagePath) == null
                && _gifPending.Add(item.ImagePath!))
            {
                _ = PrepareGifAsync(item.ImagePath!);
            }

            OverlayVisuals.Place(host.Element, item, fw, fh);
            if (item.Kind == OverlayKind.Blur)
            {
                OverlayVisuals.UpdateBlurPreview(host.Element, item, Player, fw, fh);
            }
            else
            {
                var key = ContentKey(item, fw, fh);
                if (host.ContentKey != key)
                {
                    host.Element.Children.Clear();
                    var content = OverlayVisuals.CreateContent(item, fw, fh);
                    content.IsHitTestVisible = item.Kind == OverlayKind.Pen;
                    host.Element.Children.Add(content);
                    host.ContentKey = key;
                }
            }

            Panel.SetZIndex(host.Element, item.Z);
        }

        foreach (var gone in _overlayHosts.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            OverlayCanvas.Children.Remove(_overlayHosts[gone].Element);
            _overlayHosts.Remove(gone);
        }

        if (_selectedOverlay != null && !seen.Contains(_selectedOverlay))
        {
            SelectOverlay(null);
        }

        if (_hoverOverlay != null && !seen.Contains(_hoverOverlay))
        {
            _hoverOverlay = null;
        }

        UpdateOverlayVisibility(Position);
    }

    /// <summary>Nur Objekte zeigen, die an dieser Stelle zu sehen sind. Läuft bei jedem Timer-Tick.</summary>
    private void UpdateOverlayVisibility(TimeSpan pos)
    {
        foreach (var host in _overlayHosts.Values)
        {
            var visible = host.Item.IsVisibleAt(pos) || (host.Item.Id == _penItemId && _penMode);
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            if (host.Element.Visibility != v)
            {
                host.Element.Visibility = v;
            }

            if (visible)
            {
                OverlayVisuals.UpdateGifFrame(host.Element, host.Item, pos);
            }
        }

        UpdateSelectionFrame();
        UpdateLayerDims(pos);
    }

    /// <summary>GIF einmal in Einzelbilder zerlegen (im Hintergrund), danach bewegt es sich in der Vorschau.</summary>
    private async Task PrepareGifAsync(string path)
    {
        if (App.LocateFfmpeg() is not { } ffmpeg)
        {
            return;
        }

        try
        {
            var gif = await Task.Run(() => GifFrames.ExtractAsync(App.ProcessRunner, ffmpeg, path));
            if (gif == null)
            {
                return; // Nur ein Bild oder kaputt: bleibt ein normales Bild.
            }

            OverlayVisuals.SetGif(path, gif);
            foreach (var host in _overlayHosts.Values.Where(h => string.Equals(h.Item.ImagePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                host.ContentKey = "";
            }

            SyncOverlays();
        }
        catch (Exception ex)
        {
            Log.Error($"GIF \"{path}\" konnte nicht zerlegt werden", ex);
        }
    }

    // ---------- Auswahl ----------

    private void SelectOverlay(string? id)
    {
        if (_selectedOverlay == id)
        {
            return;
        }

        _selectedOverlay = id;
        TimelineView.SelectedOverlay = id;
        if (id != null && _timeline?.FindOverlay(id) is { Kind: OverlayKind.Pen } pen)
        {
            _penColor = pen.Color;
        }

        BuildOverlayProps();
        UpdateSelectionFrame();
        BuildLayerList();
    }

    private void SetHoverOverlay(string? id, bool fromTimeline)
    {
        if (_hoverOverlay == id)
        {
            return;
        }

        _hoverOverlay = id;
        if (!fromTimeline)
        {
            TimelineView.HoverOverlay = id;
        }

        UpdateSelectionFrame();
        foreach (var child in LayerList.Children.OfType<Border>())
        {
            child.BorderBrush = LayerBorderBrush(child.Tag as string);
        }
    }

    /// <summary>Rahmen mit Griffen um das ausgewählte Objekt (und ein leichter Rahmen ums Objekt unter der Maus).</summary>
    private void UpdateSelectionFrame()
    {
        // Nur neu bauen, wenn sich etwas geändert hat (läuft bei jedem Timer-Tick).
        var selected = _selectedOverlay != null ? _timeline?.FindOverlay(_selectedOverlay) : null;
        var hovered = _hoverOverlay != null ? _timeline?.FindOverlay(_hoverOverlay) : null;
        var key = (selected, selected?.IsVisibleAt(Position), hovered, hovered?.IsVisibleAt(Position), _overlayDrag, FrameW, FrameH);
        if (Equals(key, _frameKey))
        {
            return;
        }

        _frameKey = key;
        OverlayEditCanvas.Children.Clear();
        _rotationLabel = null;
        if (_timeline == null || FrameW < 2)
        {
            return;
        }

        if (_hoverOverlay != null && _hoverOverlay != _selectedOverlay && _timeline.FindOverlay(_hoverOverlay) is { } hover && hover.IsVisibleAt(Position))
        {
            OverlayEditCanvas.Children.Add(Frame(hover, (Brush)FindResource("AccentFillColorDefaultBrush"), handles: false));
        }

        if (_selectedOverlay != null && _timeline.FindOverlay(_selectedOverlay) is { } item && (item.IsVisibleAt(Position) || _overlayDrag != OverlayDrag.None))
        {
            OverlayEditCanvas.Children.Add(Frame(item, Brushes.White, handles: true));
        }
    }

    private Grid Frame(OverlayItem item, Brush stroke, bool handles)
    {
        var frame = new Grid();
        if (item.Kind == OverlayKind.Pen)
        {
            var b = PenBounds(item);
            frame.Width = Math.Max(4, b.Width);
            frame.Height = Math.Max(4, b.Height);
            Canvas.SetLeft(frame, b.X);
            Canvas.SetTop(frame, b.Y);
        }
        else
        {
            OverlayVisuals.Place(frame, item, FrameW, FrameH);
        }

        frame.Children.Add(new Rectangle
        {
            Stroke = stroke,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Margin = new Thickness(-3),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 },
        });

        if (!handles || item.Kind == OverlayKind.Pen)
        {
            return frame;
        }

        // Unten rechts: Größe ändern (Shift = Seitenverhältnis behalten)
        var resize = new Border
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, -9, -9),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.White,
            BorderBrush = (Brush)FindResource("AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.SizeNWSE,
            ToolTip = "Größe ändern (Shift: Seitenverhältnis behalten)",
        };
        resize.MouseLeftButtonDown += (_, e) =>
        {
            StartOverlayDrag(item.Id, OverlayDrag.Resize, e);
            e.Handled = true;
        };
        frame.Children.Add(resize);

        // Oben rechts: Drehen
        var rotate = new Border
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(0, -15, -15, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            Cursor = Cursors.Hand,
            ToolTip = "Drehen (Shift: in 15°-Schritten)",
            Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 4, Opacity = 0.5 },
            Child = new Path
            {
                Data = Geometry.Parse("M20,12 A8,8 0 1 1 17.7,6.3 M20,4 V9 H15"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x6B, 0x3F, 0xD6)),
                StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
            },
        };
        rotate.MouseLeftButtonDown += (_, e) =>
        {
            StartOverlayDrag(item.Id, OverlayDrag.Rotate, e);
            e.Handled = true;
        };
        frame.Children.Add(rotate);

        if (_overlayDrag == OverlayDrag.Rotate)
        {
            _rotationLabel = new TextBlock
            {
                Text = $"{item.Rotation:0}°",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x26, 0x22, 0x2D)),
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -34, 0, 0),
                IsHitTestVisible = false,
            };
            frame.Children.Add(_rotationLabel);
        }

        return frame;
    }

    private Rect PenBounds(OverlayItem item)
    {
        var points = (item.Paths ?? new()).SelectMany(p => p).Where(p => p is { Length: >= 2 }).ToList();
        if (points.Count == 0)
        {
            return new Rect(0, 0, 0, 0);
        }

        var pad = item.Stroke * FrameH;
        var minX = points.Min(p => p[0]) * FrameW - pad;
        var maxX = points.Max(p => p[0]) * FrameW + pad;
        var minY = points.Min(p => p[1]) * FrameH - pad;
        var maxY = points.Max(p => p[1]) * FrameH + pad;
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    // ---------- Ziehen im Video ----------

    private void OverlayElement_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_penMode || _timeline == null || sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        SelectOverlay(id);
        if (e.ClickCount == 2 && _timeline.FindOverlay(id) is { Kind: OverlayKind.Text })
        {
            FocusOverlayText();
        }
        else
        {
            StartOverlayDrag(id, OverlayDrag.Move, e);
        }

        e.Handled = true;
    }

    private void StartOverlayDrag(string id, OverlayDrag mode, MouseButtonEventArgs e)
    {
        if (_timeline?.FindOverlay(id) is not { } item)
        {
            return;
        }

        SetPlaying(false);
        _dragOrig = item.Clone();
        _dragStartPoint = e.GetPosition(VideoFrame);
        _overlayMoved = false;
        _overlayDrag = mode;
        _timeline.BeginChange();
        VideoFrame.CaptureMouse();
        if (mode == OverlayDrag.Rotate)
        {
            UpdateSelectionFrame();
        }
    }

    private void VideoFrame_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_penMode || _timeline == null || FrameW < 2)
        {
            return;
        }

        // Malen: neuer Strich
        SetPlaying(false);
        var p = e.GetPosition(VideoFrame);
        _penPoints = new List<double[]> { Normalized(p) };
        _penLine = new Polyline
        {
            Stroke = new SolidColorBrush(OverlayVisuals.ParseColor(_penColor)),
            StrokeThickness = Math.Max(1, OverlayItem.DefaultStroke * FrameH),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _penLine.Points.Add(p);
        _penLine.Points.Add(new Point(p.X + 0.5, p.Y + 0.5));
        PenCanvas.Children.Add(_penLine);
        _overlayDrag = OverlayDrag.Pen;
        VideoFrame.CaptureMouse();
        e.Handled = true;
    }

    private double[] Normalized(Point p) => new[]
    {
        Math.Round(Math.Clamp(p.X / FrameW, 0, 1), 5),
        Math.Round(Math.Clamp(p.Y / FrameH, 0, 1), 5),
    };

    private void VideoFrame_MouseMove(object sender, MouseEventArgs e)
    {
        if (_overlayDrag == OverlayDrag.None || _timeline == null)
        {
            return;
        }

        var p = e.GetPosition(VideoFrame);
        var d = p - _dragStartPoint;
        if (Math.Abs(d.X) > 2 || Math.Abs(d.Y) > 2)
        {
            _overlayMoved = true;
        }

        if (_overlayDrag == OverlayDrag.Pen)
        {
            if (_penLine != null && _penPoints != null)
            {
                _penLine.Points.Add(p);
                _penPoints.Add(Normalized(p));
            }

            return;
        }

        if (_dragOrig is not { } orig || !_overlayMoved)
        {
            return;
        }

        var fw = FrameW;
        var fh = FrameH;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_overlayDrag)
        {
            case OverlayDrag.Move when orig.Kind == OverlayKind.Pen:
                _timeline.UpdateOverlay(orig.Id, o => o.Paths = orig.Paths?
                    .Select(path => path.Select(pt => new[] { Math.Round(pt[0] + d.X / fw, 5), Math.Round(pt[1] + d.Y / fh, 5) }).ToList())
                    .ToList(), recordUndo: false);
                break;

            case OverlayDrag.Move:
                _timeline.UpdateOverlay(orig.Id, o =>
                {
                    o.X = orig.X + d.X / fw;
                    o.Y = orig.Y + d.Y / fh;
                }, recordUndo: false);
                break;

            case OverlayDrag.Resize:
            {
                // Bewegung in die Richtung des gedrehten Objekts umrechnen; die Ecke oben links bleibt stehen.
                var a = orig.Rotation * Math.PI / 180;
                var (cos, sin) = (Math.Cos(a), Math.Sin(a));
                var lx = d.X * cos + d.Y * sin;
                var ly = -d.X * sin + d.Y * cos;
                var ow = orig.W * fw;
                var oh = orig.H * fh;
                var nw = Math.Max(8, ow + lx);
                var nh = Math.Max(8, oh + ly);
                if (shift)
                {
                    var scale = Math.Max(nw / ow, nh / oh);
                    nw = Math.Max(8, ow * scale);
                    nh = Math.Max(8, oh * scale);
                }

                var cx = orig.X * fw + ow / 2;
                var cy = orig.Y * fh + oh / 2;
                // Ecke oben links (gedreht) bleibt, neue Mitte daraus
                var tlx = cx + (-ow / 2) * cos - (-oh / 2) * sin;
                var tly = cy + (-ow / 2) * sin + (-oh / 2) * cos;
                var ncx = tlx + (nw / 2) * cos - (nh / 2) * sin;
                var ncy = tly + (nw / 2) * sin + (nh / 2) * cos;
                _timeline.UpdateOverlay(orig.Id, o =>
                {
                    o.W = nw / fw;
                    o.H = nh / fh;
                    o.X = (ncx - nw / 2) / fw;
                    o.Y = (ncy - nh / 2) / fh;
                }, recordUndo: false);
                break;
            }

            case OverlayDrag.Rotate:
            {
                var cx = (orig.X + orig.W / 2) * fw;
                var cy = (orig.Y + orig.H / 2) * fh;
                var a0 = Math.Atan2(_dragStartPoint.Y - cy, _dragStartPoint.X - cx);
                var a1 = Math.Atan2(p.Y - cy, p.X - cx);
                var deg = orig.Rotation + (a1 - a0) * 180 / Math.PI;
                deg = ((deg % 360) + 540) % 360 - 180;
                if (shift)
                {
                    deg = Math.Round(deg / 15) * 15;
                }

                _timeline.UpdateOverlay(orig.Id, o => o.Rotation = Math.Round(deg), recordUndo: false);
                break;
            }
        }
    }

    private void VideoFrame_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_overlayDrag == OverlayDrag.None)
        {
            return;
        }

        FinishOverlayDrag();
        e.Handled = true;
    }

    private void VideoFrame_LostMouseCapture(object sender, MouseEventArgs e) => FinishOverlayDrag();

    private void FinishOverlayDrag()
    {
        var mode = _overlayDrag;
        if (mode == OverlayDrag.None)
        {
            return;
        }

        _overlayDrag = OverlayDrag.None;
        if (VideoFrame.IsMouseCaptured)
        {
            VideoFrame.ReleaseMouseCapture();
        }

        if (mode == OverlayDrag.Pen)
        {
            CommitPenStroke();
            return;
        }

        if (!_overlayMoved)
        {
            // Nur angeklickt: der vorsorglich gemerkte Rückgängig-Schritt ist leer.
            _timeline?.Undo();
        }

        _dragOrig = null;
        UpdateSelectionFrame();
    }

    // ---------- Stift ----------

    private void StartPenMode()
    {
        CloseTools();
        SetPlaying(false);
        _penMode = true;
        _penItemId = null;
        SelectOverlay(null);
        VideoFrame.Cursor = Cursors.Pen;
        PencilButton.Background = (Brush)FindResource("AccentFillColorDefaultBrush");
        OverlayHintText.Text = "Ins Video malen. Fertig: Stift-Knopf oder Esc";
        OverlayHintBar.Visibility = Visibility.Visible;
        BuildOverlayProps();
    }

    private void EndPenMode()
    {
        if (!_penMode)
        {
            return;
        }

        _penMode = false;
        var id = _penItemId;
        _penItemId = null;
        VideoFrame.Cursor = null;
        PencilButton.Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x14, 0x12, 0x18));
        OverlayHintBar.Visibility = Visibility.Collapsed;
        PenCanvas.Children.Clear();
        SelectOverlay(id);
        BuildOverlayProps();
        UpdateOverlayVisibility(Position);
    }

    private void CommitPenStroke()
    {
        var points = _penPoints;
        _penPoints = null;
        _penLine = null;
        PenCanvas.Children.Clear();
        if (_timeline == null || points is not { Count: > 0 })
        {
            return;
        }

        if (_penItemId != null && _timeline.FindOverlay(_penItemId) != null)
        {
            _timeline.UpdateOverlay(_penItemId, o => (o.Paths ??= new()).Add(points));
            return;
        }

        var start = Position;
        var item = _timeline.AddOverlay(new OverlayItem
        {
            Kind = OverlayKind.Pen,
            Start = start.TotalSeconds,
            End = Math.Min(_timeline.Duration.TotalSeconds, start.TotalSeconds + 4),
            X = 0,
            Y = 0,
            W = 1,
            H = 1,
            Color = _penColor,
            Paths = new() { points },
        });
        _penItemId = item.Id;
        SetShowObjects(true, persist: true);
        UpdateOverlayVisibility(Position);
    }

    // ---------- Werkzeug-Menü ----------

    private void Pencil_Click(object sender, RoutedEventArgs e)
    {
        if (_penMode)
        {
            EndPenMode();
            return;
        }

        if (_toolsOpen)
        {
            CloseTools();
        }
        else
        {
            OpenTools();
        }
    }

    private void OpenTools()
    {
        _toolsOpen = true;
        OverlayToolStack.Visibility = Visibility.Visible;
        var duration = SystemParameters.ClientAreaAnimation ? TimeSpan.FromSeconds(0.22) : TimeSpan.Zero;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        OverlayToolScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
        OverlayToolStack.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    private void CloseTools()
    {
        if (!_toolsOpen)
        {
            return;
        }

        _toolsOpen = false;
        var duration = SystemParameters.ClientAreaAnimation ? TimeSpan.FromSeconds(0.16) : TimeSpan.Zero;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var shrink = new DoubleAnimation(0, duration) { EasingFunction = ease };
        shrink.Completed += (_, _) =>
        {
            if (!_toolsOpen)
            {
                OverlayToolStack.Visibility = Visibility.Collapsed;
            }
        };
        OverlayToolScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        OverlayToolStack.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration));
    }

    private void AddBox_Click(object sender, RoutedEventArgs e) => AddOverlay(OverlayKind.Box);

    private void AddCircle_Click(object sender, RoutedEventArgs e) => AddOverlay(OverlayKind.Ellipse);

    private void AddBlur_Click(object sender, RoutedEventArgs e) => AddOverlay(OverlayKind.Blur);

    private void AddText_Click(object sender, RoutedEventArgs e) => AddOverlay(OverlayKind.Text);

    private void AddPen_Click(object sender, RoutedEventArgs e) => StartPenMode();

    private void AddImage_Click(object sender, RoutedEventArgs e) => AddOverlay(OverlayKind.Image);

    /// <summary>Neues Objekt ab dem Abspielkopf (4 s lang) oder für einen markierten Bereich, mittig im Bild.</summary>
    private void AddOverlay(OverlayKind kind, TimeSpan? from = null, TimeSpan? to = null)
    {
        CloseTools();
        if (_timeline == null)
        {
            return;
        }

        EndPenMode();
        SetPlaying(false);
        var (vw, vh) = VideoPixelSize();
        var aspect = vw > 0 && vh > 0 ? vw / vh : 16.0 / 9;
        var duration = _timeline.Duration.TotalSeconds;
        var start = (from ?? Position).TotalSeconds;
        var end = to?.TotalSeconds ?? Math.Min(duration, start + 4);
        if (end - start < 0.5)
        {
            start = Math.Max(0, end - 4);
        }

        var item = new OverlayItem { Kind = kind, Start = start, End = end, Color = "#E0569F" };
        switch (kind)
        {
            case OverlayKind.Box:
                (item.W, item.H, item.Color, item.Fill) = (0.25, 0.25 * aspect * 0.6, "#111111", true);
                break;
            case OverlayKind.Ellipse:
                (item.W, item.H, item.Color) = (0.16, 0.16 * aspect, "#FF5A4E");
                break;
            case OverlayKind.Blur:
                (item.W, item.H) = (0.25, 0.2);
                break;
            case OverlayKind.Text:
                (item.W, item.H, item.Color, item.Text) = (0.3, 0.12, "#FFFFFF", "Text");
                break;
            case OverlayKind.Image:
                var path = PickImage();
                if (path == null)
                {
                    return;
                }

                item.ImagePath = path;
                var image = OverlayVisuals.LoadImage(path);
                var ratio = image is { PixelWidth: > 0, PixelHeight: > 0 } ? (double)image.PixelHeight / image.PixelWidth : 1;
                item.W = 0.3;
                item.H = 0.3 * aspect * ratio;
                if (item.H > 0.8)
                {
                    item.W *= 0.8 / item.H;
                    item.H = 0.8;
                }

                break;
        }

        item.X = 0.5 - item.W / 2;
        item.Y = kind == OverlayKind.Text ? 0.72 : 0.5 - item.H / 2;
        var added = _timeline.AddOverlay(item);
        SetShowObjects(true, persist: true);
        if (!added.IsVisibleAt(Position))
        {
            Seek(added.Range.Start);
        }

        SelectOverlay(added.Id);
        if (kind == OverlayKind.Text)
        {
            Dispatcher.BeginInvoke(new Action(FocusOverlayText), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private static string? PickImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Bild einfügen",
            Filter = "Bilder|" + string.Join(';', OverlayImages.Extensions.Select(x => "*" + x)) + "|Alle Dateien|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        try
        {
            return OverlayImages.Import(dialog.FileName, OverlayImageFolder);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Error($"Bild \"{dialog.FileName}\" konnte nicht übernommen werden", ex);
            _ = Dialogs.ErrorAsync($"Das Bild konnte nicht geladen werden: {ex.Message}");
            return null;
        }
    }

    // ---------- Eigenschaften-Leiste ----------

    private TextBox? _overlayTextBox;

    private void FocusOverlayText()
    {
        if (_overlayTextBox != null)
        {
            _overlayTextBox.Focus();
            _overlayTextBox.SelectAll();
        }
    }

    private void BuildOverlayProps()
    {
        OverlayPropsPanel.Children.Clear();
        _overlayTextBox = null;
        var item = _selectedOverlay != null ? _timeline?.FindOverlay(_selectedOverlay) : null;
        if (item == null && !_penMode)
        {
            OverlayPropsBar.Visibility = Visibility.Collapsed;
            return;
        }

        OverlayPropsBar.Visibility = Visibility.Visible;
        var kind = item?.Kind ?? OverlayKind.Pen;
        var color = item?.Color ?? _penColor;

        void SetColor(string c)
        {
            if (item == null)
            {
                _penColor = c;
            }
            else
            {
                _timeline?.UpdateOverlay(item.Id, o => o.Color = c);
                if (item.Kind == OverlayKind.Pen)
                {
                    _penColor = c;
                }
            }

            BuildOverlayProps();
        }

        if (kind is not (OverlayKind.Blur or OverlayKind.Image))
        {
            foreach (var c in Palette.Concat(App.Settings.CustomColors.Where(c => !Palette.Contains(c, StringComparer.OrdinalIgnoreCase))))
            {
                var swatch = new Button
                {
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    Focusable = false,
                    ToolTip = c,
                    Template = SwatchTemplate(),
                    Background = new SolidColorBrush(OverlayVisuals.ParseColor(c)),
                    BorderBrush = string.Equals(c, color, StringComparison.OrdinalIgnoreCase) ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(string.Equals(c, color, StringComparison.OrdinalIgnoreCase) ? 2 : 1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var value = c;
                swatch.Click += (_, _) => SetColor(value);
                if (!Palette.Contains(c, StringComparer.OrdinalIgnoreCase))
                {
                    // Eigene Farbe: Rechtsklick → entfernen
                    swatch.ToolTip = $"{c} · Rechtsklick zum Entfernen";
                    var remove = new MenuItem { Header = "Farbe entfernen" };
                    remove.Click += (_, _) =>
                    {
                        if (App.Settings.ForgetColor(value))
                        {
                            try
                            {
                                App.SaveSettings();
                            }
                            catch (Exception ex)
                            {
                                Log.Error("Einstellungen konnten nicht gespeichert werden", ex);
                            }
                        }

                        BuildOverlayProps();
                    };
                    swatch.ContextMenu = new ContextMenu { Items = { remove } };
                }

                OverlayPropsPanel.Children.Add(swatch);
            }

            // Eigene Farbe aus dem Farbkreis
            var custom = new Button
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = "Eigene Farbe (Farbkreis)",
                Template = SwatchTemplate(),
                Background = new LinearGradientBrush(new GradientStopCollection
                {
                    new(Colors.Red, 0), new(Colors.Yellow, 0.2), new(Colors.Lime, 0.4), new(Colors.Cyan, 0.6), new(Colors.Blue, 0.8), new(Colors.Magenta, 1),
                }, 45),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            custom.Click += (_, _) => OpenColorWheel(custom, item, color);
            OverlayPropsPanel.Children.Add(custom);
        }

        if (item is { Kind: OverlayKind.Box or OverlayKind.Ellipse })
        {
            var fill = new Wpf.Ui.Controls.Button
            {
                Content = item.Fill ? "Gefüllt" : "Nur Rand",
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 12,
                Focusable = false,
                Appearance = item.Fill ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary,
                ToolTip = "Zwischen gefüllt und nur Rand wechseln",
            };
            fill.Click += (_, _) =>
            {
                _timeline?.UpdateOverlay(item.Id, o => o.Fill = !o.Fill);
                BuildOverlayProps();
            };
            OverlayPropsPanel.Children.Add(fill);
        }

        if (item is { Kind: OverlayKind.Blur })
        {
            AddSlider("Unschärfe", OverlayItem.MinBlur, OverlayItem.MaxBlur, item.Blur, v => v.ToString("0"), (o, v) => o.Blur = v, item.Id);
        }

        if (kind == OverlayKind.Pen || item is { Kind: OverlayKind.Box or OverlayKind.Ellipse, Fill: false })
        {
            if (item != null)
            {
                AddSlider("Dicke", 0.002, 0.03, item.Stroke, v => (v * 1000).ToString("0"), (o, v) => o.Stroke = v, item.Id);
            }
        }

        if (item is { Kind: OverlayKind.Text })
        {
            var box = new TextBox
            {
                Text = item.Text ?? "",
                Width = 170,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Text",
            };
            var started = false;
            box.TextChanged += (_, _) =>
            {
                if (!started)
                {
                    _timeline?.BeginChange();
                    started = true;
                }

                _timeline?.UpdateOverlay(item.Id, o => o.Text = box.Text, recordUndo: false);
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Escape)
                {
                    Focus();
                    e.Handled = true;
                }
            };
            _overlayTextBox = box;
            OverlayPropsPanel.Children.Add(box);
        }

        if (item != null && _timeline != null && _timeline.Overlays.Count > 1)
        {
            var index = _timeline.Overlays.ToList().FindIndex(o => o.Id == item.Id);
            AddPropButton("Nach vorne", "Eine Ebene nach vorne (Strg+↑)", index < _timeline.Overlays.Count - 1, () => _timeline.MoveOverlayLayer(item.Id, +1));
            AddPropButton("Nach hinten", "Eine Ebene nach hinten (Strg+↓)", index > 0, () => _timeline.MoveOverlayLayer(item.Id, -1));
        }

        if (item != null)
        {
            AddPropButton("Löschen", "Objekt löschen (Entf)", true, () =>
            {
                _timeline?.RemoveOverlay(item.Id);
                SelectOverlay(null);
            });
        }
        else
        {
            OverlayPropsPanel.Children.Add(new TextBlock { Text = "Stiftfarbe", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        }
    }

    private static ControlTemplate SwatchTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        template.VisualTree = border;
        return template;
    }

    private void AddPropButton(string text, string tip, bool enabled, Action action)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Content = text,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12,
            Focusable = false,
            IsEnabled = enabled,
            ToolTip = tip,
        };
        button.Click += (_, _) =>
        {
            action();
            BuildOverlayProps();
        };
        OverlayPropsPanel.Children.Add(button);
    }

    private void AddSlider(string label, double min, double max, double value, Func<double, string> format, Action<OverlayItem, double> apply, string id)
    {
        var text = new TextBlock { Text = $"{label} {format(value)}", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = 92 };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 120,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            Focusable = false,
        };
        slider.PreviewMouseLeftButtonDown += (_, _) => _timeline?.BeginChange();
        slider.ValueChanged += (_, e) =>
        {
            text.Text = $"{label} {format(e.NewValue)}";
            _timeline?.UpdateOverlay(id, o => apply(o, e.NewValue), recordUndo: false);
        };
        OverlayPropsPanel.Children.Add(text);
        OverlayPropsPanel.Children.Add(slider);
    }

    /// <summary>Farbkreis in einem kleinen Fenster unter dem Knopf. Die Farbe ändert sich live, gemerkt wird sie beim Schließen.</summary>
    private void OpenColorWheel(FrameworkElement anchor, OverlayItem? item, string current)
    {
        var wheel = new ColorWheel(OverlayVisuals.ParseColor(current));
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 8,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1D, 0x1A, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Child = wheel,
            },
        };
        var changed = false;
        string? picked = null;
        wheel.ColorChanged += (_, c) =>
        {
            picked = OverlayVisuals.ToHex(c);
            if (item != null && _timeline != null)
            {
                if (!changed)
                {
                    _timeline.BeginChange();
                }

                _timeline.UpdateOverlay(item.Id, o => o.Color = picked, recordUndo: false);
                if (item.Kind == OverlayKind.Pen)
                {
                    _penColor = picked;
                }
            }
            else
            {
                _penColor = picked;
            }

            changed = true;
        };
        popup.Closed += (_, _) =>
        {
            if (changed && picked != null)
            {
                App.Settings.RememberColor(picked);
                try
                {
                    App.SaveSettings();
                }
                catch (Exception ex)
                {
                    Log.Error("Einstellungen konnten nicht gespeichert werden", ex);
                }
            }

            BuildOverlayProps();
        };
        popup.IsOpen = true;
    }

    // ---------- Umschalter Ton / Objekte und Ebenen-Liste ----------

    private void AudioMode_Click(object sender, RoutedEventArgs e) => SetShowObjects(false, persist: true);

    private void ObjectsMode_Click(object sender, RoutedEventArgs e) => SetShowObjects(true, persist: true);

    private void SetShowObjects(bool on, bool persist)
    {
        var changed = _showObjects != on;
        _showObjects = on;
        TimelineView.ShowObjects = on;
        TrackHeaders.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        LayerArea.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        AudioModeButton.Appearance = on ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        ObjectsModeButton.Appearance = on ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        UpdateTracksHint();
        BuildLayerList();
        if (persist && changed && App.Settings.ShowObjectLanes != on)
        {
            App.Settings.ShowObjectLanes = on;
            try
            {
                App.SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Error("Einstellungen konnten nicht gespeichert werden", ex);
            }
        }
    }

    private void UpdateTracksHint()
    {
        TracksHint.Text = _showObjects
            ? "Objekte · Liste: oben = ganz vorne, zum Umsortieren ziehen · Balken ziehen = Zeit ändern"
            : _audioHint.Length > 0 ? _audioHint : TracksHint.Text;
    }

    private Brush LayerBorderBrush(string? id) =>
        id == _selectedOverlay ? (Brush)FindResource("AccentFillColorDefaultBrush")
        : id == _hoverOverlay ? new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF))
        : Brushes.Transparent;

    /// <summary>Ebenen-Liste links neben den Objekt-Reihen, vorne zuerst.</summary>
    private void BuildLayerList()
    {
        if (_layerDragging)
        {
            return;
        }

        LayerList.Children.Clear();
        if (_timeline == null)
        {
            return;
        }

        // Gleich hoch wie die Objekt-Reihen der Zeitleiste
        var rows = Math.Max(3, OverlayLayout.AssignLanes(_timeline.Overlays).Count + 1);
        LayerArea.Height = rows * TimelineControl.ObjectLaneHeight - 4;

        var items = _timeline.Overlays.Reverse().ToList();
        if (items.Count == 0)
        {
            LayerList.Children.Add(new TextBlock
            {
                Text = "Noch keine Objekte. Unten links im Video auf den Stift klicken.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(2, 4, 8, 0),
                Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            });
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var row = new Border
            {
                Tag = item.Id,
                Height = 22,
                Margin = new Thickness(0, 0, 8, 3),
                Padding = new Thickness(6, 0, 6, 0),
                CornerRadius = new CornerRadius(5),
                Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = LayerBorderBrush(item.Id),
                BorderThickness = new Thickness(1.5),
                Cursor = Cursors.SizeNS,
                ToolTip = "Ziehen zum Umsortieren (oben = ganz vorne)",
            };
            var dock = new DockPanel();
            var swatch = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 7, 0),
                Background = new SolidColorBrush(TimelineControl.BarColor(item)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(swatch, Dock.Left);
            dock.Children.Add(swatch);
            var number = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            };
            DockPanel.SetDock(number, Dock.Right);
            dock.Children.Add(number);
            dock.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Child = dock;
            row.MouseLeftButtonDown += LayerRow_MouseDown;
            row.MouseMove += LayerRow_MouseMove;
            row.MouseLeftButtonUp += LayerRow_MouseUp;
            row.LostMouseCapture += (_, _) => FinishLayerDrag(commit: true);
            row.MouseEnter += (_, _) =>
            {
                if (!_layerDragging)
                {
                    SetHoverOverlay(item.Id, fromTimeline: false);
                }
            };
            row.MouseLeave += (_, _) =>
            {
                if (!_layerDragging)
                {
                    SetHoverOverlay(null, fromTimeline: false);
                }
            };
            LayerList.Children.Add(row);
        }

        UpdateLayerDims(Position);
    }

    /// <summary>Objekte, die gerade nicht zu sehen sind, blasser zeigen.</summary>
    private void UpdateLayerDims(TimeSpan pos)
    {
        if (_timeline == null || !_showObjects)
        {
            return;
        }

        foreach (var row in LayerList.Children.OfType<Border>())
        {
            var visible = row.Tag is string id && _timeline.FindOverlay(id)?.IsVisibleAt(pos) == true;
            row.Opacity = visible || row == _layerDragItem ? 1 : 0.5;
        }
    }

    private void LayerRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string id } row || _timeline == null)
        {
            return;
        }

        SelectOverlay(id);
        // SelectOverlay baut die Liste neu, deshalb die neue Zeile suchen
        _layerDragItem = LayerList.Children.OfType<Border>().FirstOrDefault(b => (string)b.Tag == id) ?? row;
        _layerDragStartY = e.GetPosition(LayerList).Y;
        _layerDragging = true;
        _layerDragItem.CaptureMouse();
        e.Handled = true;
    }

    private void LayerRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_layerDragging || _layerDragItem == null)
        {
            return;
        }

        var y = e.GetPosition(LayerList).Y;
        var rows = LayerList.Children.OfType<Border>().ToList();
        var current = rows.IndexOf(_layerDragItem);
        var target = rows.Count - 1;
        for (var i = 0; i < rows.Count; i++)
        {
            var top = rows[i].TranslatePoint(new Point(0, 0), LayerList).Y;
            if (y < top + rows[i].ActualHeight / 2 + (i > current ? rows[i].ActualHeight / 2 : 0))
            {
                target = i;
                break;
            }
        }

        if (target != current && Math.Abs(y - _layerDragStartY) > 3)
        {
            var dragged = _layerDragItem;
            // Kurz Maus freigeben verhindern: Element bleibt dasselbe, nur Position in der Liste ändert sich.
            _layerDragging = false;
            LayerList.Children.Remove(dragged);
            LayerList.Children.Insert(target, dragged);
            _layerDragging = true;
            dragged.CaptureMouse();
            foreach (var (row, index) in LayerList.Children.OfType<Border>().Select((b, i) => (b, i)))
            {
                if (row.Child is DockPanel dock && dock.Children.Count > 1 && dock.Children[1] is TextBlock number)
                {
                    number.Text = (index + 1).ToString();
                }
            }
        }
    }

    private void LayerRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        FinishLayerDrag(commit: true);
        e.Handled = true;
    }

    private void FinishLayerDrag(bool commit)
    {
        if (!_layerDragging)
        {
            return;
        }

        _layerDragging = false;
        var item = _layerDragItem;
        _layerDragItem = null;
        if (item?.IsMouseCaptured == true)
        {
            item.ReleaseMouseCapture();
        }

        if (commit && _timeline != null)
        {
            var order = LayerList.Children.OfType<Border>().Select(b => (string)b.Tag).ToList();
            if (!_timeline.SetOverlayOrder(order))
            {
                // Nichts verschoben: angeklicktes Objekt zeigen
                if (item?.Tag is string id && _timeline.FindOverlay(id) is { } o && !o.IsVisibleAt(Position))
                {
                    Seek(o.Range.Start);
                }
            }
        }

        BuildLayerList();
    }

    // ---------- Rechtsklick auf ein Objekt ----------

    /// <summary>Menü für ein Objekt (Rechtsklick auf den Balken oder das Objekt im Video).</summary>
    private void ShowOverlayMenu(string id)
    {
        if (_timeline?.FindOverlay(id) is not { } item)
        {
            return;
        }

        SelectOverlay(id);
        var index = _timeline.Overlays.ToList().FindIndex(o => o.Id == id);
        var items = new List<(string, Wpf.Ui.Controls.SymbolRegular, Action)>
        {
            ($"„{item.Label}“ löschen", Wpf.Ui.Controls.SymbolRegular.Delete24, () =>
            {
                _timeline?.RemoveOverlay(id);
                SelectOverlay(null);
            }),
            ("Doppeln", Wpf.Ui.Controls.SymbolRegular.Copy24, () => DuplicateOverlay(id)),
        };
        if (index < _timeline.Overlays.Count - 1)
        {
            items.Add(("Nach vorne", Wpf.Ui.Controls.SymbolRegular.ArrowUp24, () =>
            {
                _timeline?.MoveOverlayLayer(id, +1);
                BuildOverlayProps();
            }));
        }

        if (index > 0)
        {
            items.Add(("Nach hinten", Wpf.Ui.Controls.SymbolRegular.ArrowDown24, () =>
            {
                _timeline?.MoveOverlayLayer(id, -1);
                BuildOverlayProps();
            }));
        }

        items.Add(("Zum Anfang springen", Wpf.Ui.Controls.SymbolRegular.Previous24, () => Seek(TimeSpan.FromSeconds(item.Start))));
        ShowMenu(items.ToArray());
    }

    /// <summary>Kopie des Objekts, etwas versetzt, ganz vorne.</summary>
    private void DuplicateOverlay(string id)
    {
        if (_timeline?.FindOverlay(id) is not { } item)
        {
            return;
        }

        var copy = item.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.X = Math.Min(copy.X + 0.03, Math.Max(0, 1 - copy.W));
        copy.Y = Math.Min(copy.Y + 0.03, Math.Max(0, 1 - copy.H));
        if (copy.Paths != null)
        {
            copy.Paths = copy.Paths.Select(p => p.Select(pt => pt.Length >= 2 ? new[] { pt[0] + 0.03, pt[1] + 0.03 } : pt).ToList()).ToList();
        }

        var added = _timeline.AddOverlay(copy);
        SelectOverlay(added.Id);
    }

    // ---------- Bereich auf der Hauptleiste (Rechtsklick-Ziehen) ----------

    private void ShowStripMenu(TimeSpan start, TimeSpan end)
    {
        var length = TimeText.Format(end - start);
        ShowMenu(
            ($"Rausschneiden ({length})", Wpf.Ui.Controls.SymbolRegular.Cut24, () => _timeline?.RemoveRange(start, end)),
            ($"Nur das behalten ({length})", Wpf.Ui.Controls.SymbolRegular.ArrowStepIn24, () => _timeline?.KeepOnly(start, end)),
            ("Box hier einfügen", Wpf.Ui.Controls.SymbolRegular.RectangleLandscape24, () => AddOverlay(OverlayKind.Box, start, end)),
            ("Kreis hier einfügen", Wpf.Ui.Controls.SymbolRegular.Oval24, () => AddOverlay(OverlayKind.Ellipse, start, end)),
            ("Unschärfe hier einfügen", Wpf.Ui.Controls.SymbolRegular.Blur24, () => AddOverlay(OverlayKind.Blur, start, end)),
            ("Text hier einfügen", Wpf.Ui.Controls.SymbolRegular.TextT24, () => AddOverlay(OverlayKind.Text, start, end)),
            ("Bild hier einfügen …", Wpf.Ui.Controls.SymbolRegular.ImageAdd24, () => AddOverlay(OverlayKind.Image, start, end)),
            ("Ab hier abspielen", Wpf.Ui.Controls.SymbolRegular.Play24, () =>
            {
                Seek(start);
                SetPlaying(true);
            }));
    }

    // ---------- Tasten ----------

    /// <summary>Tasten für Objekte. True, wenn die Taste verbraucht wurde.</summary>
    private bool HandleOverlayKey(KeyEventArgs e)
    {
        if (_penMode && e.Key == Key.Escape)
        {
            EndPenMode();
            return true;
        }

        if (_toolsOpen && e.Key == Key.Escape)
        {
            CloseTools();
            return true;
        }

        if (_selectedOverlay == null || _timeline == null)
        {
            return false;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Delete:
            case Key.Back:
                _timeline.RemoveOverlay(_selectedOverlay);
                SelectOverlay(null);
                return true;
            case Key.Escape:
                SelectOverlay(null);
                return true;
            case Key.Up when ctrl:
                _timeline.MoveOverlayLayer(_selectedOverlay, +1);
                BuildOverlayProps();
                return true;
            case Key.Down when ctrl:
                _timeline.MoveOverlayLayer(_selectedOverlay, -1);
                BuildOverlayProps();
                return true;
            default:
                return false;
        }
    }
}
