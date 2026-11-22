using NAudio.Wave;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Audio;

/// <summary>
/// Dual-microphone aligned equal-headroom mixer (120 ms PC mic delay + 48 kHz stereo output).
/// Implements NAudio <see cref="ISampleProvider"/> for zero-copy pull by WasapiOut.
/// </summary>
public sealed class DualMicMixer : ISampleProvider
{
    public const int OutputSampleRate = 48_000;
    public const int OutputChannels = 2;
    private const float RemoteGain = 10.0f; // +20 dB calibration matching macOS AudioPipe
    private const double AlignmentDelaySeconds = 0.12;
    private const int AlignmentDelayFrames = (int)(OutputSampleRate * AlignmentDelaySeconds);
    private const int MaxQueuedFrames = OutputSampleRate * 2;

    private readonly object _sync = new();
    private readonly List<float> _pendingPc = new(OutputSampleRate);
    private readonly List<float> _pendingRemote = new(OutputSampleRate);
    private int _pcReadIndex;
    private int _remoteReadIndex;

    private bool _mixActive;
    private bool _pcEnabled = true;
    private bool _remoteEnabled = true;
    private int _remoteSessionBuffers;
    private bool _loggedFirstRender;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels);

    public void SetMixActive(bool active)
    {
        lock (_sync)
        {
            bool changed = _mixActive != active;
            _mixActive = active;
            ClearLocked();
            if (active && changed)
            {
                _remoteSessionBuffers = 0;
                _loggedFirstRender = false;
            }
        }
    }

    public void SetInputEnabled(bool pcEnabled, bool remoteEnabled)
    {
        lock (_sync)
        {
            _pcEnabled = pcEnabled;
            _remoteEnabled = remoteEnabled;
            if (!pcEnabled)
            {
                _pendingPc.Clear();
                _pcReadIndex = 0;
            }
            if (!remoteEnabled)
            {
                _pendingRemote.Clear();
                _remoteReadIndex = 0;
            }
        }
    }

    public void FeedRemotePcm(short[] samples, int inputSampleRate)
    {
        if (samples.Length == 0 || inputSampleRate <= 0) return;

        bool active;
        lock (_sync)
        {
            active = _mixActive && _remoteEnabled;
        }
        if (!active) return;

        var floats = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float amplified = (samples[i] / 32768.0f) * RemoteGain;
            floats[i] = Math.Clamp(amplified, -1.0f, 1.0f);
        }

        ScheduleMono(floats, inputSampleRate, isRemote: true);
    }

    public void FeedPcMono(float[] monoSamples, int inputSampleRate)
    {
        if (monoSamples.Length == 0 || inputSampleRate <= 0) return;
        bool active;
        lock (_sync)
        {
            active = _mixActive && _pcEnabled;
        }
        if (!active) return;

        ScheduleMono(monoSamples, inputSampleRate, isRemote: false);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / OutputChannels;
        if (frames <= 0) return 0;

        float[] pc = new float[frames];
        float[] remote = new float[frames];
        int pcTaken = 0;
        int remoteTaken = 0;
        string? firstRenderLog = null;

        lock (_sync)
        {
            if (_mixActive)
            {
                int remoteAvail = _pendingRemote.Count - _remoteReadIndex;
                if (remoteAvail > 0)
                {
                    remoteTaken = Math.Min(frames, remoteAvail);
                    _pendingRemote.CopyTo(_remoteReadIndex, remote, 0, remoteTaken);
                    _remoteReadIndex += remoteTaken;
                    if (!_loggedFirstRender)
                    {
                        _loggedFirstRender = true;
                        float peak = 0;
                        for (int i = 0; i < remoteTaken; i++)
                        {
                            float a = Math.Abs(remote[i]);
                            if (a > peak) peak = a;
                        }
                        firstRenderLog = $"[AUDIO] Remote first render frames={remoteTaken} peak={peak:F4}";
                    }
                }

                int pcAvail = _pendingPc.Count - _pcReadIndex;
                int pcReady = Math.Max(0, pcAvail - AlignmentDelayFrames);
                if (pcReady > 0)
                {
                    pcTaken = Math.Min(frames, pcReady);
                    _pendingPc.CopyTo(_pcReadIndex, pc, 0, pcTaken);
                    _pcReadIndex += pcTaken;
                }

                CompactLocked(_pendingRemote, ref _remoteReadIndex, extraFrames: 0);
                CompactLocked(_pendingPc, ref _pcReadIndex, extraFrames: AlignmentDelayFrames);
            }
        }

        if (firstRenderLog is not null)
        {
            LogService.Write(firstRenderLog);
        }

        for (int f = 0; f < frames; f++)
        {
            bool hasPc = f < pcTaken;
            bool hasRemote = f < remoteTaken;
            float mixed = (hasPc, hasRemote) switch
            {
                (true, true) => (pc[f] + remote[f]) * 0.5f,
                (true, false) => pc[f],
                (false, true) => remote[f],
                _ => 0.0f
            };
            mixed = Math.Clamp(mixed, -1.0f, 1.0f);

            int outIdx = offset + f * OutputChannels;
            buffer[outIdx] = mixed;
            buffer[outIdx + 1] = mixed;
        }

        return frames * OutputChannels;
    }

    private void ScheduleMono(float[] input, int sampleRate, bool isRemote)
    {
        float[] resampled = ResampleLinear(input, sampleRate, OutputSampleRate);
        if (resampled.Length == 0) return;

        string? firstBufLog = null;
        lock (_sync)
        {
            if (!_mixActive) return;
            if (isRemote)
            {
                if (!_remoteEnabled) return;
                CompactLocked(_pendingRemote, ref _remoteReadIndex, 0);
                _pendingRemote.AddRange(resampled);
                _remoteSessionBuffers++;
                if (_remoteSessionBuffers == 1)
                {
                    float peak = resampled.Max(Math.Abs);
                    firstBufLog = $"[AUDIO] Remote first buffer frames={resampled.Length} peak={peak:F4}";
                }
            }
            else
            {
                if (!_pcEnabled) return;
                CompactLocked(_pendingPc, ref _pcReadIndex, AlignmentDelayFrames);
                _pendingPc.AddRange(resampled);
            }
        }

        if (firstBufLog is not null)
        {
            LogService.Write(firstBufLog);
        }
    }

    private static float[] ResampleLinear(float[] input, double inputRate, double outputRate)
    {
        if (Math.Abs(inputRate - outputRate) < 1.0)
        {
            return input;
        }

        double ratio = outputRate / inputRate;
        int outputCount = Math.Max(1, (int)Math.Floor(input.Length * ratio));
        var output = new float[outputCount];
        for (int i = 0; i < outputCount; i++)
        {
            double pos = i / ratio;
            int lower = Math.Min((int)pos, input.Length - 1);
            int upper = Math.Min(lower + 1, input.Length - 1);
            float frac = (float)(pos - lower);
            output[i] = input[lower] + (input[upper] - input[lower]) * frac;
        }
        return output;
    }

    private void ClearLocked()
    {
        _pendingPc.Clear();
        _pcReadIndex = 0;
        _pendingRemote.Clear();
        _remoteReadIndex = 0;
    }

    private static void CompactLocked(List<float> buffer, ref int readIndex, int extraFrames)
    {
        if (readIndex > 4_096)
        {
            buffer.RemoveRange(0, readIndex);
            readIndex = 0;
        }

        int maxQueued = MaxQueuedFrames + extraFrames;
        int avail = buffer.Count - readIndex;
        if (avail > maxQueued)
        {
            int overflow = avail - maxQueued;
            readIndex += overflow;
            if (readIndex > 4_096)
            {
                buffer.RemoveRange(0, readIndex);
                readIndex = 0;
            }
        }
    }
}

