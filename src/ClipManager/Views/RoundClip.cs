using System.Windows;
using System.Windows.Media;

namespace ClipManager.Views;

/// <summary>
/// Schneidet ein Element an runden Ecken ab (WPF-Border macht das für den Inhalt nicht selbst).
/// Benutzung: local:RoundClip.Radius="7".
/// </summary>
public static class RoundClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= Element_SizeChanged;
        element.SizeChanged += Element_SizeChanged;
        Apply(element);
    }

    private static void Element_SizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var radius = GetRadius(element);
        element.Clip = radius > 0 && element.ActualWidth > 0
            ? new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius)
            : null;
    }
}
