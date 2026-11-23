using System.Diagnostics;
using System.Text;
using ClipManager.Core.Editing;

namespace ClipManager.Core.Export;

/// <summary>Was exportiert wird. LimitMb = Upload-Limit in MB, null = volle Qualität ohne Größenlimit.</summary>
public sealed record ExportRequest(
    string FfmpegPath,
    string Input,
    string Output,
    IReadOnlyList<TimeRange> Ranges,
    IReadOnlyList<TrackEdit> AudioTracks,
    int SourceHeight,
    double SourceFps,
    VideoEncoder Encoder,
    double? LimitMb);

public sealed record ExportResult(bool Success, string? OutputPath, long Bytes, ExportPlan? Plan, string? Error, int Attempts);

/// <summary>Fortschritt 0..1 und ein Text wie "Durchgang 2: 45 %".</summary>
public sealed record ExportProgress(double Fraction, string Text);

/// <summary>
/// Führt den Export aus: FFmpeg starten, Fortschritt melden, danach Größe prüfen
/// und bei Bedarf mit weniger Bitrate wiederholen.
/// </summary>
public sealed class Exporter
{
    public const int MaxAttempts = 3;

    public async Task<ExportResult> RunAsync(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var outputDuration = request.Ranges.Aggregate(TimeSpan.Zero, (sum, r) => sum + r.Duration);
        var plan = request.LimitMb is { } limit
            ? ExportPlanner.Plan(limit, outputDuration, request.SourceHeight, request.SourceFps)
            : FullQualityPlan(request.SourceHeight, request.SourceFps);

        Directory.CreateDirectory(Path.GetDirectoryName(request.Output)!);
        var partial = Path.Combine(Path.GetDirectoryName(request.Output)!, Path.GetFileNameWithoutExtension(request.Output) + ".part.mp4");

        try
        {
            return await RunAttemptsAsync(request, plan, partial, outputDuration, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task<ExportResult> RunAttemptsAsync(
        ExportRequest request,
        ExportPlan plan,
        string partial,
        TimeSpan outputDuration,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        string? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var label = attempt == 1 ? "Exportiere" : $"Noch zu groß, Versuch {attempt}";
            var args = ExportCommand.Build(
                request.Input, partial, request.Ranges, request.AudioTracks, plan, request.Encoder, request.SourceHeight, request.SourceFps);
            Log.Info($"Export Versuch {attempt}: {plan.Describe()}, Encoder {request.Encoder}");

            var (exitCode, stderr) = await RunFfmpegAsync(request.FfmpegPath, args, outputDuration, label, progress, ct).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(partial))
            {
                lastError = stderr.Trim();
                Log.Error($"Export fehlgeschlagen (Exit {exitCode}): {lastError}");
                TryDelete(partial);
                return new ExportResult(false, null, 0, plan, string.IsNullOrEmpty(lastError) ? $"FFmpeg-Fehler {exitCode}" : lastError, attempt);
            }

            var bytes = new FileInfo(partial).Length;
            if (request.LimitMb == null || bytes <= plan.TargetBytes || attempt == MaxAttempts)
            {
                File.Move(partial, request.Output, overwrite: true);
                var fits = request.LimitMb == null || bytes <= plan.TargetBytes;
                return new ExportResult(
                    fits,
                    request.Output,
                    bytes,
                    plan,
                    fits ? null : "Die Datei ist trotz mehrerer Versuche etwas zu groß. Clip kürzen hilft.",
                    attempt);
            }

            plan = plan with { VideoKbps = ExportPlanner.ReduceAfterOvershoot(plan.VideoKbps, bytes, plan.TargetBytes) };
            TryDelete(partial);
        }

        return new ExportResult(false, null, 0, plan, lastError, MaxAttempts);
    }

    /// <summary>"Als Kopie speichern": hohe Qualität, Größe egal.</summary>
    public static ExportPlan FullQualityPlan(int sourceHeight, double sourceFps)
    {
        var height = sourceHeight > 0 ? sourceHeight : 1080;
        return new ExportPlan(ExportPlanner.MaxUsefulKbps(height), 192, height, sourceFps > 1 ? sourceFps : 60, long.MaxValue, false);
    }

    private static async Task<(int ExitCode, string StdErr)> RunFfmpegAsync(
        string ffmpeg,
        IReadOnlyList<string> args,
        TimeSpan duration,
        string label,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        progress?.Report(new ExportProgress(0, $"{label} … 0 %"));

        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (ExportCommand.ParseProgress(line) is { } t && duration > TimeSpan.Zero)
                {
                    var fraction = Math.Clamp(t.TotalSeconds / duration.TotalSeconds, 0, 1);
                    progress?.Report(new ExportProgress(fraction, $"{label} … {fraction * 100:0} %"));
                }
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
