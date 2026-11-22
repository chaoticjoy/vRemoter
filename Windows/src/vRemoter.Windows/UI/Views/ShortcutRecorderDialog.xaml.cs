using System.Windows;
using System.Windows.Input;
using vRemoter.Core.Localization;
using vRemoter.Core.Mapping;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.UI.Views;

public partial class ShortcutRecorderDialog : Window
{
    private readonly Action<RemoteCustomShortcut> _onSave;
    private RemoteCustomShortcut? _captured;

    public ShortcutRecorderDialog(string buttonTitle, Action<RemoteCustomShortcut> onSave)
    {
        _onSave = onSave;
        InitializeComponent();
        WindowsVersionInfo.ApplyDarkWindowFrame(this);

        Title = L10n.Text("录制按键", "Record Key");
        TitleLabel.Text = L10n.Text($"为「{buttonTitle}」录制按键", $"Record key for “{buttonTitle}”");
        HintLabel.Text = L10n.Text("请按下任意按键或组合键，然后点击保存。", "Press any key or shortcut, then click Save.");
        CapturedLabel.Text = L10n.Text("等待按键…", "Waiting for key…");
        CancelBtn.Content = L10n.Text("取消", "Cancel");
        SaveBtn.Content = L10n.Text("保存", "Save");

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool win = (Keyboard.Modifiers & ModifierKeys.Windows) != 0;

        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        if (win) parts.Add("Win");
        parts.Add(InputTriggerSettings.FormatVirtualKeyName(vk));

        string label = string.Join(" + ", parts);
        _captured = new RemoteCustomShortcut(vk, ctrl, shift, alt, win, label);
        CapturedLabel.Text = label;
        SaveBtn.IsEnabled = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_captured is not null)
        {
            _onSave(_captured);
        }
        Close();
    }
}

