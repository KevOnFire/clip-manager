namespace ClipManager.Core.Export;

public enum VideoEncoder
{
    /// <summary>CPU (libx264). Langsamer, läuft aber überall.</summary>
    Cpu,
    Nvidia,
    Amd,
}

/// <summary>Ergebnis der Planung: mit welchen Werten exportiert wird.</summary>
public sealed record ExportPlan(
    int VideoKbps,
    int AudioKbps,
    int Height,
    double Fps,
    long TargetBytes,
    bool TooLongWarning)
{
    /// <summary>Kurzbeschreibung für die Oberfläche, z.B. "1080p60 · 7.400 kbit/s".</summary>
    public string Describe() =>
        $"{Height}p{Math.Round(Fps)} · {VideoKbps.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} kbit/s";
}

/// <summary>
/// Rechnet aus Zielgröße und Länge die Bitrate aus und wählt passend Auflösung und FPS.
/// Formel siehe CLAUDE.md (Etappe 4).
/// </summary>
public static class ExportPlanner
{
    /// <summary>Puffer für den MP4-Container und Schwankungen des Encoders.</summary>
    public const double ContainerReserve = 0.95;

    /// <summary>Mehr bringt sichtbar nichts mehr, macht die Datei nur größer (abhängig von der Auflösung).</summary>
    public static int MaxUsefulKbps(int height) => height switch
    {
        <= 480 => 3_000,
        <= 720 => 8_000,
        <= 1080 => 14_000,
        <= 1440 => 24_000,
        _ => 40_000,
    };

    public static int VideoKbps(double limitMb, TimeSpan duration, int audioKbps)
    {
        var seconds = Math.Max(0.5, duration.TotalSeconds);
        return (int)Math.Floor(limitMb * 8192 * ContainerReserve / seconds) - audioKbps;
    }

    /// <param name="limitMb">Upload-Limit in MB (MiB, so wie Discord rechnet).</param>
    /// <param name="duration">Länge des fertigen Clips (nach dem Schneiden).</param>
    public static ExportPlan Plan(double limitMb, TimeSpan duration, int sourceHeight, double sourceFps)
    {
        sourceHeight = sourceHeight > 0 ? sourceHeight : 1080;
        sourceFps = sourceFps > 1 ? sourceFps : 60;
        var targetBytes = (long)(limitMb * 1024 * 1024);

        // Bei sehr knappem Limit lieber am Ton sparen als am Bild.
        var audio = 128;
        if (VideoKbps(limitMb, duration, audio) < 1_500)
        {
            audio = 96;
        }

        var video = VideoKbps(limitMb, duration, audio);

        int height;
        double fps;
        var warning = false;
        if (video >= 8_000)
        {
            height = sourceHeight;
            fps = sourceFps;
        }
        else if (video >= 4_000)
        {
            height = Math.Min(sourceHeight, 1080);
            fps = sourceFps;
        }
        else if (video >= 2_000)
        {
            height = Math.Min(sourceHeight, 720);
            fps = Math.Min(sourceFps, 60);
        }
        else if (video >= 1_000)
        {
            height = Math.Min(sourceHeight, 720);
            fps = Math.Min(sourceFps, 30);
        }
        else
        {
            height = Math.Min(sourceHeight, 480);
            fps = Math.Min(sourceFps, 30);
            warning = true;
        }

        // Unter ~250 kbit/s ist es nur noch Matsch; dann trotzdem versuchen, aber warnen.
        video = Math.Max(video, 150);
        video = Math.Min(video, MaxUsefulKbps(height));
        return new ExportPlan(video, audio, height, fps, targetBytes, warning);
    }

    /// <summary>Nach einem zu großen Ergebnis: neue Bitrate mit etwas Abstand nach unten.</summary>
    public static int ReduceAfterOvershoot(int videoKbps, long actualBytes, long targetBytes)
    {
        if (actualBytes <= 0 || targetBytes <= 0)
        {
            return (int)(videoKbps * 0.92);
        }

        var ratio = (double)targetBytes / actualBytes;
        return Math.Max(100, (int)(videoKbps * Math.Min(ratio, 1.0) * 0.93));
    }

    /// <summary>
    /// Welche Tonspuren standardmäßig in den Export kommen: Gibt es mehrere, ist Spur 1 bei OBS
    /// meist der Mix aus allen anderen. Dann nur die Einzelspuren nehmen, sonst ist alles doppelt.
    /// </summary>
    public static IReadOnlyList<int> DefaultAudioTracks(int trackCount) =>
        trackCount switch
        {
            <= 0 => Array.Empty<int>(),
            1 => new[] { 0 },
            _ => Enumerable.Range(1, trackCount - 1).ToArray(),
        };
}
