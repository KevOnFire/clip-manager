namespace ClipManager.Core.Settings;

/// <summary>
/// Eigenes Design: Farbverlauf als Hintergrund des Programms. Standardmäßig aus (dann normales dunkles Design).
/// </summary>
public sealed class ThemeSettings
{
    public const int MinColors = 2;
    public const int MaxColors = 5;

    public bool Enabled { get; set; }

    /// <summary>Farben des Verlaufs als "#RRGGBB", von Anfang bis Ende.</summary>
    public List<string> Colors { get; set; } = new(Presets[0].Colors);

    /// <summary>Richtung in Grad: 0 = von links nach rechts, 90 = von oben nach unten.</summary>
    public double Angle { get; set; } = Presets[0].Angle;

    /// <summary>Wie stark der Verlauf durchscheint (0,1 = kaum, 1 = voll).</summary>
    public double Strength { get; set; } = 0.55;

    /// <summary>Fertige Verläufe zum Ausprobieren.</summary>
    public static readonly IReadOnlyList<ThemePreset> Presets = new[]
    {
        new ThemePreset("Lila Nacht", new[] { "#2B1055", "#7597DE" }, 135),
        new ThemePreset("Sonnenuntergang", new[] { "#3A1C71", "#D76D77", "#FFAF7B" }, 120),
        new ThemePreset("Ozean", new[] { "#0F2027", "#203A43", "#2C5364" }, 90),
        new ThemePreset("Neon", new[] { "#FC466B", "#3F5EFB" }, 45),
        new ThemePreset("Wald", new[] { "#0B3D2E", "#3A7D44", "#A8C686" }, 160),
        new ThemePreset("Glut", new[] { "#200122", "#6F0000" }, 90),
        new ThemePreset("Pink", new[] { "#41295A", "#FF5FA2" }, 135),
    };

    /// <summary>Nur gültige Farben, mindestens 2 und höchstens 5; sonst der erste fertige Verlauf.</summary>
    public IReadOnlyList<string> ValidColors()
    {
        var colors = (Colors ?? new List<string>()).Where(AppSettings.IsHexColor).Take(MaxColors).ToList();
        return colors.Count >= MinColors ? colors : Presets[0].Colors;
    }

    public double ValidStrength() => double.IsFinite(Strength) ? Math.Clamp(Strength, 0.1, 1) : 0.55;

    /// <summary>
    /// Start- und Endpunkt des Verlaufs (0–1 in der Fläche) für einen Winkel, so dass der Verlauf
    /// immer von Kante zu Kante durch die Mitte läuft.
    /// </summary>
    public static (double X1, double Y1, double X2, double Y2) GradientPoints(double angleDegrees)
    {
        if (!double.IsFinite(angleDegrees))
        {
            angleDegrees = 0;
        }

        var rad = angleDegrees * Math.PI / 180;
        var dx = Math.Cos(rad);
        var dy = Math.Sin(rad);
        // Bis zur Kante des Quadrats strecken (|dx| oder |dy| wird 0,5).
        var scale = 0.5 / Math.Max(Math.Abs(dx), Math.Abs(dy));
        dx *= scale;
        dy *= scale;
        return (Round(0.5 - dx), Round(0.5 - dy), Round(0.5 + dx), Round(0.5 + dy));
    }

    private static double Round(double v) => Math.Round(v, 6);
}

public sealed record ThemePreset(string Name, IReadOnlyList<string> Colors, double Angle);
