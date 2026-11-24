using ClipManager.Core.Settings;


namespace ClipManager.Core.Tests;

public class WindowBoundsTests
{
    [Fact]
    public void BleibtGleich_WennEsPasst()
    {
        var b = new WindowBounds { Left = 100, Top = 50, Width = 1600, Height = 900, Maximized = true };
        var fit = b.FitInto(0, 0, 2560, 1400, 900, 560)!;
        Assert.Equal((100d, 50d, 1600d, 900d, true), (fit.Left, fit.Top, fit.Width, fit.Height, fit.Maximized));
    }

    [Fact]
    public void WirdZurueckGeschoben_WennMonitorFehlt()
    {
        // War auf einem zweiten Monitor rechts, der jetzt fehlt.
        var b = new WindowBounds { Left = 3000, Top = 100, Width = 1280, Height = 800 };
        var fit = b.FitInto(0, 0, 1920, 1040, 900, 560)!;
        Assert.Equal(640, fit.Left);
        Assert.Equal(100, fit.Top);
    }

    [Fact]
    public void ZuGross_WirdAufBildschirmVerkleinert()
    {
        var b = new WindowBounds { Left = -50, Top = -50, Width = 5000, Height = 3000 };
        var fit = b.FitInto(0, 0, 1920, 1040, 900, 560)!;
        Assert.Equal((0d, 0d, 1920d, 1040d), (fit.Left, fit.Top, fit.Width, fit.Height));
    }

    [Fact]
    public void Unbrauchbar_GibtNull()
    {
        Assert.Null(new WindowBounds { Width = 0, Height = 800 }.FitInto(0, 0, 1920, 1040, 900, 560));
        Assert.Null(new WindowBounds { Width = double.NaN, Height = 800 }.FitInto(0, 0, 1920, 1040, 900, 560));
    }

    [Fact]
    public void WirdInEinstellungenGespeichert()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(System.IO.Path.Combine(dir.Path, "s.json"));
        var s = new AppSettings { Window = new WindowBounds { Left = 10, Top = 20, Width = 1500, Height = 950, Maximized = true } };
        store.Save(s);
        var loaded = store.Load();
        Assert.NotNull(loaded.Window);
        Assert.Equal(1500, loaded.Window!.Width);
        Assert.True(loaded.Window.Maximized);
    }
}
