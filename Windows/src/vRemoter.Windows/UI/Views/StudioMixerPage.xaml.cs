using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using vRemoter.Core.Localization;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.UI.Views;

public partial class StudioMixerPage : UserControl
{
    private readonly ConsoleViewModel _vm;

    public StudioMixerPage(ConsoleViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        ApplyLocalizedLabels();
        _vm.PropertyChanged += OnVmPropertyChanged;
        RefreshUi();
    }

    private void ApplyLocalizedLabels()
    {
        PcMicTitle.Text = L10n.Text("电脑麦克风", "PC Microphone");
        PcMicSub.Text = L10n.Text("系统物理输入", "Built-in / Default Mic");
        PcAlignmentHint.Text = L10n.Text("120ms 延时对齐", "120 ms delay alignment");
        RemoteMicTitle.Text = L10n.Text("遥控器麦克风", "Remote Mic");
        RemoteMicSub.Text = L10n.Text("蓝牙语音遥控器", "Bluetooth Voice Remote");
        RemoteHintText.Text = L10n.Text("按遥控器语音键测试收音", "Press remote voice key to test");
        OutputTitle.Text = L10n.Text("混音输出总线", "Mixed Output Bus");
        OutputSub.Text = L10n.Text("48 kHz 双声道虚拟声卡", "48 kHz Stereo Virtual Cable");
        RefreshAudioBtn.Content = L10n.Text("刷新设备", "Refresh Audio");
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(RefreshUi);
    }

    private void RefreshUi()
    {
        PcToggle.IsChecked = _vm.PcInputEnabled;
        RemoteToggle.IsChecked = _vm.RemoteInputEnabled;

        UpdateLevelBar(PcLevelBar, PcDbText, _vm.PcInputEnabled ? _vm.PcLevelDb : -120);
        UpdateLevelBar(RemoteLevelBar, RemoteDbText, _vm.RemoteInputEnabled ? _vm.RemoteLevelDb : -120);
        RemoteHintText.Visibility = _vm.ShowMicHint && _vm.HidConnected && _vm.BleConnected
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_vm.DriverAvailable)
        {
            OutputRouteName.Text = _vm.DriverName;
            OutputRouteName.Foreground = (Brush)FindResource("BrushGreen");
            OutputRouteDetail.Text = L10n.Text(
                "虚拟回环已就绪，输入法可从此处收音",
                "Virtual loopback ready for input method capture");
        }
        else
        {
            OutputRouteName.Text = L10n.Text("未检测到虚拟声卡", "Virtual Audio Cable Missing");
            OutputRouteName.Foreground = (Brush)FindResource("BrushRed");
            OutputRouteDetail.Text = L10n.Text(
                "请点击下方「一键安装 VB-Cable」完成配置",
                "Click Install VB-Cable below to configure");
        }

        RebuildStatusRows();
    }

    private void RebuildStatusRows()
    {
        StatusRowsGrid.Children.Clear();
        string imeName = VoiceInputMethodProfiles.GetShortName(_vm.VoiceInputMethod);

        // 1. IME input device
        string imeVal = _vm.ImeIsRecording
            ? (_vm.ImeUsesVirtualMic
                ? L10n.Text($"{_vm.DriverName} · 录音中", $"{_vm.DriverName} · Recording")
                : _vm.ImeInput)
            : L10n.Text("当前未录音", "Not recording");
        bool imeErr = _vm.ImeIsRecording && !_vm.ImeUsesVirtualMic;
        AddStatusRow(
            L10n.Text($"{imeName}输入设备", $"{imeName} input"),
            imeVal,
            imeErr ? "BrushRed" : "BrushGreen",
            imeErr ? L10n.Text("设置向导", "Setup Guide") : null,
            () => OpenGuide(SetupGuideKind.InputMethod));

        // 2. Virtual audio driver
        AddStatusRow(
            L10n.Text("虚拟声卡驱动", "Virtual Audio"),
            _vm.DriverAvailable
                ? L10n.Text($"{_vm.DriverName} 可用", $"{_vm.DriverName} available")
                : L10n.Text("未安装 VB-Cable", "VB-Cable not installed"),
            _vm.DriverAvailable ? "BrushGreen" : "BrushRed",
            _vm.DriverAvailable ? null : L10n.Text("一键安装 VB-Cable", "Install VB-Cable"),
            () => OpenGuide(SetupGuideKind.VirtualCable));

        // 3. Remote connection
        string remoteVal = (_vm.HidConnected && _vm.BleConnected)
            ? (_vm.RemoteStreaming
                ? L10n.Text("HID · BLE · 语音流传输中", "HID · BLE · Streaming")
                : L10n.Text("HID · BLE 已就绪", "HID · BLE ready"))
            : (_vm.HidConnected || _vm.BleConnected)
                ? L10n.Text("正在连接语音服务", "Connecting voice service")
                : L10n.Text("HID 与 BLE 未连接", "HID and BLE disconnected");
        AddStatusRow(
            L10n.Text("遥控器连接", "Remote connection"),
            remoteVal,
            (_vm.HidConnected && _vm.BleConnected) ? "BrushGreen" : "BrushAmber",
            (_vm.HidConnected && _vm.BleConnected) ? null : L10n.Text("蓝牙设置", "Bluetooth"),
            () => OpenGuide(SetupGuideKind.Bluetooth));

        // 4. Keyboard hook
        AddStatusRow(
            L10n.Text("全局键盘钩子", "Input Hook"),
            _vm.KeyboardHookReady
                ? L10n.Text("WH_KEYBOARD_LL 运行中", "WH_KEYBOARD_LL active")
                : L10n.Text("未挂载（请重启应用）", "Inactive (restart required)"),
            _vm.KeyboardHookReady ? "BrushGreen" : "BrushRed",
            null,
            null);

        // 5. UIPI / Admin compatibility
        AddStatusRow(
            L10n.Text("高权限窗口穿透 (UIPI)", "Admin Window Compatibility"),
            _vm.IsElevated
                ? L10n.Text("管理员模式 · 支持管理员窗口与全局聚焦", "Elevated · Full UIPI bypass active")
                : L10n.Text("标准模式 · 前台为管理员窗口时请开启增强模式", "Standard · Elevated apps may block SendInput"),
            _vm.IsElevated ? "BrushGreen" : "BrushAmber",
            _vm.IsElevated ? null : L10n.Text("管理员重启", "Run as Admin"),
            () => _vm.RequestRestartAsAdmin());
    }

    private void AddStatusRow(string title, string value, string toneKey, string? actionText, Action? onAction)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("BrushSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTb, 0);
        row.Children.Add(titleTb);

        var valTb = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(toneKey),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(valTb, 1);
        row.Children.Add(valTb);

        if (!string.IsNullOrEmpty(actionText) && onAction is not null)
        {
            var btn = new Button
            {
                Content = actionText,
                Style = (Style)FindResource(toneKey == "BrushRed" ? "ConsoleButtonDanger" : "ConsoleButtonNeutral"),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Click += (_, _) => onAction();
            Grid.SetColumn(btn, 2);
            row.Children.Add(btn);
        }

        StatusRowsGrid.Children.Add(row);
    }

    private void OpenGuide(SetupGuideKind kind)
    {
        var dialog = new SetupGuideDialog(kind, _vm)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private static void UpdateLevelBar(Border bar, TextBlock label, double db)
    {
        double clamped = Math.Clamp(db, -60.0, 0.0);
        double ratio = (clamped + 60.0) / 60.0;
        bar.Width = Math.Round(188.0 * ratio);
        label.Text = db <= -119 ? "-∞ dB" : $"{db:F0} dB";
    }

    private void OnPcToggleClick(object sender, RoutedEventArgs e) =>
        _vm.SetPcInput(PcToggle.IsChecked == true);

    private void OnRemoteToggleClick(object sender, RoutedEventArgs e) =>
        _vm.SetRemoteInput(RemoteToggle.IsChecked == true);

    private void OnRefreshAudioClick(object sender, RoutedEventArgs e) =>
        _vm.RequestRefreshAudio();
}

