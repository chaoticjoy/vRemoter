using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using vRemoter.Core.Protocol;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Bluetooth;

public sealed partial class WinRtBleBridge
{
    private void OnCommandValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        HandleControl(args.CharacteristicValue.ToArray());

    private void OnControlValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        HandleControl(args.CharacteristicValue.ToArray());

    private void OnAudioValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        HandleAudio(args.CharacteristicValue.ToArray());

    private void HandleControl(byte[] data)
    {
        var ev = _protocol.ParseControl(data);
        switch (ev)
        {
            case AtvvControlEvent.StartSearch:
                var now = DateTimeOffset.UtcNow;
                if (_isStreaming || (now - _lastStartSearchAt) < TimeSpan.FromSeconds(0.5)) return;
                _lastStartSearchAt = now;
                OpenMicrophone();
                break;

            case AtvvControlEvent.Capabilities caps:
                try
                {
                    _protocol.AcceptCapabilities(caps.Value);
                    LogService.Write($"[{_logTag}] caps ok v{caps.Value.Version.Describe()} codec={_protocol.Codec?.Describe()} frame={caps.Value.FrameSize}");
                    ResetStaleSessionIfNeeded();
                }
                catch (Exception ex)
                {
                    LogService.Write($"[{_logTag}] caps error: {ex.Message}");
                }
                break;

            case AtvvControlEvent.AudioStart start:
                CancelCloseTimeout();
                _protocol.BeginAudioStream(start.Codec);
                _streamId = start.StreamId;
                _isStreaming = true;
                _streamFrameCount = 0;
                _streamSampleCount = 0;
                _streamPeak = 0;
                _streamSumSq = 0;
                _streamClippedCount = 0;
                _streamSyncCount = _pendingSyncCount;
                _pendingSyncCount = 0;
                if (AppStorage.Shared.RecordingEnabled)
                {
                    _wavRecorder = WavRecorder.CreateNext(_recordingPrefix);
                }
                StartKeepAliveTimer();
                StreamingChanged?.Invoke(true, start.Codec.SampleRate());
                AudioStarted?.Invoke(start.Reason, start.Codec.SampleRate());
                LogService.Write($"[{_logTag}] AUDIO_START reason=0x{start.Reason:x2} streamID={start.StreamId}");
                break;

            case AtvvControlEvent.AudioStop stop:
                if (!_isStreaming && data.Length > 2)
                {
                    LogService.Write($"[{_logTag}] AUDIO_STOP (idle) payload hex={BitConverter.ToString(data)}");
                }
                FinishAudioStreamLocally(stop.Reason);
                break;

            case AtvvControlEvent.Unknown:
                if (data.Length > 0)
                {
                    LogService.Write($"[{_logTag}] ATVV unknown ctl hex={BitConverter.ToString(data)}");
                }
                break;

            case AtvvControlEvent.AudioSync sync:
                _protocol.ApplyAudioSync(sync.Codec, sync.Sequence, sync.Predictor, sync.StepIndex);
                if (_isStreaming) _streamSyncCount++; else _pendingSyncCount++;
                break;

            case AtvvControlEvent.MicOpenError err:
                LogService.Write($"[{_logTag}] MIC_OPEN error: 0x{err.Code:x4}");
                MicrophoneOpenFailed?.Invoke(err.Code);
                break;
        }
    }

    private void HandleAudio(byte[] data)
    {
        if (!_isStreaming) return;
        var frame = _protocol.DecodeAudio(data);
        if (frame is null || frame.Samples.Length == 0) return;

        _streamFrameCount++;
        if (_streamFrameCount == 1)
        {
            FirstAudioFrame?.Invoke();
        }

        int framePeak = 0;
        long frameSq = 0;
        foreach (short s in frame.Samples)
        {
            int abs = Math.Abs((int)s);
            if (abs > framePeak) framePeak = abs;
            if (abs >= 32_760) _streamClippedCount++;
            frameSq += (long)s * s;
        }
        _streamSampleCount += frame.Samples.Length;
        _streamPeak = Math.Max(_streamPeak, framePeak);
        _streamSumSq += frameSq;

        if (_wavRecorder is { } w)
        {
            w.AppendSamples(frame.Samples);
            w.AppendRawBytes(data);
        }

        int sampleRate = _protocol.Codec?.SampleRate() ?? 16_000;
        PcmDecoded?.Invoke(frame.Samples, sampleRate);

        _levelSumSq += frameSq;
        _levelCount += frame.Samples.Length;
        _levelPeak = Math.Max(_levelPeak, framePeak);
        var now = DateTimeOffset.UtcNow;
        if (now - _levelLogAt >= TimeSpan.FromMilliseconds(100))
        {
            double rms = _levelCount > 0 ? Math.Sqrt((double)_levelSumSq / _levelCount) : 0;
            double db = rms > 0 ? 20.0 * Math.Log10(rms / 32768.0) : -120.0;
            LevelUpdated?.Invoke(db, _levelPeak);
            _levelSumSq = 0;
            _levelCount = 0;
            _levelPeak = 0;
            _levelLogAt = now;
        }
    }

    private void ResetStaleSessionIfNeeded()
    {
        if (!_resetSessionOnConnect || _hasResetSessionForConnection) return;
        _hasResetSessionForConnection = true;
        try
        {
            _ = WriteCommandAsync(_protocol.MicCloseCommand(0));
            LogService.Write($"[{_logTag}] TX micClose reset");
        }
        catch { }
    }

    private void FinishAudioStreamLocally(byte reason)
    {
        CancelCloseTimeout();
        bool wasStreaming = _isStreaming;
        _isStreaming = false;
        _protocol.EndAudioStream();
        StopKeepAliveTimer();

        if (wasStreaming)
        {
            int sr = _protocol.Codec?.SampleRate() ?? 16_000;
            StreamingChanged?.Invoke(false, sr);
            AudioStopped?.Invoke(reason);
        }

        _levelSumSq = 0;
        _levelCount = 0;
        _levelPeak = 0;
        LevelUpdated?.Invoke(-120, 0);

        _wavRecorder?.Dispose();
        _wavRecorder = null;
        if (wasStreaming)
        {
            LogService.Write($"[{_logTag}] AUDIO_STOP reason=0x{reason:x2} frames={_streamFrameCount} samples={_streamSampleCount} syncs={_streamSyncCount}");
        }
        else
        {
            LogService.Write($"[{_logTag}] AUDIO_STOP (idle) reason=0x{reason:x2}");
        }
        _streamFrameCount = 0;
        _streamSampleCount = 0;
        _streamPeak = 0;
        _streamSumSq = 0;
        _streamClippedCount = 0;
        _streamSyncCount = 0;
    }

    private async Task WriteCommandAsync(byte[] data)
    {
        await _gattLock.WaitAsync();
        try
        {
            if (_commandChar is not null)
            {
                await WriteCommandCoreAsync(_commandChar, data);
            }
        }
        finally
        {
            _gattLock.Release();
        }
    }

    private static async Task WriteCommandCoreAsync(GattCharacteristic ch, byte[] data)
    {
        var writer = new DataWriter();
        writer.WriteBytes(data);
        var option = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;
        _ = await ch.WriteValueAsync(writer.DetachBuffer(), option);
    }

    private void StartKeepAliveTimer()
    {
        StopKeepAliveTimer();
        _keepAliveTimer = new Timer(_ =>
        {
            if (!_isStreaming) return;
            try { _ = WriteCommandAsync(_protocol.KeepAliveCommand(_streamId)); }
            catch { }
        }, null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
    }

    private void StopKeepAliveTimer()
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
    }

    private void ScheduleCloseTimeout()
    {
        CancelCloseTimeout();
        _closeTimeoutTimer = new Timer(_ =>
        {
            if (_isStreaming)
            {
                LogService.Write($"[{_logTag}] AUDIO_STOP timeout; forcing local cleanup");
                FinishAudioStreamLocally(0xFE);
            }
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    private void CancelCloseTimeout()
    {
        _closeTimeoutTimer?.Dispose();
        _closeTimeoutTimer = null;
    }
}

