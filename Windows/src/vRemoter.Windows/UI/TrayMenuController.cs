using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using vRemoter.Core.Localization;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.UI;

/// <summary>
/// Manages the Windows system tray NotifyIcon and context menu across Win10 & Win11.
/// </summary>
public sealed class TrayMenuController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _launchAtLoginItem;
    private readonly ToolStripMenuItem _elevatedLaunchItem;
    private readonly ToolStripMenuItem _loggingToggleItem;
    private readonly ToolStripMenuItem _logSizeItem;
    private readonly ToolStripMenuItem _recordingToggleItem;
    private readonly ToolStripMenuItem _recordingSizeItem;
    private readonly Dictionary<AppLanguage, ToolStripMenuItem> _languageItems = [];

    public event Action? OpenConsoleRequested;
    public event Action? StopMicrophoneRequested;
    public event Action? RestartRequested;
    public event Action? OpenUpdatesRequested;
    public event Action<bool>? RecordingToggled;
    public event Action? ClearRecordingsRequested;
    public event Action? QuitRequested;

    public TrayMenuController()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RefreshState();

        _statusItem = new ToolStripMenuItem(L10n.Text("状态 · 启动中", "Status · Starting")) { Enabled = false };
        menu.Items.Add(_statusItem);

        _launchAtLoginItem = new ToolStripMenuItem(L10n.Text("登录时自动启动", "Launch at login"));
        _launchAtLoginItem.Click += (_, _) => ToggleLaunchAtLogin(elevated: AppStorage.Shared.ElevatedLaunchAtLogin);
        menu.Items.Add(_launchAtLoginItem);

        _elevatedLaunchItem = new ToolStripMenuItem(L10n.Text("增强兼容模式（管理员自启）", "Enhanced Mode (Elevated Startup)"));
        _elevatedLaunchItem.Click += (_, _) => ToggleLaunchAtLogin(elevated: !AppStorage.Shared.ElevatedLaunchAtLogin);
        menu.Items.Add(_elevatedLaunchItem);

        menu.Items.Add(new ToolStripSeparator());

        var consoleMenu = new ToolStripMenuItem(L10n.Text("控制台", "Console"));
        consoleMenu.DropDownItems.Add(L10n.Text("打开前台控制台", "Open Console"), null, (_, _) => OpenConsoleRequested?.Invoke());
        consoleMenu.DropDownItems.Add(L10n.Text("关闭麦克风", "Stop Microphone"), null, (_, _) => StopMicrophoneRequested?.Invoke());
        consoleMenu.DropDownItems.Add(L10n.Text("重启 App", "Restart App"), null, (_, _) => RestartRequested?.Invoke());
        menu.Items.Add(consoleMenu);

        var logMenu = new ToolStripMenuItem(L10n.Text("日志", "Logs"));
        _loggingToggleItem = new ToolStripMenuItem(L10n.Text("记录日志", "Record Logs"));
        _loggingToggleItem.Click += (_, _) =>
        {
            LogService.SetEnabled(!LogService.IsEnabled);
            RefreshState();
        };
        _logSizeItem = new ToolStripMenuItem(L10n.Text("占用 · 0 B", "Size · 0 B")) { Enabled = false };
        logMenu.DropDownItems.Add(_loggingToggleItem);
        logMenu.DropDownItems.Add(_logSizeItem);
        logMenu.DropDownItems.Add(L10n.Text("打开日志文件夹", "Open Logs Folder"), null, (_, _) => OpenFolder(AppStorage.Shared.LogsDirectory));
        logMenu.DropDownItems.Add(L10n.Text("清空日志", "Clear Logs"), null, (_, _) => { LogService.Clear(); RefreshState(); });
        menu.Items.Add(logMenu);

        var recMenu = new ToolStripMenuItem(L10n.Text("调试录音", "Debug Recordings"));
        _recordingToggleItem = new ToolStripMenuItem(L10n.Text("保存 WAV 与原始数据", "Save WAV and Raw Data"));
        _recordingToggleItem.Click += (_, _) =>
        {
            bool next = !AppStorage.Shared.RecordingEnabled;
            AppStorage.Shared.RecordingEnabled = next;
            RecordingToggled?.Invoke(next);
            RefreshState();
        };
        _recordingSizeItem = new ToolStripMenuItem(L10n.Text("占用 · 0 B", "Size · 0 B")) { Enabled = false };
        recMenu.DropDownItems.Add(_recordingToggleItem);
        recMenu.DropDownItems.Add(_recordingSizeItem);
        recMenu.DropDownItems.Add(L10n.Text("打开录音文件夹", "Open Recordings Folder"), null, (_, _) => OpenFolder(AppStorage.Shared.RecordingsDirectory));
        recMenu.DropDownItems.Add(L10n.Text("清空录音文件", "Clear Recordings"), null, (_, _) => { ClearRecordingsRequested?.Invoke(); RefreshState(); });
        menu.Items.Add(recMenu);

        var langMenu = new ToolStripMenuItem(L10n.Text("语言", "Language"));
        AddLanguageItem(langMenu, AppLanguage.System, L10n.Text("跟随系统", "System Default"));
        AddLanguageItem(langMenu, AppLanguage.SimplifiedChinese, "简体中文");
        AddLanguageItem(langMenu, AppLanguage.English, "English");
        menu.Items.Add(langMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L10n.Text("版本与更新…", "Version & Updates…"), null, (_, _) => OpenUpdatesRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L10n.Text("退出", "Quit"), null, (_, _) => QuitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "vRemoter",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenConsoleRequested?.Invoke();
        RefreshState();
    }

    public void UpdateStatus(string statusText)
    {
        _statusItem.Text = L10n.Text($"状态 · {statusText}", $"Status · {statusText}");
        string tip = $"vRemoter · {statusText}";
        _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void RefreshState()
    {
        _launchAtLoginItem.Checked = LaunchAtLogin.IsEnabled;
        _elevatedLaunchItem.Checked = AppStorage.Shared.ElevatedLaunchAtLogin;
        _loggingToggleItem.Checked = LogService.IsEnabled;
        _recordingToggleItem.Checked = AppStorage.Shared.RecordingEnabled;
        _logSizeItem.Text = L10n.Text("占用", "Size") + $" · {AppStorage.FormatSize(LogService.ByteSize)}";
        long recBytes = AppStorage.DirectoryByteSize(AppStorage.Shared.RecordingsDirectory);
        _recordingSizeItem.Text = L10n.Text("占用", "Size") + $" · {AppStorage.FormatSize(recBytes)}";

        var currentLang = AppStorage.Shared.AppLanguage;
        foreach (var (lang, item) in _languageItems)
        {
            item.Checked = lang == currentLang;
        }
    }

    private void AddLanguageItem(ToolStripMenuItem parent, AppLanguage language, string title)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += (_, _) =>
        {
            if (AppStorage.Shared.AppLanguage == language) return;
            AppStorage.Shared.AppLanguage = language;
            RestartRequested?.Invoke();
        };
        parent.DropDownItems.Add(item);
        _languageItems[language] = item;
    }

    private void ToggleLaunchAtLogin(bool elevated)
    {
        try
        {
            bool nextEnabled = elevated ? true : !LaunchAtLogin.IsEnabled;
            LaunchAtLogin.SetEnabled(nextEnabled, elevated);
        }
        catch (Exception ex)
        {
            LogService.Write($"[STARTUP] toggle failed: {ex.Message}");
            _statusItem.Text = L10n.Text("状态 · 登录启动设置失败", "Status · Launch-at-login failed");
        }
        RefreshState();
    }

    private static void OpenFolder(string path)
    {
        AppStorage.EnsureDirectory(path);
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/vRemoter.ico"));
            if (streamInfo is not null)
            {
                return new Icon(streamInfo.Stream);
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}

