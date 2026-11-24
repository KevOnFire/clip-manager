using ClipManager.Core.Settings;

namespace ClipManager.Core.Tests;

public class ThemeTests
{
    [Fact]
    public void StandardmaessigAus()
    {
        Assert.False(new AppSettings().Theme.Enabled);
    }

    [Theory]
    [InlineData(0, 0, 0.5, 1, 0.5)]     // links → rechts
    [InlineData(90, 0.5, 0, 0.5, 1)]    // oben → unten
    [InlineData(180, 1, 0.5, 0, 0.5)]   // rechts → links
    [InlineData(45, 0, 0, 1, 1)]        // Ecke oben links → unten rechts
    public void Verlaufspunkte(double angle, double x1, double y1, double x2, double y2)
    {
        var p = ThemeSettings.GradientPoints(angle);
        Assert.Equal(x1, p.X1, 5);
        Assert.Equal(y1, p.Y1, 5);
        Assert.Equal(x2, p.X2, 5);
        Assert.Equal(y2, p.Y2, 5);
    }

    [Fact]
    public void UngueltigeFarben_FallenAufVorlageZurueck()
    {
        var t = new ThemeSettings { Colors = new List<string> { "#FF0000", "rot" } };
        Assert.Equal(ThemeSettings.Presets[0].Colors, t.ValidColors());

        t.Colors = new List<string> { "#FF0000", "#00FF00", "kaputt", "#0000FF", "#111111", "#222222", "#333333" };
        Assert.Equal(new[] { "#FF0000", "#00FF00", "#0000FF", "#111111", "#222222" }, t.ValidColors());
    }

    [Fact]
    public void Staerke_WirdBegrenzt()
    {
        Assert.Equal(1, new ThemeSettings { Strength = 5 }.ValidStrength());
        Assert.Equal(0.1, new ThemeSettings { Strength = 0 }.ValidStrength());
        Assert.Equal(0.55, new ThemeSettings { Strength = double.NaN }.ValidStrength());
    }

    [Fact]
    public void WirdGespeichert()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(System.IO.Path.Combine(dir.Path, "s.json"));
        var s = new AppSettings();
        s.Theme.Enabled = true;
        s.Theme.Colors = new List<string> { "#112233", "#445566", "#778899" };
        s.Theme.Angle = 30;
        store.Save(s);
        var loaded = store.Load();
        Assert.True(loaded.Theme.Enabled);
        Assert.Equal(new[] { "#112233", "#445566", "#778899" }, loaded.Theme.Colors);
        Assert.Equal(30, loaded.Theme.Angle);
    }
}
