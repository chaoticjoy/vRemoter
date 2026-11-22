using System.IO;
using System.Text.Json;
using vRemoter.Core.Localization;
using vRemoter.Core.Mapping;
using vRemoter.Core.Session;

namespace vRemoter.Windows.SystemSupport;

public sealed class AppStorage : IKeyValueSettingsStore
{
    public const string LoggingEnabledKey = "loggingEnabled";
    public const string RecordingEnabledKey = "recordingEnabled";
    public const string MacInputEnabledKey = "macInputEnabled";
    public const string RemoteInputEnabledKey = "remoteInputEnabled";
    public const string InputTriggerKeyKey = "inputTriggerKey";
    public const string InputTriggerModeKey = "inputTriggerMode";
    public const string VoiceInputMethodKey = "voiceInputMethod";
    public const string AppLanguageKey = "vRemoter.appLanguage";
    public const string AutomaticUpdatesKey = "vRemoter.automaticUpdates";
    public const string CompletedMicTestKey = "vRemoter.hasCompletedX6MicTest";
    public const string ElevatedLaunchKey = "vRemoter.elevatedLaunchAtLogin";
    public const string VoiceCompatibilityModeKey = "voiceCompatibilityMode";
    public const string PreferredVirtualDeviceKey = "vRemoter.preferredVirtualAudio";
    public const string UseNativeAltTabKey = "vRemoter.useNativeAltTab";

    public static AppStorage Shared { get; } = new();

    public string BaseDirectory { get; }
    public string LogsDirectory { get; }
    public string LogFilePath { get; }
    public string RecordingsDirectory { get; }
    public string BleCacheDirectory { get; }
    private string SettingsFilePath { get; }

    private readonly object _sync = new();
    private Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    private AppStorage()
    {
        BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "vRemoter");
        LogsDirectory = Path.Combine(BaseDirectory, "Logs");
        LogFilePath = Path.Combine(LogsDirectory, "vRemoter.log");
        RecordingsDirectory = Path.Combine(BaseDirectory, "Recordings");
        BleCacheDirectory = Path.Combine(BaseDirectory, "BleCache");
        SettingsFilePath = Path.Combine(BaseDirectory, "settings.json");
        Load();
    }

    public void Prepare()
    {
        EnsureDirectory(BaseDirectory);
        EnsureDirectory(LogsDirectory);
        EnsureDirectory(RecordingsDirectory);
        EnsureDirectory(BleCacheDirectory);
        L10n.Configure(() => AppLanguage);
    }

    public bool LoggingEnabled
    {
        get => GetBool(LoggingEnabledKey, true);
        set => SetBool(LoggingEnabledKey, value);
    }

    public bool RecordingEnabled
    {
        get => GetBool(RecordingEnabledKey, false);
        set => SetBool(RecordingEnabledKey, value);
    }

    public bool PcInputEnabled
    {
        get => GetBool(MacInputEnabledKey, true);
        set => SetBool(MacInputEnabledKey, value);
    }

    public bool RemoteInputEnabled
    {
        get => GetBool(RemoteInputEnabledKey, true);
        set => SetBool(RemoteInputEnabledKey, value);
    }

    public InputTriggerMode InputTriggerMode
    {
        get => InputTriggerModeExtensions.ParseStorageValue(GetString(InputTriggerModeKey));
        set => SetString(InputTriggerModeKey, value.ToStorageValue());
    }

    public VoiceInputMethodKind VoiceInputMethod
    {
        get => string.Equals(GetString(VoiceInputMethodKey), "wetype", StringComparison.OrdinalIgnoreCase)
            ? VoiceInputMethodKind.WeType
            : VoiceInputMethodKind.Doubao;
        set => SetString(VoiceInputMethodKey, value == VoiceInputMethodKind.WeType ? "wetype" : "doubao");
    }

    public AppLanguage AppLanguage
    {
        get => AppLanguageExtensions.ParseStorageValue(GetString(AppLanguageKey));
        set => SetString(AppLanguageKey, value.ToStorageValue());
    }

    public bool AutomaticUpdates
    {
        get => GetBool(AutomaticUpdatesKey, true);
        set => SetBool(AutomaticUpdatesKey, value);
    }

    public bool ElevatedLaunchAtLogin
    {
        get => GetBool(ElevatedLaunchKey, false);
        set => SetBool(ElevatedLaunchKey, value);
    }

    public bool? VoiceCompatibilityOverride
    {
        get
        {
            string? raw = GetString(VoiceCompatibilityModeKey);
            return bool.TryParse(raw, out bool b) ? b : null;
        }
    }

    public string? PreferredVirtualAudioDevice
    {
        get => GetString(PreferredVirtualDeviceKey);
        set => SetString(PreferredVirtualDeviceKey, value);
    }

    public bool UseNativeAltTab
    {
        get => GetBool(UseNativeAltTabKey, false);
        set => SetBool(UseNativeAltTabKey, value);
    }

    public string? GetString(string key)
    {
        lock (_sync)
        {
            return _values.TryGetValue(key, out var val) ? val : null;
        }
    }

    public void SetString(string key, string? value)
    {
        lock (_sync)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }
            SaveLocked();
        }
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        string? raw = GetString(key);
        return bool.TryParse(raw, out bool parsed) ? parsed : defaultValue;
    }

    public void SetBool(string key, bool value) =>
        SetString(key, value ? "true" : "false");

    public void Remove(string key) => SetString(key, null);

    public static void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public static long DirectoryByteSize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
    }

    public static void ClearFiles(string directory)
    {
        EnsureDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private void Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;
                string json = File.ReadAllText(SettingsFilePath);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
                if (parsed is not null)
                {
                    _values = new Dictionary<string, string?>(parsed, StringComparer.Ordinal);
                }
            }
            catch
            {
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            EnsureDirectory(BaseDirectory);
            string json = JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
            string tmp = SettingsFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsFilePath, overwrite: true);
        }
        catch
        {
        }
    }
}

