using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ClipManager.Core;
using ClipManager.Core.Editing;
using ClipManager.Core.Export;

namespace ClipManager.Editor;

/// <summary>
/// Zeichnet Objekte (Box, Kreis, Text, Stift, Bild, Unschärfe). Dieselben Bausteine werden für die Vorschau
/// über dem Video und für den Export (als PNG) benutzt, damit das Ergebnis genau so aussieht wie im Editor.
/// Größen hängen an der Bildhöhe, nicht an Bildschirm-Pixeln.
/// </summary>
internal static class OverlayVisuals
{
    private static readonly Dictionary<string, BitmapSource?> Images = new(StringComparer.OrdinalIgnoreCase);

    public static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(OverlayItem.NormalizeColor(hex));
        }
        catch (FormatException)
        {
            return Colors.White;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Bild laden (einmal pro Datei, danach aus dem Speicher).</summary>
    public static BitmapSource? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (Images.TryGetValue(path, out var cached))
        {
            return cached;
        }

        BitmapSource? image = null;
        try
        {
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Bild \"{path}\" konnte nicht geladen werden", ex);
        }

        Images[path] = image;
        return image;
    }

    /// <summary>Inhalt eines Objekts (ohne Platz und Drehung), füllt die ganze Fläche. Nicht für Unschärfe.</summary>
    public static FrameworkElement CreateContent(OverlayItem item, double frameWidth, double frameHeight)
    {
        var brush = new SolidColorBrush(ParseColor(item.Color));
        brush.Freeze();
        var stroke = Math.Max(1, item.Stroke * frameHeight);
        switch (item.Kind)
        {
            case OverlayKind.Box:
                return new Rectangle
                {
                    Fill = item.Fill ? brush : null,
                    Stroke = item.Fill ? null : brush,
                    StrokeThickness = stroke,
                };

            case OverlayKind.Ellipse:
                return new Ellipse
                {
                    Fill = item.Fill ? brush : null,
                    Stroke = item.Fill ? null : brush,
                    StrokeThickness = stroke,
                };

            case OverlayKind.Text:
                return new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(item.Text) ? " " : item.Text,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontWeight = FontWeights.Bold,
                        FontSize = 48,
                        Foreground = brush,
                    },
                    Effect = new DropShadowEffect
                    {
                        Color = Colors.Black,
                        Opacity = 0.65,
                        ShadowDepth = Math.Max(1, frameHeight * 0.003),
                        BlurRadius = Math.Max(2, frameHeight * 0.007),
                    },
                };

            case OverlayKind.Image:
                if (LoadImage(item.ImagePath) is { } image)
                {
                    return new Image { Source = image, Stretch = Stretch.Fill };
                }

                return new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                    Stroke = Brushes.White,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    StrokeThickness = 2,
                };

            case OverlayKind.Pen:
                return new System.Windows.Shapes.Path
                {
                    Data = PenGeometry(item, frameWidth, frameHeight),
                    Stroke = brush,
                    StrokeThickness = stroke,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                };

            default:
                return new Rectangle { Fill = Brushes.Transparent };
        }
    }

    /// <summary>Striche des Stifts in Bildpixeln.</summary>
    public static Geometry PenGeometry(OverlayItem item, double frameWidth, double frameHeight)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            foreach (var path in item.Paths ?? new List<List<double[]>>())
            {
                var points = path.Where(p => p is { Length: >= 2 }).Select(p => new Point(p[0] * frameWidth, p[1] * frameHeight)).ToList();
                if (points.Count == 0)
                {
                    continue;
                }

                if (points.Count == 1)
                {
                    // Einzelner Klick: kleiner Punkt
                    points.Add(new Point(points[0].X + 0.5, points[0].Y + 0.5));
                }

                g.BeginFigure(points[0], false, false);
                g.PolyLineTo(points.Skip(1).ToList(), true, true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Platz, Größe und Drehung eines Objekts setzen (Stift liegt immer übers ganze Bild).</summary>
    public static void Place(FrameworkElement element, OverlayItem item, double frameWidth, double frameHeight)
    {
        if (item.Kind == OverlayKind.Pen)
        {
            element.Width = frameWidth;
            element.Height = frameHeight;
            Canvas.SetLeft(element, 0);
            Canvas.SetTop(element, 0);
            element.RenderTransform = Transform.Identity;
            return;
        }

        element.Width = Math.Max(1, item.W * frameWidth);
        element.Height = Math.Max(1, item.H * frameHeight);
        Canvas.SetLeft(element, item.X * frameWidth);
        Canvas.SetTop(element, item.Y * frameHeight);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = item.Rotation == 0 ? Transform.Identity : new RotateTransform(item.Rotation);
    }

    /// <summary>
    /// Vorschau der Unschärfe: zeigt den Bildausschnitt des Videos darunter verwischt.
    /// Das Element wird wie das Objekt gedreht, der Bildausschnitt deshalb zurückgedreht.
    /// </summary>
    public static void UpdateBlurPreview(Grid host, OverlayItem item, Visual video, double frameWidth, double frameHeight)
    {
        var w = Math.Max(1, item.W * frameWidth);
        var h = Math.Max(1, item.H * frameHeight);
        var radius = item.Blur * frameHeight / 540.0;
        var pad = Math.Ceiling(radius * 1.5) + 2;
        var cx = item.X * frameWidth + w / 2;
        var cy = item.Y * frameHeight + h / 2;

        // Ausschnitt groß genug für das gedrehte, um "pad" vergrößerte Rechteck
        var a = item.Rotation * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(a));
        var sin = Math.Abs(Math.Sin(a));
        var bw = (w + 2 * pad) * cos + (h + 2 * pad) * sin;
        var bh = (w + 2 * pad) * sin + (h + 2 * pad) * cos;

        if (host.Children.Count == 0 || host.Children[0] is not Rectangle rect)
        {
            host.Children.Clear();
            rect = new Rectangle { IsHitTestVisible = false };
            host.Children.Add(rect);
        }

        host.ClipToBounds = true;
        rect.Margin = new Thickness(-pad);
        var rw = w + 2 * pad;
        var rh = h + 2 * pad;
        // Rechteck-Koordinaten: Mitte bei (rw/2, rh/2). Ausschnitt (bw × bh um die Mitte) passend hineinlegen.
        var brush = new VisualBrush(video)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(cx - bw / 2, cy - bh / 2, bw, bh),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(rw / 2 - bw / 2, rh / 2 - bh / 2, bw, bh),
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            Transform = item.Rotation == 0 ? Transform.Identity : new RotateTransform(-item.Rotation, rw / 2, rh / 2),
        };
        rect.Fill = brush;
        if (rect.Effect is BlurEffect blur)
        {
            blur.Radius = radius;
        }
        else
        {
            rect.Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        }
    }

    /// <summary>
    /// Zeichnet alle Objekte als PNGs für den Export (Originalauflösung). Unschärfe wird als Maske gezeichnet
    /// (weiß = verwischen), das Verwischen selbst macht FFmpeg. Muss im UI-Thread laufen.
    /// </summary>
    public static List<OverlayLayer> RenderLayers(IEnumerable<OverlayItem> items, int frameWidth, int frameHeight, string folder)
    {
        Directory.CreateDirectory(folder);
        var layers = new List<OverlayLayer>();
        var index = 0;
        foreach (var item in items.OrderBy(i => i.Z))
        {
            if (OverlayLayout.Bounds(item, frameWidth, frameHeight) is not { } bounds)
            {
                continue;
            }

            var blur = item.Kind == OverlayKind.Blur;
            FrameworkElement element = blur ? new Rectangle { Fill = Brushes.White } : CreateContent(item, frameWidth, frameHeight);
            Place(element, item, frameWidth, frameHeight);
            Canvas.SetLeft(element, Canvas.GetLeft(element) - bounds.X);
            Canvas.SetTop(element, Canvas.GetTop(element) - bounds.Y);

            var canvas = new Canvas
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Background = blur ? Brushes.Black : Brushes.Transparent,
                ClipToBounds = true,
            };
            canvas.Children.Add(element);
            var size = new Size(bounds.Width, bounds.Height);
            canvas.Measure(size);
            canvas.Arrange(new Rect(size));
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var path = System.IO.Path.Combine(folder, $"objekt{index++}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            layers.Add(new OverlayLayer(
                path,
                bounds,
                item.Start,
                item.End,
                blur ? OverlayLayout.BlurRadius(item.Blur, frameHeight, bounds) : 0));
        }

        return layers;
    }
}
