using vRemoter.Core.Protocol;
using Xunit;

namespace vRemoter.Core.Tests;

public sealed class AtvvProtocolTests
{
    [Fact]
    public void RepeatedAudioStartResetsUnsynchronizedStreams()
    {
        var protocol = new AtvvProtocol();
        protocol.AcceptCapabilities(new AtvvCapabilities(
            AtvvVersion.V10,
            (byte)AtvvCodec.Adpcm16k,
            0,
            120));

        byte[] packet = [0x12, 0x34, 0xAB, 0xCD, 0x56, 0x78];
        protocol.BeginAudioStream(AtvvCodec.Adpcm16k);
        var first = protocol.DecodeAudio(packet);
        Assert.NotNull(first);

        _ = protocol.DecodeAudio(Enumerable.Repeat((byte)0x77, 24).ToArray());
        protocol.EndAudioStream();
        protocol.BeginAudioStream(AtvvCodec.Adpcm16k);
        var second = protocol.DecodeAudio(packet);

        Assert.NotNull(second);
        Assert.Equal(first!.Samples, second!.Samples);
        Assert.Equal((ushort)0, first.Sequence);
        Assert.Equal((ushort)0, second.Sequence);
    }

    [Fact]
    public void AudioStartPreservesFreshAudioSync()
    {
        var protocol = new AtvvProtocol();
        protocol.AcceptCapabilities(new AtvvCapabilities(
            AtvvVersion.V10,
            (byte)AtvvCodec.Adpcm16k,
            0,
            120));

        protocol.BeginAudioStream(AtvvCodec.Adpcm16k);
        var baseline = protocol.DecodeAudio([0x12, 0x34]);
        protocol.EndAudioStream();

        protocol.PrepareForAudioStream();
        protocol.ApplyAudioSync(AtvvCodec.Adpcm16k, sequence: 42, predictor: 1_234, stepIndex: 20);
        protocol.BeginAudioStream(AtvvCodec.Adpcm16k);
        var synced = protocol.DecodeAudio([0x12, 0x34]);

        Assert.NotNull(synced);
        Assert.Equal((ushort)42, synced!.Sequence);
        Assert.NotEqual(baseline!.Samples[0], synced.Samples[0]);
    }

    [Fact]
    public void ActiveStreamAudioSyncDoesNotLeakIntoNextStream()
    {
        var protocol = new AtvvProtocol();
        protocol.AcceptCapabilities(new AtvvCapabilities(
            AtvvVersion.V10,
            (byte)AtvvCodec.Adpcm16k,
            0,
            120));

        byte[] packet = [0x12, 0x34, 0xAB, 0xCD, 0x56, 0x78];
        protocol.BeginAudioStream(AtvvCodec.Adpcm16k);
        var first = protocol.DecodeAudio(packet);

        protocol.ApplyAudioSync(AtvvCodec.Adpcm16k, sequence: 70, predictor: -2_000, stepIndex: 30);
        _ = protocol.DecodeAudio([0x77, 0x77]);
        protocol.EndAudioStream();

        protocol.BeginAudioStream(AtvvCodec.Adpcm16k);
        var afterActiveSync = protocol.DecodeAudio(packet);

        Assert.NotNull(afterActiveSync);
        Assert.Equal(first!.Samples, afterActiveSync!.Samples);
        Assert.Equal((ushort)0, afterActiveSync.Sequence);
    }

    [Fact]
    public void ParseControlHandlesV04AndV10Events()
    {
        var protocol = new AtvvProtocol();
        byte[] capsPayload = [0x0B, 0x01, 0x00, 0x02, 0x01, 0x00, 0x80];
        var capsEvent = Assert.IsType<AtvvControlEvent.Capabilities>(protocol.ParseControl(capsPayload));
        Assert.Equal(AtvvVersion.V10, capsEvent.Value.Version);
        Assert.Equal(128, capsEvent.Value.FrameSize);

        protocol.AcceptCapabilities(capsEvent.Value);
        Assert.Equal([0x0C, 0x00], protocol.MicOpenCommand());
        Assert.Equal([0x0D, 0x05], protocol.MicCloseCommand(5));
        Assert.Equal([0x0E, 0x05], protocol.KeepAliveCommand(5));

        var start = Assert.IsType<AtvvControlEvent.AudioStart>(
            protocol.ParseControl([0x04, 0x03, 0x02, 0x01]));
        Assert.Equal((byte)0x03, start.Reason);
        Assert.Equal(AtvvCodec.Adpcm16k, start.Codec);
        Assert.Equal((byte)0x01, start.StreamId);

        var stop = Assert.IsType<AtvvControlEvent.AudioStop>(protocol.ParseControl([0x00, 0x02]));
        Assert.Equal((byte)0x02, stop.Reason);

        var err = Assert.IsType<AtvvControlEvent.MicOpenError>(protocol.ParseControl([0x0C, 0x0F, 0x01]));
        Assert.Equal((ushort)0x0F01, err.Code);
    }
}

