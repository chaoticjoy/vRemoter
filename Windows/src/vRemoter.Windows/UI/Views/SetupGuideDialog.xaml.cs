using System.Diagnostics;
using System.Windows;
using vRemoter.Core.Localization;
using vRemoter.Core.Session;
using vRemoter.Windows.Audio;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.UI.Views;

public enum SetupGuideKind
{
    VirtualCable,
    Bluetooth,
    InputMethod
}

public partial class SetupGuideDialog : Window
{
    private readonly SetupGuideKind _kind;
    private readonly ConsoleViewModel _vm;

    public SetupGuideDialog(SetupGuideKind kind, ConsoleViewModel vm)
    {
        _kind = kind;
        _vm = vm;
        InitializeComponent();
        WindowsVersionInfo.ApplyDarkWindowFrame(this);
        CloseBtn.Content = L10n.Text("关闭", "Close");
        ConfigureContent();
    }

    private void ConfigureContent()
    {
        switch (_kind)
        {
            case SetupGuideKind.VirtualCable:
                Title = L10n.Text("安装虚拟麦克风 (VB-Cable)", "Install Virtual Microphone (VB-Cable)");
                GuideTitle.Text = Title;
                GuideBody.Text = L10n.Text(
                    "1. vRemoter 需要借助虚拟声卡将「电脑麦克风 + 遥控器麦克风」混音后送给豆包 / 微信输入法。\n" +
                    "2. 点击下方「一键下载并安装」，将自动下载官方 WHQL 签名驱动并拉起安装向导，无需手动修改系统签名设置。\n" +
                    "3. 安装完成后点击主界面的「刷新设备」，即可看到 CABLE Output 就绪。",
                    "1. vRemoter uses a virtual audio loopback cable to feed mixed PC + remote audio into Doubao / WeType.\n" +
                    "2. Click Download & Install to fetch the official WHQL-signed VB-Cable package and launch setup.\n" +
                    "3. Once finished, click Refresh Audio on the mixer page.");
                PrimaryBtn.Content = L10n.Text("一键下载并安装 VB-Cable", "Download & Install VB-Cable");
                SecondaryBtn.Content = L10n.Text("打开 VB-Audio 官网", "Open VB-Audio Website");
                SecondaryBtn.Visibility = Visibility.Visible;
                break;

            case SetupGuideKind.Bluetooth:
                Title = L10n.Text("蓝牙遥控器配对指引", "Bluetooth Remote Pairing Guide");
                GuideTitle.Text = Title;
                GuideBody.Text = L10n.Text(
                    "1. 请在 Windows「设置 → 蓝牙和其他设备」中添加并配对 Chromecast Voice Remote 或 X6-Remote。\n" +
                    "2. 配对成功后，即使蓝牙列表显示已连接 HID，遥控器不会持续广播；vRemoter 会通过已配对端点自动识别并建立 ATVV 语音通道。\n" +
                    "3. 若长时间显示未连接，请在系统蓝牙设置中重新开关蓝牙或重连一次遥控器。",
                    "1. Pair your Chromecast Voice Remote or X6-Remote in Windows Settings > Bluetooth & devices.\n" +
                    "2. Once paired, Windows HID connections stop advertising; vRemoter monitors paired endpoints automatically.\n" +
                    "3. If stuck connecting, toggle Bluetooth or reconnect the remote once.");
                PrimaryBtn.Content = L10n.Text("打开 Windows 蓝牙设置", "Open Bluetooth Settings");
                break;

            case SetupGuideKind.InputMethod:
                Title = _vm.VoiceInputMethod == VoiceInputMethodKind.WeType
                    ? L10n.Text("微信输入法麦克风设置", "WeType Microphone Setup")
                    : L10n.Text("豆包输入法麦克风设置", "Doubao Microphone Setup");
                GuideTitle.Text = Title;
                GuideBody.Text = _vm.VoiceInputMethod == VoiceInputMethodKind.WeType
                    ? L10n.Text(
                        "微信输入法 Windows 版默认跟随系统默认录音设备。保持选中「微信输入法」时，vRemoter 会自动将系统默认录音切换为 CABLE Output，并在退出时自动恢复原麦克风。",
                        "WeType follows the Windows default capture device. vRemoter automatically routes the default microphone to CABLE Output while WeType is selected and restores it on exit.")
                    : L10n.Text(
                        "1. 打开豆包输入法设置 → 语音输入 → 麦克风选择。\n" +
                        "2. 将麦克风设为「CABLE Output (VB-Audio Virtual Cable)」。\n" +
                        "3. 确认语音快捷键（如 右 Alt）与 vRemoter「按键映射」页一致。",
                        "1. Open Doubao IME Settings > Voice Input > Microphone.\n" +
                        "2. Select CABLE Output (VB-Audio Virtual Cable).\n" +
                        "3. Make sure the trigger shortcut (e.g. Right Alt) matches vRemoter Key Mapping.");
                PrimaryBtn.Content = L10n.Text("打开 Windows 声音设置", "Open Sound Settings");
                break;
        }
    }

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_kind == SetupGuideKind.VirtualCable)
        {
            PrimaryBtn.IsEnabled = false;
            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var (launched, message) = await VirtualAudioDetector.InstallVbCableGuidedAsync(progress);
            StatusText.Text = message;
            PrimaryBtn.IsEnabled = true;
            if (launched)
            {
                _vm.RequestRefreshAudio();
            }
            return;
        }

        string uri = _kind == SetupGuideKind.Bluetooth ? "ms-settings:bluetooth" : "ms-settings:sound";
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(VirtualAudioDetector.VbCableDownloadPage) { UseShellExecute = true }); } catch { }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

