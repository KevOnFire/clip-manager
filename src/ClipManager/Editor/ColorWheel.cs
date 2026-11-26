using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ClipManager.Editor;

/// <summary>
/// Farbkreis zum Auswählen einer eigenen Farbe: im Kreis Farbton (rundherum) und Sättigung (zur Mitte blasser),
/// darunter ein Regler für die Helligkeit und ein Feld für den Farbcode (#RRGGBB).
/// </summary>
internal sealed class ColorWheel : StackPanel
{
    private const int Size = 180;
    private static WriteableBitmap? _wheel;

    private readonly Grid _wheelHost;
    private readonly Ellipse _darken;
    private readonly Ellipse _marker;
    private readonly Slider _value;
    private readonly TextBox _hex;
    private readonly Border _preview;
    private double _h;
    private double _s;
    private double _v = 1;
    private bool _updating;

    public ColorWheel(Color start)
    {
        Width = Size;
        _wheelHost = new Grid { Width = Size, Height = Size, Cursor = Cursors.Cross, Background = Brushes.Transparent };
        _wheelHost.Children.Add(new Image { Source = Wheel(), Width = Size, Height = Size, IsHitTestVisible = false });
        _darken = new Ellipse { Fill = Brushes.Black, IsHitTestVisible = false };
        _wheelHost.Children.Add(_darken);
        _marker = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 3, Opacity = 0.8 },
        };
        _wheelHost.Children.Add(_marker);
        _wheelHost.MouseLeftButtonDown += (_, e) =>
        {
            _wheelHost.CaptureMouse();
            Pick(e.GetPosition(_wheelHost));
            e.Handled = true;
        };
        _wheelHost.MouseMove += (_, e) =>
        {
            if (_wheelHost.IsMouseCaptured)
            {
                Pick(e.GetPosition(_wheelHost));
            }
        };
        _wheelHost.MouseLeftButtonUp += (_, _) => _wheelHost.ReleaseMouseCapture();
        Children.Add(_wheelHost);

        Children.Add(new TextBlock { Text = "Helligkeit", FontSize = 12, Margin = new Thickness(0, 10, 0, 2), Opacity = 0.8 });
        _value = new Slider { Minimum = 0, Maximum = 1, Value = 1, IsMoveToPointEnabled = true, Focusable = false };
        _value.ValueChanged += (_, _) =>
        {
            if (!_updating)
            {
                _v = _value.Value;
                Changed();
            }
        };
        Children.Add(_value);

        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _preview = new Border { Width = 32, Height = 28, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) };
        DockPanel.SetDock(_preview, Dock.Left);
        row.Children.Add(_preview);
        _hex = new TextBox { FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 7 };
        _hex.TextChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            var text = _hex.Text.Trim();
            if (!text.StartsWith('#'))
            {
                text = "#" + text;
            }

            if (text.Length == 7 && text.Skip(1).All(Uri.IsHexDigit))
            {
                SetColor((Color)ColorConverter.ConvertFromString(text), updateHex: false);
                ColorChanged?.Invoke(this, Color);
            }
        };
        row.Children.Add(_hex);
        Children.Add(row);

        SetColor(start, updateHex: true);
    }

    /// <summary>Die gewählte Farbe hat sich geändert (bei jeder Bewegung).</summary>
    public event EventHandler<Color>? ColorChanged;

    public Color Color => FromHsv(_h, _s, _v);

    private void Pick(Point p)
    {
        var r = Size / 2.0;
        var dx = p.X - r;
        var dy = p.Y - r;
        _h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        _s = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / r, 0, 1);
        if (_v < 0.05)
        {
            // Wer in den Kreis klickt, will Farbe sehen, nicht schwarz.
            _v = 1;
        }

        Changed();
    }

    private void Changed()
    {
        _updating = true;
        _value.Value = _v;
        _hex.Text = OverlayVisuals.ToHex(Color);
        _updating = false;
        UpdateVisuals();
        ColorChanged?.Invoke(this, Color);
    }

    private void SetColor(Color c, bool updateHex)
    {
        ToHsv(c, out _h, out _s, out _v);
        _updating = true;
        _value.Value = _v;
        if (updateHex)
        {
            _hex.Text = OverlayVisuals.ToHex(c);
        }

        _updating = false;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var r = Size / 2.0;
        var a = _h * Math.PI / 180;
        _marker.Margin = new Thickness(r + Math.Cos(a) * _s * r - 7, r + Math.Sin(a) * _s * r - 7, 0, 0);
        _darken.Opacity = 1 - _v;
        _preview.Background = new SolidColorBrush(Color);
    }

    private static WriteableBitmap Wheel()
    {
        if (_wheel != null)
        {
            return _wheel;
        }

        var bitmap = new WriteableBitmap(Size, Size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[Size * Size * 4];
        var r = Size / 2.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = x + 0.5 - r;
                var dy = y + 0.5 - r;
                var d = Math.Sqrt(dx * dx + dy * dy);
                var i = (y * Size + x) * 4;
                if (d > r)
                {
                    continue;
                }

                var c = FromHsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, d / r, 1);
                var alpha = Math.Clamp(r - d, 0, 1); // weicher Rand
                pixels[i] = (byte)(c.B * alpha);
                pixels[i + 1] = (byte)(c.G * alpha);
                pixels[i + 2] = (byte)(c.R * alpha);
                pixels[i + 3] = (byte)(255 * alpha);
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, Size, Size), pixels, Size * 4, 0);
        bitmap.Freeze();
        _wheel = bitmap;
        return bitmap;
    }

    public static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static void ToHsv(Color color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0)
        {
            h = 0;
        }
        else if (max == r)
        {
            h = 60 * (((g - b) / d + 6) % 6);
        }
        else if (max == g)
        {
            h = 60 * ((b - r) / d + 2);
        }
        else
        {
            h = 60 * ((r - g) / d + 4);
        }
    }
}
