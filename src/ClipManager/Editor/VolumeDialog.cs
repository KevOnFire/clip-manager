using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ClipManager.Core.Editing;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace ClipManager.Editor;

/// <summary>Was im Fenster "Lautstärke ändern" gewählt wurde.</summary>
internal enum VolumeChoice
{
    Cancel,
    Apply,
    Reset,
}

/// <summary>
/// Fenster zum Einstellen einer Stelle: wie viel lauter/leiser (dB) und wie schnell rein- und rausgeblendet wird.
/// </summary>
internal static class VolumeDialog
{
    // Zuletzt gewählte Werte (für die nächste neue Stelle).
    private static double _lastDb = -12;
    private static double _lastFadeIn = DuckRange.FadeSeconds;
    private static double _lastFadeOut = DuckRange.FadeSeconds;

    public static async Task<(VolumeChoice Choice, double Db, double FadeIn, double FadeOut)> ShowAsync(string title, DuckRange? current)
    {
        var db = current?.Db ?? _lastDb;
        var fadeIn = current?.FadeIn ?? _lastFadeIn;
        var fadeOut = current?.FadeOut ?? _lastFadeOut;

        var panel = new StackPanel { Width = 380 };
        var dbSlider = AddSlider(panel, "Lautstärke", DuckRange.MinDb, DuckRange.MaxDb, 1, db, FormatDb);
        panel.Children.Add(new TextBlock
        {
            Text = "Minus = leiser, plus = lauter. −6 dB ist etwa halb so laut, −20 dB fast weg.",
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // Schnellwahl
        var presets = new WrapPanel { Margin = new Thickness(0, -6, 0, 16) };
        foreach (var value in new[] { -20.0, -12, -6, 6 })
        {
            var button = new Button { Content = FormatDb(value), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
            button.Click += (_, _) => dbSlider.Value = value;
            presets.Children.Add(button);
        }

        panel.Children.Add(presets);

        var inSlider = AddSlider(panel, "Übergang am Anfang", 0, 3, 0.05, fadeIn, FormatSeconds);
        var outSlider = AddSlider(panel, "Übergang am Ende", 0, 3, 0.05, fadeOut, FormatSeconds);
        panel.Children.Add(new TextBlock
        {
            Text = "So lange dauert es, bis die neue Lautstärke erreicht ist (und wieder zurück). 0 = sofort.",
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var box = new UiMessageBox
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "Übernehmen",
            CloseButtonText = "Abbrechen",
            SecondaryButtonText = "Wieder normal",
            IsSecondaryButtonEnabled = current != null,
        };

        var result = await box.ShowDialogAsync();
        if (result == UiMessageBoxResult.Secondary)
        {
            return (VolumeChoice.Reset, 0, 0, 0);
        }

        if (result != UiMessageBoxResult.Primary)
        {
            return (VolumeChoice.Cancel, 0, 0, 0);
        }

        _lastDb = dbSlider.Value == 0 ? _lastDb : dbSlider.Value;
        _lastFadeIn = inSlider.Value;
        _lastFadeOut = outSlider.Value;
        return (VolumeChoice.Apply, dbSlider.Value, inSlider.Value, outSlider.Value);
    }

    private static Slider AddSlider(Panel panel, string label, double min, double max, double step, double value, Func<double, string> format)
    {
        var valueText = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(valueText, Dock.Right);
        header.Children.Add(valueText);
        header.Children.Add(new TextBlock { Text = label });

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            SmallChange = step,
            LargeChange = step * 5,
            Value = Math.Clamp(value, min, max),
            Margin = new Thickness(0, 0, 0, 10),
        };
        valueText.Text = format(slider.Value);
        slider.ValueChanged += (_, e) => valueText.Text = format(e.NewValue);

        panel.Children.Add(header);
        panel.Children.Add(slider);
        return slider;
    }

    private static string FormatDb(double db) =>
        db == 0 ? "0 dB (normal)" : (db > 0 ? "+" : "−") + Math.Abs(db).ToString("0.#", CultureInfo.InvariantCulture) + " dB";

    private static string FormatSeconds(double s) =>
        s <= 0 ? "sofort" : s.ToString("0.00", CultureInfo.GetCultureInfo("de-DE")) + " s";
}
