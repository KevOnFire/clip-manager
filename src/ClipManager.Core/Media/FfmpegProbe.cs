namespace ClipManager.Core.Media;

public enum GpuEncoderState
{
    /// <summary>Der FFmpeg-Build kennt den Encoder gar nicht.</summary>
    NotInBuild,

    /// <summary>FFmpeg kennt ihn, aber er läuft hier nicht (keine passende Grafikkarte oder Treiber).</summary>
    Unavailable,

    /// <summary>Getestet und funktioniert.</summary>
    Available,
}

public sealed record FfmpegStatus(
    bool Found,
    FfmpegPaths? Paths,
    string? Version,
    GpuEncoderState Nvidia,
    GpuEncoderState Amd,
    string? Error)
{
    public static FfmpegStatus NotFound(string error) =>
        new(false, null, null, GpuEncoderState.NotInBuild, GpuEncoderState.NotInBuild, error);
}

/// <summary>Prüft, ob FFmpeg läuft und welche GPU-Encoder wirklich nutzbar sind.</summary>
public sealed class FfmpegProbe
{
    public const string NvidiaEncoder = "h264_nvenc";
    public const string AmdEncoder = "h264_amf";

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EncodeTestTimeout = TimeSpan.FromSeconds(20);

    private readonly IProcessRunner _runner;

    public FfmpegProbe(IProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<FfmpegStatus> CheckAsync(FfmpegPaths? paths, CancellationToken cancellationToken = default)
    {
        if (paths == null)
        {
            return FfmpegStatus.NotFound("FFmpeg wurde nicht gefunden.");
        }

        string? version;
        HashSet<string> encoders;
        try
        {
            var versionResult = await _runner.RunAsync(paths.Ffmpeg, new[] { "-hide_banner", "-version" }, QuickTimeout, cancellationToken);
            version = ParseVersion(versionResult.StdOut);
            if (versionResult.ExitCode != 0 || version == null)
            {
                return FfmpegStatus.NotFound("FFmpeg startet, liefert aber keine gültige Version.");
            }

            var encoderResult = await _runner.RunAsync(paths.Ffmpeg, new[] { "-hide_banner", "-encoders" }, QuickTimeout, cancellationToken);
            encoders = ParseEncoders(encoderResult.StdOut);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return FfmpegStatus.NotFound($"FFmpeg konnte nicht gestartet werden: {ex.Message}");
        }

        var nvidia = await TestEncoderAsync(paths.Ffmpeg, encoders, NvidiaEncoder, cancellationToken);
        var amd = await TestEncoderAsync(paths.Ffmpeg, encoders, AmdEncoder, cancellationToken);
        return new FfmpegStatus(true, paths, version, nvidia, amd, null);
    }

    /// <summary>Kodiert ein paar schwarze Bilder, um zu sehen, ob der Encoder auf dieser Hardware läuft.</summary>
    private async Task<GpuEncoderState> TestEncoderAsync(string ffmpeg, IReadOnlySet<string> encoders, string encoder, CancellationToken cancellationToken)
    {
        if (!encoders.Contains(encoder))
        {
            return GpuEncoderState.NotInBuild;
        }

        try
        {
            var result = await _runner.RunAsync(
                ffmpeg,
                new[]
                {
                    "-hide_banner", "-loglevel", "error",
                    "-f", "lavfi", "-i", "color=c=black:s=256x256:r=30:d=0.2",
                    "-frames:v", "5", "-c:v", encoder, "-f", "null", "-",
                },
                EncodeTestTimeout,
                cancellationToken);
            return result.ExitCode == 0 ? GpuEncoderState.Available : GpuEncoderState.Unavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GpuEncoderState.Unavailable;
        }
    }

    /// <summary>Liest die Version aus der ersten Zeile von "ffmpeg -version".</summary>
    public static string? ParseVersion(string output)
    {
        const string prefix = "ffmpeg version ";
        var firstLine = output.Split('\n', 2)[0].Trim();
        if (!firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = firstLine[prefix.Length..];
        var end = rest.IndexOf(' ');
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>
    /// Liest die Encoder-Namen aus "ffmpeg -encoders". Zeilen sehen so aus:
    /// " V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)".
    /// </summary>
    public static HashSet<string> ParseEncoders(string output)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pastHeader = false;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("------", StringComparison.Ordinal))
            {
                pastHeader = true;
                continue;
            }

            if (!pastHeader || line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6)
            {
                result.Add(parts[1]);
            }
        }

        return result;
    }
}
