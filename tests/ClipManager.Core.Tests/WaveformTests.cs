using ClipManager.Core.Media;

namespace ClipManager.Core.Tests;

public class WaveformTests
{
    [Fact]
    public void DisplayHeight_ShowsQuietMusic()
    {
        Assert.Equal(0, Waveform.DisplayHeight(0));
        Assert.Equal(1, Waveform.DisplayHeight(1), 3);
        // Leise Musik (-30 dB ≈ 0,03) war vorher praktisch flach und muss jetzt deutlich sichtbar sein.
        Assert.True(Waveform.DisplayHeight(0.0316f) > 0.35);
        // Lauter ist immer höher.
        Assert.True(Waveform.DisplayHeight(0.5f) > Waveform.DisplayHeight(0.1f));
        // Rauschen weit unter der Grenze bleibt flach.
        Assert.Equal(0, Waveform.DisplayHeight(0.001f));
    }
}
