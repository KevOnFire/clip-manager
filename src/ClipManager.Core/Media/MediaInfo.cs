using System.Globalization;
using System.Text.Json;

namespace ClipManager.Core.Media;

public sealed record AudioTrackInfo(int Index, string? Title, int Channels);

/// <summary>Technische Infos zu einem Clip, gelesen mit ffprobe.</summary>
public sealed record MediaInfo(
    TimeSpan Duration,
    int Width,
    int Height,
    double Fps,
    IReadOnlyList<AudioTrackInfo> AudioTracks)
{
    /// <summary>Liest die JSON-Ausgabe von "ffprobe -print_format json -show_format -show_streams".</summary>
    public static MediaInfo? ParseFfprobeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var duration = TimeSpan.Zero;
            if (root.TryGetProperty("format", out var format) && TryGetDouble(format, "duration", out var seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            int width = 0, height = 0;
            double fps = 0;
            var audio = new List<AudioTrackInfo>();

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    if (type == "video" && width == 0 && !IsAttachedPicture(stream))
                    {
                        width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        fps = ParseRate(stream, "avg_frame_rate");
                        if (fps <= 0)
                        {
                            fps = ParseRate(stream, "r_frame_rate");
                        }

                        if (duration == TimeSpan.Zero && TryGetDouble(stream, "duration", out var streamSeconds))
                        {
                            duration = TimeSpan.FromSeconds(streamSeconds);
                        }
                    }
                    else if (type == "audio")
                    {
                        var channels = stream.TryGetProperty("channels", out var c) ? c.GetInt32() : 0;
                        string? title = null;
                        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var tt))
                        {
                            title = tt.GetString();
                        }

                        audio.Add(new AudioTrackInfo(audio.Count, string.IsNullOrWhiteSpace(title) ? null : title, channels));
                    }
                }
            }

            return new MediaInfo(duration, width, height, fps, audio);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d)
        && d.TryGetProperty("attached_pic", out var a)
        && a.ValueKind == JsonValueKind.Number
        && a.GetInt32() == 1;

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var p))
        {
            return false;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static double ParseRate(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty(name, out var p) || p.GetString() is not { } text)
        {
            return 0;
        }

        var parts = text.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0)
        {
            return Math.Round(num / den, 2);
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : 0;
    }
}

/// <summary>Liest <see cref="MediaInfo"/> über ffprobe.</summary>
public sealed class MediaInfoReader
{
    private readonly IProcessRunner _runner;

    public MediaInfoReader(IProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<MediaInfo?> ReadAsync(string ffprobePath, string file, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            ffprobePath,
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0 ? MediaInfo.ParseFfprobeJson(result.StdOut) : null;
    }
}
