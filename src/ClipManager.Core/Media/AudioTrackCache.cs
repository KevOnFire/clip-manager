using System.Security.Cryptography;
using System.Text;
using ClipManager.Core.Library;

namespace ClipManager.Core.Media;

/// <summary>
/// Holt jede Tonspur eines Clips als eigene WAV-Datei heraus (48 kHz, Stereo, 16 Bit),
/// damit der Editor die Spuren einzeln mischen und abspielen kann. Liegt in einem Cache-Ordner,
/// alte Dateien werden nach ein paar Tagen weggeräumt.
/// </summary>
public sealed class AudioTrackCache
{
    public const int SampleRate = 48_000;
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(3);

    private readonly string _cacheDirectory;
    private readonly IProcessRunner _runner;

    public AudioTrackCache(string cacheDirectory, IProcessRunner runner)
    {
        _cacheDirectory = cacheDirectory;
        _runner = runner;
    }

    public string PathFor(ClipFile clip, int track)
    {
        var key = string.Join('|', "wav", clip.Path.ToUpperInvariant(), clip.SizeBytes, clip.ModifiedUtc.Ticks);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return System.IO.Path.Combine(_cacheDirectory, $"{hash}_{track}.wav");
    }

    public static IReadOnlyList<string> BuildArguments(string input, IReadOnlyList<string> outputs)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", input };
        for (var i = 0; i < outputs.Count; i++)
        {
            args.AddRange(new[] { "-map", $"0:a:{i}", "-vn", "-ac", "2", "-ar", SampleRate.ToString(), "-c:a", "pcm_s16le", "-y", outputs[i] });
        }

        return args;
    }

    /// <summary>Gibt pro Spur den WAV-Pfad zurück, oder null, wenn es nicht geklappt hat.</summary>
    public async Task<IReadOnlyList<string>?> GetOrCreateAsync(string ffmpegPath, ClipFile clip, int trackCount, CancellationToken ct = default)
    {
        if (trackCount <= 0)
        {
            return Array.Empty<string>();
        }

        var targets = Enumerable.Range(0, trackCount).Select(i => PathFor(clip, i)).ToList();
        if (targets.All(File.Exists))
        {
            return targets;
        }

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            CleanupOld();
            var partials = targets.Select(t => t[..^4] + ".part.wav").ToList();
            var result = await _runner.RunAsync(ffmpegPath, BuildArguments(clip.Path, partials), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || !partials.All(File.Exists))
            {
                Log.Error($"Tonspuren für \"{clip.Path}\" konnten nicht gelesen werden (Exit {result.ExitCode}): {result.StdErr.Trim()}");
                foreach (var p in partials.Where(File.Exists))
                {
                    File.Delete(p);
                }

                return null;
            }

            for (var i = 0; i < targets.Count; i++)
            {
                File.Move(partials[i], targets[i], overwrite: true);
            }

            return targets;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Tonspuren für \"{clip.Path}\" konnten nicht gelesen werden", ex);
            return null;
        }
    }

    private void CleanupOld()
    {
        foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.wav"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeepFor)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Wird gerade benutzt, dann eben beim nächsten Mal.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Liest Lautstärke-Spitzen aus einer 16-Bit-PCM-WAV-Datei, für die Wellenform im Editor.</summary>
public static class Waveform
{
    /// <summary>Unterster Pegel, der in der Wellenform noch sichtbar ist (in dB).</summary>
    public const double FloorDb = -50;

    /// <summary>
    /// Rechnet einen Spitzenwert (0..1) in eine Höhe (0..1) für die Anzeige um. Logarithmisch (dB) wie in
    /// Schnittprogrammen üblich, damit auch leise Spuren (z.B. Musik im Hintergrund) sichtbar sind.
    /// </summary>
    public static double DisplayHeight(float peak)
    {
        if (peak <= 0)
        {
            return 0;
        }

        var db = 20 * Math.Log10(Math.Min(1.0, peak));
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    /// <summary>Teilt die Datei in "buckets" gleich lange Stücke und gibt pro Stück den lautesten Wert (0..1) zurück.</summary>
    public static float[] ReadPeaks(string wavPath, int buckets)
    {
        using var stream = File.OpenRead(wavPath);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Keine WAV-Datei");
        }

        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Keine WAV-Datei");
        }

        short channels = 2, bits = 16;
        long dataLength = 0;
        while (stream.Position < stream.Length - 8)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            if (id == "fmt ")
            {
                reader.ReadInt16();
                channels = reader.ReadInt16();
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
                stream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                // FFmpeg schreibt bei Pipes manchmal eine falsche Länge; dann bis Dateiende lesen.
                dataLength = size == 0 || size == uint.MaxValue || stream.Position + size > stream.Length
                    ? stream.Length - stream.Position
                    : size;
                break;
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        if (bits != 16 || dataLength <= 0 || buckets <= 0)
        {
            return new float[Math.Max(0, buckets)];
        }

        var frameBytes = 2 * channels;
        var frames = dataLength / frameBytes;
        var peaks = new float[buckets];
        var buffer = new byte[frameBytes * 4096];
        long frame = 0;
        int read;
        while (frame < frames && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, (frames - frame) * frameBytes))) > 0)
        {
            for (var offset = 0; offset + frameBytes <= read; offset += frameBytes, frame++)
            {
                var bucket = (int)(frame * buckets / frames);
                for (var c = 0; c < channels; c++)
                {
                    var sample = Math.Abs(BitConverter.ToInt16(buffer, offset + c * 2) / 32768f);
                    if (sample > peaks[bucket])
                    {
                        peaks[bucket] = sample;
                    }
                }
            }
        }

        return peaks;
    }
}
