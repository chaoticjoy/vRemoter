using System.Text.Json;
using vRemoter.Core.Focus;
using vRemoter.Core.Localization;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Focus;

public sealed class InputFocusStore
{
    public sealed record Snapshot(
        bool AutoFocusOnSwitchEnabled,
        bool FocusBeforeVoiceEnabled,
        bool PullFromOtherEditorsOnSwitch,
        IReadOnlyDictionary<string, InputFocusAppEntry> Apps);

    private const string AutoFocusKey = "inputFocus.autoFocusOnSwitch";
    private const string FocusBeforeVoiceKey = "inputFocus.focusBeforeVoice";
    private const string PullOtherEditorsKey = "inputFocus.pullFromOtherEditors";
    private const string AppsKey = "inputFocus.apps";

    private readonly object _sync = new();
    private readonly List<InputFocusAppEntry> _apps = [];
    private readonly Dictionary<string, InputFocusResultStatus> _appStates = new(StringComparer.OrdinalIgnoreCase);

    private bool _autoFocusOnSwitch;
    private bool _focusBeforeVoice;
    private bool _pullFromOtherEditors;
    private Snapshot _cachedSnapshot;

    public InputFocusBindingState BindingState { get; private set; } = new();
    public string LastFeedback { get; private set; } = string.Empty;

    public event Action? Changed;

    public InputFocusStore()
    {
        _autoFocusOnSwitch = AppStorage.Shared.GetBool(AutoFocusKey, true);
        _focusBeforeVoice = AppStorage.Shared.GetBool(FocusBeforeVoiceKey, true);
        _pullFromOtherEditors = AppStorage.Shared.GetBool(PullOtherEditorsKey, true);
        LoadApps();
        _cachedSnapshot = BuildSnapshot();
    }

    public bool AutoFocusOnSwitchEnabled
    {
        get => _autoFocusOnSwitch;
        set
        {
            _autoFocusOnSwitch = value;
            AppStorage.Shared.SetBool(AutoFocusKey, value);
            SyncSnapshot();
        }
    }

    public bool FocusBeforeVoiceEnabled
    {
        get => _focusBeforeVoice;
        set
        {
            _focusBeforeVoice = value;
            AppStorage.Shared.SetBool(FocusBeforeVoiceKey, value);
            SyncSnapshot();
        }
    }

    public bool PullFromOtherEditorsOnSwitch
    {
        get => _pullFromOtherEditors;
        set
        {
            _pullFromOtherEditors = value;
            AppStorage.Shared.SetBool(PullOtherEditorsKey, value);
            SyncSnapshot();
        }
    }

    public IReadOnlyList<InputFocusAppEntry> Apps
    {
        get { lock (_sync) return _apps.ToList(); }
    }

    public Snapshot GetSnapshot()
    {
        lock (_sync) return _cachedSnapshot;
    }

    public InputFocusAppEntry? GetEntry(string appId)
    {
        lock (_sync)
        {
            return _apps.FirstOrDefault(a => a.AppIdentifier.Equals(appId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public InputFocusResultStatus? GetAppState(string appId)
    {
        lock (_sync)
        {
            return _appStates.TryGetValue(appId, out var status) ? status : null;
        }
    }

    public void AddOrEnableApp(string appIdentifier, string name, string? appPath = null)
    {
        lock (_sync)
        {
            var existing = _apps.FirstOrDefault(a => a.AppIdentifier.Equals(appIdentifier, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Enabled = true;
                existing.Name = name;
                if (!string.IsNullOrWhiteSpace(appPath)) existing.AppPath = appPath;
            }
            else
            {
                _apps.Add(new InputFocusAppEntry
                {
                    AppIdentifier = appIdentifier,
                    Name = name,
                    AppPath = appPath,
                    Enabled = true,
                    Mode = InputFocusMode.Auto
                });
                _apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            }
            SaveAppsLocked();
            _cachedSnapshot = BuildSnapshot();
        }
        Changed?.Invoke();
    }

    public void RemoveApp(string appIdentifier)
    {
        lock (_sync)
        {
            _apps.RemoveAll(a => a.AppIdentifier.Equals(appIdentifier, StringComparison.OrdinalIgnoreCase));
            _appStates.Remove(appIdentifier);
            SaveAppsLocked();
            _cachedSnapshot = BuildSnapshot();
        }
        Changed?.Invoke();
    }

    public void SetAppEnabled(bool enabled, string appIdentifier)
    {
        lock (_sync)
        {
            var entry = _apps.FirstOrDefault(a => a.AppIdentifier.Equals(appIdentifier, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;
            entry.Enabled = enabled;
            SaveAppsLocked();
            _cachedSnapshot = BuildSnapshot();
        }
        Changed?.Invoke();
    }

    public void SaveLocator(InputFocusLocator? locator, string appIdentifier)
    {
        lock (_sync)
        {
            var entry = _apps.FirstOrDefault(a => a.AppIdentifier.Equals(appIdentifier, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;
            entry.Mode = InputFocusMode.Manual;
            entry.Locator = locator;
            _appStates[appIdentifier] = InputFocusResultStatus.Waiting;
            SaveAppsLocked();
            _cachedSnapshot = BuildSnapshot();
        }
        Changed?.Invoke();
    }

    public void ResetToAuto(string appIdentifier)
    {
        lock (_sync)
        {
            var entry = _apps.FirstOrDefault(a => a.AppIdentifier.Equals(appIdentifier, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;
            entry.Mode = InputFocusMode.Auto;
            entry.Locator = null;
            _appStates[appIdentifier] = InputFocusResultStatus.Waiting;
            SaveAppsLocked();
            _cachedSnapshot = BuildSnapshot();
        }
        Changed?.Invoke();
    }

    public void UpdateState(InputFocusResultStatus status, string appIdentifier)
    {
        lock (_sync)
        {
            _appStates[appIdentifier] = status;
        }
        Changed?.Invoke();
    }

    public void SetBindingState(InputFocusBindingState state)
    {
        BindingState = state;
        Changed?.Invoke();
    }

    public void SetFeedback(string message)
    {
        LastFeedback = message;
        Changed?.Invoke();
    }

    private void SyncSnapshot()
    {
        lock (_sync)
        {
            _cachedSnapshot = BuildSnapshot();
        }
        Changed?.Invoke();
    }

    private Snapshot BuildSnapshot()
    {
        var map = _apps.ToDictionary(a => a.AppIdentifier, StringComparer.OrdinalIgnoreCase);
        return new Snapshot(_autoFocusOnSwitch, _focusBeforeVoice, _pullFromOtherEditors, map);
    }

    private void LoadApps()
    {
        string? json = AppStorage.Shared.GetString(AppsKey);
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var decoded = JsonSerializer.Deserialize<List<InputFocusAppEntry>>(json);
            if (decoded is not null) _apps.AddRange(decoded);
        }
        catch
        {
        }
    }

    private void SaveAppsLocked()
    {
        string json = JsonSerializer.Serialize(_apps);
        AppStorage.Shared.SetString(AppsKey, json);
    }
}

