using System.Diagnostics;
using System.IO;
using System.Text;

namespace vRemoter.Windows.SystemSupport;

public static class LogService
{
    private const long MaximumBytes = 5 * 1024 * 1024;
    private static readonly object Sync = new();
    private static StreamWriter? _writer;

    public static bool IsEnabled => AppStorage.Shared.LoggingEnabled;

    public static void SetEnabled(bool enabled)
    {
        lock (Sync)
        {
            AppStorage.Shared.LoggingEnabled = enabled;
            if (enabled)
            {
                OpenWriterIfNeededLocked();
            }
            else
            {
                CloseWriterLocked();
            }
        }
    }

    public static void Write(string message)
    {
        Debug.WriteLine(message);
        if (!AppStorage.Shared.LoggingEnabled) return;

        lock (Sync)
        {
            try
            {
                RotateIfNeededLocked();
                OpenWriterIfNeededLocked();
                _writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
                _writer?.Flush();
            }
            catch
            {
            }
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            CloseWriterLocked();
            try
            {
                if (File.Exists(AppStorage.Shared.LogFilePath))
                {
                    File.Delete(AppStorage.Shared.LogFilePath);
                }
            }
            catch
            {
            }
            if (AppStorage.Shared.LoggingEnabled)
            {
                OpenWriterIfNeededLocked();
            }
        }
    }

    public static long ByteSize
    {
        get
        {
            try
            {
                var info = new FileInfo(AppStorage.Shared.LogFilePath);
                return info.Exists ? info.Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    private static void OpenWriterIfNeededLocked()
    {
        if (_writer is not null) return;
        AppStorage.EnsureDirectory(AppStorage.Shared.LogsDirectory);
        var stream = new FileStream(
            AppStorage.Shared.LogFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void RotateIfNeededLocked()
    {
        if (ByteSize < MaximumBytes) return;
        CloseWriterLocked();
        try
        {
            File.WriteAllText(AppStorage.Shared.LogFilePath, string.Empty);
        }
        catch
        {
        }
    }

    private static void CloseWriterLocked()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
        }
        _writer = null;
    }
}

