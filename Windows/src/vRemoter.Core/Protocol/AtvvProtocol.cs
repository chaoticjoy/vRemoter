namespace vRemoter.Core.Protocol;

/// <summary>
/// Google Android TV Voice Service (ATVV v0.4 / v1.0) protocol handler.
/// </summary>
public sealed class AtvvProtocol
{
    public static readonly Guid ServiceUuid = new("AB5E0001-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid TxUuid = new("AB5E0002-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid RxUuid = new("AB5E0003-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid ControlUuid = new("AB5E0004-5A21-4F05-BC7D-AF01F617B664");

    public static readonly byte[] GetCapabilitiesCommand = [0x0A, 0x01, 0x00, 0x00, 0x03, 0x03];

    private readonly ImaAdpcmDecoder _decoder = new();
    private ushort _v10Sequence;
    private bool _audioStreamActive;
    private bool _hasPendingSyncBeforeStart;

    public AtvvCapabilities? Capabilities { get; private set; }
    public AtvvCodec? Codec { get; private set; }

    public void AcceptCapabilities(AtvvCapabilities capabilities)
    {
        var selected = capabilities.SelectedCodec
            ?? throw new AtvvProtocolException("遥控器不支持 ADPCM 8/16 kHz");

        Capabilities = capabilities;
        Codec = selected;
        _decoder.Reset(0, 0);
        _v10Sequence = 0;
        _audioStreamActive = false;
        _hasPendingSyncBeforeStart = false;
    }

    public byte[] MicOpenCommand()
    {
        if (Capabilities is null || Codec is null)
        {
            throw new AtvvProtocolException("尚未完成 ATVV capabilities 协商");
        }

        return Capabilities.Version switch
        {
            AtvvVersion.V04 => [0x0C, 0x00, (byte)Codec.Value],
            AtvvVersion.V10 => [0x0C, 0x00],
            _ => throw new AtvvProtocolException("Unsupported ATVV version")
        };
    }

    public byte[] MicCloseCommand(byte streamId)
    {
        if (Capabilities is null)
        {
            throw new AtvvProtocolException("尚未完成 ATVV capabilities 协商");
        }

        return Capabilities.Version == AtvvVersion.V04
            ? [0x0D]
            : [0x0D, streamId];
    }

    public byte[] KeepAliveCommand(byte streamId)
    {
        if (Capabilities is null)
        {
            throw new AtvvProtocolException("尚未完成 ATVV capabilities 协商");
        }

        return Capabilities.Version == AtvvVersion.V04
            ? MicOpenCommand()
            : [0x0E, streamId];
    }

    public AtvvControlEvent ParseControl(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return new AtvvControlEvent.Unknown([]);
        }

        byte opcode = data[0];
        switch (opcode)
        {
            case 0x00:
                return new AtvvControlEvent.AudioStop(data.Length > 1 ? data[1] : (byte)0);

            case 0x04:
                if (Capabilities?.Version == AtvvVersion.V10 &&
                    data.Length >= 4 &&
                    TryParseCodec(data[2], out var startCodec))
                {
                    return new AtvvControlEvent.AudioStart(data[1], startCodec, data[3]);
                }
                return new AtvvControlEvent.AudioStart(0, Codec ?? AtvvCodec.Adpcm8k, 0);

            case 0x08:
                return new AtvvControlEvent.StartSearch();

            case 0x0A:
                if (Capabilities?.Version == AtvvVersion.V10 &&
                    data.Length >= 7 &&
                    TryParseCodec(data[1], out var syncCodec))
                {
                    ushort sequence = (ushort)((data[2] << 8) | data[3]);
                    short predictor = unchecked((short)((data[4] << 8) | data[5]));
                    return new AtvvControlEvent.AudioSync(syncCodec, sequence, predictor, data[6]);
                }
                return new AtvvControlEvent.Unknown(data.ToArray());

            case 0x0B:
                var caps = ParseCapabilities(data);
                return caps is not null
                    ? new AtvvControlEvent.Capabilities(caps)
                    : new AtvvControlEvent.Unknown(data.ToArray());

            case 0x0C:
                if (data.Length < 3)
                {
                    return new AtvvControlEvent.MicOpenError(0xFFFF);
                }
                ushort code = (ushort)((data[1] << 8) | data[2]);
                return new AtvvControlEvent.MicOpenError(code);

            default:
                return new AtvvControlEvent.Unknown(data.ToArray());
        }
    }

    public static AtvvCapabilities? ParseCapabilities(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || data[0] != 0x0B)
        {
            return null;
        }

        ushort version = (ushort)((data[1] << 8) | data[2]);
        return version switch
        {
            0x0004 when data.Length >= 9 => new AtvvCapabilities(
                AtvvVersion.V04,
                data[4],
                0,
                (data[5] << 8) | data[6]),
            0x0100 when data.Length >= 7 => new AtvvCapabilities(
                AtvvVersion.V10,
                data[3],
                data[4],
                (data[5] << 8) | data[6]),
            _ => null
        };
    }

    public void ApplyAudioSync(AtvvCodec codec, ushort sequence, short predictor, byte stepIndex)
    {
        Codec = codec;
        _v10Sequence = sequence;
        _decoder.Reset(predictor, stepIndex);
        // Sync received before AUDIO_START belongs to the upcoming stream;
        // sync received mid-stream must not leak into the next stream.
        _hasPendingSyncBeforeStart = !_audioStreamActive;
    }

    public void PrepareForAudioStream()
    {
        _v10Sequence = 0;
        _decoder.Reset(0, 0);
        _audioStreamActive = false;
        _hasPendingSyncBeforeStart = false;
    }

    public void BeginAudioStream(AtvvCodec codec)
    {
        Codec = codec;
        if (!_hasPendingSyncBeforeStart)
        {
            _v10Sequence = 0;
            _decoder.Reset(0, 0);
        }
        _audioStreamActive = true;
        _hasPendingSyncBeforeStart = false;
    }

    public void EndAudioStream()
    {
        _audioStreamActive = false;
        _hasPendingSyncBeforeStart = false;
    }

    public DecodedAudioFrame? DecodeAudio(ReadOnlySpan<byte> data)
    {
        if (Capabilities is null || Codec is null)
        {
            return null;
        }

        switch (Capabilities.Version)
        {
            case AtvvVersion.V04:
                if (data.Length != Capabilities.FrameSize || data.Length < 6)
                {
                    return null;
                }
                ushort sequenceV04 = (ushort)((data[0] << 8) | data[1]);
                short predictor = unchecked((short)((data[3] << 8) | data[4]));
                _decoder.Reset(predictor, data[5]);
                short[] body = _decoder.Decode(data[6..]);
                var combined = new short[body.Length + 1];
                combined[0] = predictor;
                Array.Copy(body, 0, combined, 1, body.Length);
                return new DecodedAudioFrame(sequenceV04, combined);

            case AtvvVersion.V10:
                if (data.IsEmpty)
                {
                    return null;
                }
                ushort sequenceV10 = _v10Sequence;
                _v10Sequence = unchecked((ushort)(_v10Sequence + 1));
                return new DecodedAudioFrame(sequenceV10, _decoder.Decode(data));

            default:
                return null;
        }
    }

    private static bool TryParseCodec(byte raw, out AtvvCodec codec)
    {
        if (raw == (byte)AtvvCodec.Adpcm8k)
        {
            codec = AtvvCodec.Adpcm8k;
            return true;
        }
        if (raw == (byte)AtvvCodec.Adpcm16k)
        {
            codec = AtvvCodec.Adpcm16k;
            return true;
        }
        codec = default;
        return false;
    }
}

