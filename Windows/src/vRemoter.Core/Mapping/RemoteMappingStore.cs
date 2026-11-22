using System.Text.Json;
using vRemoter.Core.Focus;
using vRemoter.Core.Localization;

namespace vRemoter.Core.Mapping;

public interface IKeyValueSettingsStore
{
    string? GetString(string key);
    void SetString(string key, string? value);
    bool GetBool(string key, bool defaultValue = false);
    void SetBool(string key, bool value);
    void Remove(string key);
}

public sealed class RemoteMappingStore
{
    private const string Prefix = "remoteMapping.";
    private const string EnabledPrefix = "remoteMappingEnabled.";
    private const string CustomPrefix = "remoteCustomMapping.";
    private const string AppLaunchPrefix = "remoteAppLaunch.";

    private readonly IKeyValueSettingsStore _store;

    public event Action? Changed;

    public RemoteMappingStore(IKeyValueSettingsStore store)
    {
        _store = store;
    }

    public bool IsEnabled(SupportedRemoteId remote) =>
        _store.GetBool(EnabledPrefix + remote.StorageKey(), false);

    public void SetEnabled(bool enabled, SupportedRemoteId remote)
    {
        _store.SetBool(EnabledPrefix + remote.StorageKey(), enabled);
        Changed?.Invoke();
    }

    public RemoteMappingTarget GetTarget(RemoteButtonDefinition button, SupportedRemoteId remote)
    {
        if (button.VoiceControlled) return RemoteMappingTarget.DoubaoVoice;
        if (!button.Remappable) return RemoteMappingTarget.Disabled;

        string? raw = _store.GetString(Prefix + remote.StorageKey() + "." + button.Id);
        return RemoteMappingTargetExtensions.TryParseStorageValue(raw, out var target)
            ? target
            : button.DefaultTarget;
    }

    public void SetTarget(RemoteMappingTarget target, RemoteButtonDefinition button, SupportedRemoteId remote)
    {
        if (button.VoiceControlled || !button.Remappable) return;
        _store.SetString(Prefix + remote.StorageKey() + "." + button.Id, target.ToStorageValue());
        Changed?.Invoke();
    }

    public RemoteCustomShortcut? GetCustomShortcut(RemoteButtonDefinition button, SupportedRemoteId remote)
    {
        string? json = _store.GetString(CustomPrefix + remote.StorageKey() + "." + button.Id);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<RemoteCustomShortcut>(json);
        }
        catch
        {
            return null;
        }
    }

    public void SetCustomShortcut(RemoteCustomShortcut shortcut, RemoteButtonDefinition button, SupportedRemoteId remote)
    {
        string key = CustomPrefix + remote.StorageKey() + "." + button.Id;
        _store.SetString(key, JsonSerializer.Serialize(shortcut));
        SetTarget(RemoteMappingTarget.Custom, button, remote);
    }

    public RemoteAppLaunchConfig? GetAppLaunchConfig(RemoteButtonDefinition button, SupportedRemoteId remote)
    {
        string? json = _store.GetString(AppLaunchPrefix + remote.StorageKey() + "." + button.Id);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<RemoteAppLaunchConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    public void SetAppLaunchConfig(RemoteAppLaunchConfig config, RemoteButtonDefinition button, SupportedRemoteId remote)
    {
        string key = AppLaunchPrefix + remote.StorageKey() + "." + button.Id;
        _store.SetString(key, JsonSerializer.Serialize(config));
        SetTarget(RemoteMappingTarget.OpenApp, button, remote);
    }

    public string GetTargetTitle(
        RemoteButtonDefinition button,
        SupportedRemoteId remote,
        string voiceInputShortName = "豆包")
    {
        var target = GetTarget(button, remote);
        if (target == RemoteMappingTarget.Custom && GetCustomShortcut(button, remote) is { } shortcut)
        {
            return shortcut.Label;
        }
        if (target == RemoteMappingTarget.OpenApp && GetAppLaunchConfig(button, remote) is { } appConfig)
        {
            return L10n.Text($"打开 {appConfig.Name}", $"Open {appConfig.Name}");
        }
        return target.GetTitle(voiceInputShortName);
    }

    public void Reset(SupportedRemoteId remote)
    {
        foreach (var button in RemoteProfiles.GetButtons(remote))
        {
            _store.Remove(Prefix + remote.StorageKey() + "." + button.Id);
            _store.Remove(CustomPrefix + remote.StorageKey() + "." + button.Id);
            _store.Remove(AppLaunchPrefix + remote.StorageKey() + "." + button.Id);
        }
        Changed?.Invoke();
    }
}

