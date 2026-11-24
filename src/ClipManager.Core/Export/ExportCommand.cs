using System.Globalization;
using System.Text;
using ClipManager.Core.Editing;

namespace ClipManager.Core.Export;

/// <summary>Baut die FFmpeg-Befehlszeile für einen Export.</summary>
public static class ExportCommand
{
    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <param name="ranges">Behaltene Bereiche im Original (aus <see cref="EditTimeline.KeptRanges"/>).</param>
    /// <param name="audioTracks">Tonspuren, die gemischt werden, mit Lautstärke und stummen Stellen.</param>
    public static IReadOnlyList<string> Build(
        string input,
        string output,
        IReadOnlyList<TimeRange> ranges,
        IReadOnlyList<TrackEdit> audioTracks,
        ExportPlan plan,
        VideoEncoder encoder,
        int sourceHeight,
        double sourceFps)
    {
        if (ranges.Count == 0)
        {
            throw new ArgumentException("Es muss mindestens ein Bereich übrig bleiben.", nameof(ranges));
        }

        // Schnelles Springen zum ersten Bereich; danach zählen die Zeiten ab diesem Punkt.
        var offset = ranges[0].Start;
        var end = ranges[^1].End;
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-ss", F(offset.TotalSeconds), "-t", F((end - offset).TotalSeconds), "-i", input,
            "-filter_complex", BuildFilter(ranges, offset, audioTracks, plan, sourceHeight, sourceFps),
            "-map", "[v]",
        };

        if (audioTracks.Count > 0)
        {
            args.AddRange(new[] { "-map", "[a]", "-c:a", "aac", "-b:a", $"{plan.AudioKbps}k", "-ac", "2" });
        }

        args.AddRange(EncoderArgs(encoder, plan.VideoKbps));
        args.AddRange(new[] { "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-map_metadata", "-1", "-y", output });
        return args;
    }

    public static IEnumerable<string> EncoderArgs(VideoEncoder encoder, int videoKbps)
    {
        // Obergrenze (maxrate) und Puffer (bufsize) halten die Größe im Rahmen.
        var b = $"{videoKbps}k";
        var max = $"{(int)(videoKbps * 1.5)}k";
        var buf = $"{videoKbps * 2}k";
        return encoder switch
        {
            VideoEncoder.Nvidia => new[]
            {
                "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres",
                "-b:v", b, "-maxrate", max, "-bufsize", buf, "-profile:v", "high",
            },
            VideoEncoder.Amd => new[]
            {
                "-c:v", "h264_amf", "-quality", "quality", "-rc", "vbr_peak",
                "-b:v", b, "-maxrate", max, "-bufsize", buf, "-profile:v", "high",
            },
            _ => new[]
            {
                "-c:v", "libx264", "-preset", "medium",
                "-b:v", b, "-maxrate", max, "-bufsize", buf, "-profile:v", "high",
            },
        };
    }

    public static string BuildFilter(
        IReadOnlyList<TimeRange> ranges,
        TimeSpan offset,
        IReadOnlyList<TrackEdit> audioTracks,
        ExportPlan plan,
        int sourceHeight,
        double sourceFps)
    {
        var sb = new StringBuilder();
        var n = ranges.Count;

        // Video: Bereiche ausschneiden und aneinanderhängen
        for (var i = 0; i < n; i++)
        {
            var (s, e) = (ranges[i].Start - offset, ranges[i].End - offset);
            sb.Append($"[0:v]trim=start={F(s.TotalSeconds)}:end={F(e.TotalSeconds)},setpts=PTS-STARTPTS[v{i}];");
        }

        var videoOut = "vc";
        if (n > 1)
        {
            for (var i = 0; i < n; i++)
            {
                sb.Append($"[v{i}]");
            }

            sb.Append($"concat=n={n}:v=1:a=0[vc];");
        }
        else
        {
            videoOut = "v0";
        }

        // Skalieren/FPS nur wenn nötig
        var post = new List<string>();
        if (plan.Height < sourceHeight)
        {
            post.Add($"scale=-2:{plan.Height}:flags=lanczos");
        }

        if (plan.Fps < sourceFps - 0.5)
        {
            post.Add($"fps={F(plan.Fps)}");
        }

        sb.Append($"[{videoOut}]{(post.Count > 0 ? string.Join(',', post) : "null")}[v]");

        // Ton: pro Spur die Bereiche aneinanderhängen, dann alle Spuren mischen
        if (audioTracks.Count > 0)
        {
            sb.Append(';');
            foreach (var edit in audioTracks)
            {
                var track = edit.Index;

                // Lautstärke und stumme Stellen zuerst, auf der ganzen Spur (Zeiten ab dem Sprungpunkt),
                // danach in Stücke schneiden.
                var gain = TrackGainFilter(edit, offset);
                if (n > 1)
                {
                    sb.Append($"[0:a:{track}]{gain}asplit={n}");
                    for (var i = 0; i < n; i++)
                    {
                        sb.Append($"[s{track}_{i}]");
                    }

                    sb.Append(';');
                }

                for (var i = 0; i < n; i++)
                {
                    var (s, e) = (ranges[i].Start - offset, ranges[i].End - offset);
                    var source = n > 1 ? $"[s{track}_{i}]" : $"[0:a:{track}]{gain}";
                    sb.Append($"{source}atrim=start={F(s.TotalSeconds)}:end={F(e.TotalSeconds)},asetpts=PTS-STARTPTS[a{track}_{i}];");
                }

                if (n > 1)
                {
                    for (var i = 0; i < n; i++)
                    {
                        sb.Append($"[a{track}_{i}]");
                    }

                    sb.Append($"concat=n={n}:v=0:a=1[t{track}];");
                }
                else
                {
                    sb.Append($"[a{track}_0]anull[t{track}];");
                }
            }

            if (audioTracks.Count == 1)
            {
                sb.Append($"[t{audioTracks[0].Index}]anull[a]");
            }
            else
            {
                foreach (var edit in audioTracks)
                {
                    sb.Append($"[t{edit.Index}]");
                }

                // normalize=0: Spuren nicht leiser machen, nur zusammenlegen (wie OBS-Spur 1)
                sb.Append($"amix=inputs={audioTracks.Count}:duration=longest:normalize=0[a]");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Filter für Lautstärke und stumme Stellen einer Spur, mit Komma am Ende (oder leer).
    /// Beispiel: "volume=1.5,asetnsamples=n=64:p=0,volume=0:enable='between(t,1.994,4)',".
    /// </summary>
    private const double MuteEdge = 0.006;

    public static string TrackGainFilter(TrackEdit edit, TimeSpan offset)
    {
        var sb = new StringBuilder();
        if (Math.Abs(edit.Volume - 1) > 0.001)
        {
            sb.Append($"volume={F(edit.Volume)},");
        }

        var muted = edit.Muted
            .Select(r => (Start: (r.Start - offset).TotalSeconds, End: (r.End - offset).TotalSeconds))
            .Where(r => r.End > 0)
            .Select(r => $"between(t,{F(Math.Max(0, r.Start - MuteEdge))},{F(r.End)})")
            .ToList();

        // Leiser gemachte Stellen: Faktor 10^(dB·w/20), w steigt am Anfang über die Übergangszeit von 0 auf 1
        // und fällt am Ende wieder ab (gleiche Kurve wie DuckRange.GainAt in der Vorschau).
        var ducks = edit.Ducked
            .Select(d => (Start: (d.Range.Start - offset).TotalSeconds, End: (d.Range.End - offset).TotalSeconds, d.Db, d.Fade))
            .Where(d => d.End > 0 && d.Fade > 0)
            .Select(d => $"pow(10,{F(d.Db)}*clip(min((t-{F(d.Start)})/{F(d.Fade)},({F(d.End)}-t)/{F(d.Fade)}),0,1)/20)")
            .ToList();

        if (muted.Count > 0 || ducks.Count > 0)
        {
            // Kleine Audio-Pakete (64 Samples), damit "stumm" fast auf die Millisekunde genau greift
            // und Übergänge glatt sind, plus ein paar Millisekunden Vorlauf gegen Knacken an der Kante.
            sb.Append("asetnsamples=n=64:p=0,");
        }

        if (ducks.Count > 0)
        {
            sb.Append($"volume='{string.Join('*', ducks)}':eval=frame,");
        }

        if (muted.Count > 0)
        {
            sb.Append($"volume=0:enable='{string.Join('+', muted)}',");
        }

        return sb.ToString();
    }

    /// <summary>Liest "out_time_us=..." aus der -progress-Ausgabe. Gibt null bei anderen Zeilen zurück.</summary>
    public static TimeSpan? ParseProgress(string line)
    {
        const string key = "out_time_us=";
        if (!line.StartsWith(key, StringComparison.Ordinal)
            || !long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)
            || us < 0)
        {
            return null;
        }

        return TimeSpan.FromTicks(us * 10);
    }

    /// <summary>Freier Dateiname im Export-Ordner, z.B. "Replay … (Discord).mp4", "(Discord 2)".</summary>
    public static string OutputPath(string exportFolder, string clipPath, string suffix)
    {
        var name = Path.GetFileNameWithoutExtension(clipPath);
        var candidate = Path.Combine(exportFolder, $"{name} ({suffix}).mp4");
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(exportFolder, $"{name} ({suffix} {i}).mp4");
        }

        return candidate;
    }
}
