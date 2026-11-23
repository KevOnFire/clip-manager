using ClipManager.Core.Editing;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClipManager.Editor;

/// <summary>
/// Spielt die Tonspuren eines Clips einzeln gemischt ab (NAudio), genau so wie sie exportiert werden:
/// nur eingeschaltete Spuren, mit Lautstärke und stummen Stellen. Das Video läuft stumm daneben,
/// der Editor hält beides synchron.
/// </summary>
public sealed class TrackPlayer : IDisposable
{
    private readonly List<WaveFileReader> _readers = new();
    private readonly List<TrackSampleProvider> _tracks = new();
    private readonly VolumeSampleProvider _master;
    private readonly WaveOutEvent _output;
    private TimeSpan _startTime;
    private bool _playing;

    private TrackPlayer(IReadOnlyList<string> wavFiles, EditTimeline timeline)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Core.Media.AudioTrackCache.SampleRate, 2);
        var mixer = new MixingSampleProvider(format) { ReadFully = true };
        for (var i = 0; i < wavFiles.Count; i++)
        {
            var reader = new WaveFileReader(wavFiles[i]);
            _readers.Add(reader);
            var track = new TrackSampleProvider(reader.ToSampleProvider(), timeline, i);
            _tracks.Add(track);
            mixer.AddMixerInput(track);
        }

        _master = new VolumeSampleProvider(mixer);
        _output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
        _output.Init(_master);
    }

    /// <summary>Legt den Player an. Gibt null zurück, wenn es nicht klappt (z.B. kein Audiogerät).</summary>
    public static TrackPlayer? TryCreate(IReadOnlyList<string> wavFiles, EditTimeline timeline)
    {
        try
        {
            return new TrackPlayer(wavFiles, timeline);
        }
        catch (Exception ex)
        {
            Core.Log.Error("Tonspuren-Vorschau konnte nicht gestartet werden", ex);
            return null;
        }
    }

    public float Volume
    {
        get => _master.Volume;
        set => _master.Volume = value;
    }

    public bool IsPlaying => _playing;

    /// <summary>Was gerade aus dem Lautsprecher kommt (Zeit im Original).</summary>
    public TimeSpan Position
    {
        get
        {
            if (!_playing)
            {
                return _startTime;
            }

            var bytes = _output.GetPosition();
            return _startTime + TimeSpan.FromSeconds(bytes / (double)_output.OutputWaveFormat.AverageBytesPerSecond);
        }
    }

    /// <summary>Startet (oder springt) ab Zeitpunkt t.</summary>
    public void Play(TimeSpan t)
    {
        _output.Stop();
        foreach (var reader in _readers)
        {
            reader.CurrentTime = t < reader.TotalTime ? t : reader.TotalTime;
        }

        foreach (var track in _tracks)
        {
            track.Position = t;
        }

        _startTime = t;
        _playing = true;
        _output.Play();
    }

    public void Stop()
    {
        if (_playing)
        {
            _startTime = Position;
        }

        _playing = false;
        _output.Stop();
    }

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
        foreach (var reader in _readers)
        {
            reader.Dispose();
        }
    }

    /// <summary>Eine Spur: an/aus, Lautstärke und stumme Stellen, Sample für Sample.</summary>
    private sealed class TrackSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly EditTimeline _timeline;
        private readonly int _index;
        private long _frames;
        private TimeSpan _start;

        public TrackSampleProvider(ISampleProvider source, EditTimeline timeline, int index)
        {
            _source = source;
            _timeline = timeline;
            _index = index;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public TimeSpan Position
        {
            set
            {
                _start = value;
                _frames = 0;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            var tracks = _timeline.Tracks;
            var track = _index < tracks.Count ? tracks[_index] : null;
            var channels = WaveFormat.Channels;
            var rate = (double)WaveFormat.SampleRate;

            if (track == null || !track.Enabled || track.Volume <= 0)
            {
                Array.Clear(buffer, offset, read);
            }
            else if (track.Muted.Count == 0)
            {
                if (Math.Abs(track.Volume - 1) > 0.001)
                {
                    var v = (float)track.Volume;
                    for (var i = 0; i < read; i++)
                    {
                        buffer[offset + i] *= v;
                    }
                }
            }
            else
            {
                var v = (float)track.Volume;
                var startSec = _start.TotalSeconds + _frames / rate;
                var muted = track.Muted;
                for (var i = 0; i < read; i += channels)
                {
                    var t = startSec + (i / channels) / rate;
                    var gain = v;
                    for (var r = 0; r < muted.Count; r++)
                    {
                        if (t >= muted[r].Start.TotalSeconds && t < muted[r].End.TotalSeconds)
                        {
                            gain = 0f;
                            break;
                        }
                    }

                    for (var c = 0; c < channels && i + c < read; c++)
                    {
                        buffer[offset + i + c] *= gain;
                    }
                }
            }

            _frames += read / channels;
            return read;
        }
    }
}
