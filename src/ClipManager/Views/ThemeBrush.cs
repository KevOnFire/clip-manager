using System.Windows;
using System.Windows.Media;
using ClipManager.Core.Settings;

namespace ClipManager.Views;

/// <summary>Macht aus den Design-Einstellungen einen Farbverlauf (Pinsel) für WPF.</summary>
public static class ThemeBrush
{
    public static LinearGradientBrush Create(IReadOnlyList<string> colors, double angle)
    {
        var (x1, y1, x2, y2) = ThemeSettings.GradientPoints(angle);
        var brush = new LinearGradientBrush { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2) };
        for (var i = 0; i < colors.Count; i++)
        {
            var offset = colors.Count == 1 ? 0 : (double)i / (colors.Count - 1);
            brush.GradientStops.Add(new GradientStop(ToColor(colors[i]), offset));
        }

        brush.Freeze();
        return brush;
    }

    public static LinearGradientBrush Create(ThemeSettings theme) => Create(theme.ValidColors(), theme.Angle);

    public static Color ToColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
