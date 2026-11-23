using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClipManager.Views;

/// <summary>Gelber Stern für Favoriten, weißer sonst.</summary>
public sealed class FavoriteBrushConverter : IValueConverter
{
    private static readonly Brush Gold = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)));
    private static readonly Brush White = Freeze(new SolidColorBrush(Colors.White));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Gold : White;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
