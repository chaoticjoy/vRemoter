using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using vRemoter.Core.Focus;
using vRemoter.Core.Localization;
using vRemoter.Windows.Focus;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.UI.Views;

public partial class AppLaunchPickerDialog : Window
{
    private readonly Action<RemoteAppLaunchConfig> _onSave;
    private readonly InputFocusStore? _focusStore;
    private FocusAppInfo? _selected;

    public AppLaunchPickerDialog(
        string buttonTitle,
        RemoteAppLaunchConfig? existing,
        Action<RemoteAppLaunchConfig> onSave,
        InputFocusStore? focusStore = null)
    {
        _onSave = onSave;
        _focusStore = focusStore;
        InitializeComponent();
        WindowsVersionInfo.ApplyDarkWindowFrame(this);

        Title = L10n.Text("打开指定应用", "Open Specified App");
        TitleText.Text = Title;
        SubtitleText.Text = L10n.Text(
            $"为“{buttonTitle}”选择要打开或切到前台的应用。",
            $"Choose the app to launch or bring forward for “{buttonTitle}”.");
        BrowseBtn.Content = L10n.Text("浏览 .exe…", "Browse .exe…");
        RunningHint.Text = L10n.Text("或从当前运行中的应用快速选择：", "Or pick a running app:");
        AutoFocusCheck.Content = L10n.Text("打开后自动将输入焦点切入输入框", "Auto-focus input box after opening");
        AddAutoFocusCheck.Content = L10n.Text("同时加入「切换应用自动聚焦」名单", "Also add to Switch-App Auto-Focus list");
        AddAutoFocusCheck.Visibility = _focusStore is not null ? Visibility.Visible : Visibility.Collapsed;
        CancelBtn.Content = L10n.Text("取消", "Cancel");
        SaveBtn.Content = L10n.Text("保存映射", "Save Mapping");

        if (existing is not null)
        {
            _selected = new FocusAppInfo(existing.AppIdentifier, 0, existing.Name, existing.AppPath, null);
            AutoFocusCheck.IsChecked = existing.AutoFocusInput;
        }

        LoadRunningApps();
        UpdateSelectionUi();
    }

    private void LoadRunningApps()
    {
        RunningAppsWrap.Children.Clear();
        foreach (var app in WinUiaFocusBackend.GetRunningAppsForSelection().Take(18))
        {
            var btn = new Button
            {
                Content = app.Name,
                Style = (Style)FindResource("ConsoleButtonNeutral"),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 6)
            };
            btn.Click += (_, _) =>
            {
                _selected = app;
                UpdateSelectionUi();
            };
            RunningAppsWrap.Children.Add(btn);
        }
    }

    private void UpdateSelectionUi()
    {
        if (_selected is null)
        {
            SelectedAppName.Text = L10n.Text("尚未选择应用", "No app selected");
            SelectedAppName.Foreground = (Brush)FindResource("BrushSecondary");
            SelectedAppId.Text = L10n.Text("支持任意 .exe 桌面应用", "Supports any .exe application");
            SaveBtn.IsEnabled = false;
        }
        else
        {
            SelectedAppName.Text = _selected.Name;
            SelectedAppName.Foreground = (Brush)FindResource("BrushText");
            SelectedAppId.Text = _selected.AppIdentifier;
            SaveBtn.IsEnabled = !string.IsNullOrEmpty(_selected.AppPath);
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe" };
        if (dlg.ShowDialog() != true) return;
        if (WinUiaFocusBackend.GetAppInfoFromPath(dlg.FileName) is { } info)
        {
            _selected = info;
            UpdateSelectionUi();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_selected?.AppPath is null) return;
        if (AddAutoFocusCheck.IsChecked == true)
        {
            _focusStore?.AddOrEnableApp(_selected.AppIdentifier, _selected.Name, _selected.AppPath);
        }
        _onSave(new RemoteAppLaunchConfig(
            _selected.AppIdentifier,
            _selected.AppPath,
            _selected.Name,
            AutoFocusCheck.IsChecked == true));
        Close();
    }
}

