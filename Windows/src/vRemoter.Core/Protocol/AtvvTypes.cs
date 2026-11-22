namespace vRemoter.Core.Protocol;

public enum AtvvVersion
{
    V04,
    V10
}

public enum AtvvCodec : byte
{
    Adpcm8k = 0x01,
    Adpcm16k = 0x02
}

public static class AtvvCodecExtensions
{
    public static int SampleRate(this AtvvCodec codec) =>
        codec == AtvvCodec.Adpcm8k ? 8_000 : 16_000;

    public static string Describe(this AtvvCodec codec) =>
        codec == AtvvCodec.Adpcm8k ? "ADPCM 8 kHz" : "ADPCM 16 kHz";

    public static string Describe(this AtvvVersion version) =>
        version == AtvvVersion.V04 ? "0.4" : "1.0";
}

public sealed record AtvvCapabilities(
    AtvvVersion Version,
    byte Codecs,
    byte InteractionModel,
    int FrameSize)
{
    public AtvvCodec? SelectedCodec
    {
        get
        {
            if ((Codecs & (byte)AtvvCodec.Adpcm16k) != 0)
            {
                return AtvvCodec.Adpcm16k;
            }
            if ((Codecs & (byte)AtvvCodec.Adpcm8k) != 0)
            {
                return AtvvCodec.Adpcm8k;
            }
            return null;
        }
    }
}

public abstract record AtvvControlEvent
{
    public sealed record AudioStop(byte Reason) : AtvvControlEvent;
    public sealed record AudioStart(byte Reason, AtvvCodec Codec, byte StreamId) : AtvvControlEvent;
    public sealed record StartSearch : AtvvControlEvent;
    public sealed record AudioSync(AtvvCodec Codec, ushort Sequence, short Predictor, byte StepIndex) : AtvvControlEvent;
    public sealed record Capabilities(AtvvCapabilities Value) : AtvvControlEvent;
    public sealed record MicOpenError(ushort Code) : AtvvControlEvent;
    public sealed record Unknown(byte[] Raw) : AtvvControlEvent;
}

public sealed record DecodedAudioFrame(ushort Sequence, short[] Samples);

public sealed class AtvvProtocolException : Exception
{
    public AtvvProtocolException(string message) : base(message) { }
}

