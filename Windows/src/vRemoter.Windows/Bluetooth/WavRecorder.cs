using System.Globalization;
using System.IO;
using System.Text;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Bluetooth;

public sealed class WavRecorder : IDisposable
{
    private const uint SampleRate = 16_000;
    private const ushort Channels = 1;
    private const ushort BitsPerSample = 16;

    private readonly object _sync = new();
    private FileStream? _wavStream;
    private FileStream? _rawStream;
    private ulong _totalFrames;
    private bool _closed;

    public string Basename { get; }
    public string Filename => $"{Basename}.wav";

    public static WavRecorder? CreateNext(string prefix)
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string baseName = $"{prefix}-{stamp}";
            string dir = AppStorage.Shared.RecordingsDirectory;
            AppStorage.EnsureDirectory(dir);
            string wavPath = Path.Combine(dir, $"{baseName}.wav");
            string rawPath = Path.Combine(dir, $"{baseName}.raw.bin");
            return new WavRecorder(wavPath, rawPath, baseName);
        }
        catch (Exception ex)
        {
            LogService.Write($"[WAV] create error: {ex.Message}");
            return null;
        }
    }

    private WavRecorder(string wavPath, string rawPath, string basename)
    {
        Basename = basename;
        _wavStream = new FileStream(wavPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _rawStream = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        byte[] header = BuildHeader(0);
        _wavStream.Write(header, 0, header.Length);
    }

    public void AppendSamples(short[] samples)
    {
        if (samples.Length == 0) return;
        lock (_sync)
        {
            if (_closed || _wavStream is null) return;
            byte[] bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            _wavStream.Write(bytes, 0, bytes.Length);
            _totalFrames += (ulong)samples.Length;
        }
    }

    public void AppendRawBytes(byte[] raw)
    {
        if (raw.Length == 0) return;
        lock (_sync)
        {
            if (_closed || _rawStream is null) return;
            _rawStream.Write(raw, 0, raw.Length);
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            try
            {
                if (_wavStream is not null)
                {
                    uint dataBytes = (uint)Math.Min(uint.MaxValue, _totalFrames * Channels * (BitsPerSample / 8));
                    byte[] header = BuildHeader(dataBytes);
                    _wavStream.Seek(0, SeekOrigin.Begin);
                    _wavStream.Write(header, 0, header.Length);
                    _wavStream.Dispose();
                    _wavStream = null;
                }
            }
            catch (Exception ex)
            {
                LogService.Write($"[WAV] close error: {ex.Message}");
            }
            try
            {
                _rawStream?.Dispose();
                _rawStream = null;
            }
            catch
            {
            }
        }
    }

    public void Dispose() => Close();

    private static byte[] BuildHeader(uint dataBytes)
    {
        uint byteRate = SampleRate * Channels * (BitsPerSample / 8);
        ushort blockAlign = (ushort)(Channels * (BitsPerSample / 8));
        uint chunkSize = 36 + dataBytes;

        using var ms = new MemoryStream(44);
        using var bw = new BinaryWriter(ms, Encoding.ASCII);
        bw.Write("RIFF"u8);
        bw.Write(chunkSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16u);
        bw.Write((ushort)1);
        bw.Write(Channels);
        bw.Write(SampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(BitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataBytes);
        return ms.ToArray();
    }
}

