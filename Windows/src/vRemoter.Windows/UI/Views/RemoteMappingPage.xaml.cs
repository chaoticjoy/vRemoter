using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using vRemoter.Core.Localization;
using vRemoter.Core.Mapping;
using vRemoter.Core.Session;
using vRemoter.Windows.Focus;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.UI.Views;

public partial class RemoteMappingPage : UserControl
{
    private readonly ConsoleViewModel _vm;
    private readonly RemoteMappingStore _mappings;
    private readonly InputFocusStore? _focusStore;
    private SupportedRemoteId _selectedRemote = SupportedRemoteId.Chromecast;
    private bool _suppressSelectionEvents;

    public RemoteMappingPage(ConsoleViewModel vm, RemoteMappingStore mappings, InputFocusStore? focusStore = null)
    {
        _vm = vm;
        _mappings = mappings;
        _focusStore = focusStore;
        InitializeComponent();

        ApplyLabels();
        PopulateTriggerControls();
        _vm.PropertyChanged += OnVmChanged;
        _mappings.Changed += () => Dispatcher.BeginInvoke(RefreshMappingPanel);

        RefreshDeviceCards();
        RefreshMappingPanel();
    }

    private void ApplyLabels()
    {
        TriggerTitle.Text = L10n.Text("语音输入法与触发键", "Voice input method & trigger");
        TriggerDesc.Text = L10n.Text(
            "遥控器语音键与电脑键盘都使用这个按键控制输入法。开关型快捷键选「点按切换」，按住说话选「按住说话」。",
            "Remote voice key and PC keyboard share this trigger. Choose Tap to toggle or Hold to talk to match your IME.");
        SupportedDevicesHeader.Text = L10n.Text("支持的设备", "Supported devices");
        ProfileNote.Text = L10n.Text("设置跟随型号，不绑定某一只遥控器", "Settings follow the model, not one unit");
        RemoteSubText.Text = L10n.Text("点击右侧菜单修改按键", "Use each menu to assign a key");
        EnableMappingLabel.Text = L10n.Text("启用映射", "Enable");
        UnchangedHint.Text = L10n.Text("未启用时保持系统原行为", "System behavior is unchanged");
        ResetMappingBtn.Content = L10n.Text("恢复默认", "Reset");
    }

    private void PopulateTriggerControls()
    {
        _suppressSelectionEvents = true;
        ImeCombo.ItemsSource = new[]
        {
            new ComboOption<VoiceInputMethodKind>(VoiceInputMethodKind.Doubao, VoiceInputMethodProfiles.GetTitle(VoiceInputMethodKind.Doubao)),
            new ComboOption<VoiceInputMethodKind>(VoiceInputMethodKind.WeType, VoiceInputMethodProfiles.GetTitle(VoiceInputMethodKind.WeType))
        };
        ImeCombo.DisplayMemberPath = nameof(ComboOption<VoiceInputMethodKind>.Label);
        ImeCombo.SelectedIndex = _vm.VoiceInputMethod == VoiceInputMethodKind.WeType ? 1 : 0;

        ModeCombo.ItemsSource = new[]
        {
            new ComboOption<InputTriggerMode>(InputTriggerMode.Toggle, InputTriggerMode.Toggle.GetTitle()),
            new ComboOption<InputTriggerMode>(InputTriggerMode.Hold, InputTriggerMode.Hold.GetTitle())
        };
        ModeCombo.DisplayMemberPath = nameof(ComboOption<InputTriggerMode>.Label);
        ModeCombo.SelectedIndex = _vm.TriggerMode == InputTriggerMode.Hold ? 1 : 0;

        RefreshTriggerPresetItems();
        _suppressSelectionEvents = false;
    }

    private void RefreshTriggerPresetItems()
    {
        _suppressSelectionEvents = true;
        var items = InputTriggerSettings.Presets
            .Select(p => new ComboOption<InputTriggerPreset>(p, InputTriggerSettings.GetPresetTitle(p)))
            .ToList();
        TriggerCombo.ItemsSource = items;
        TriggerCombo.DisplayMemberPath = nameof(ComboOption<InputTriggerPreset>.Label);
        TriggerCombo.SelectedIndex = Math.Max(0, items.FindIndex(x => x.Value == _vm.TriggerPreset));
        _suppressSelectionEvents = false;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ImeCombo.IsEnabled = !_vm.RemoteStreaming;
            ModeCombo.IsEnabled = !_vm.RemoteStreaming;
            TriggerCombo.IsEnabled = !_vm.RemoteStreaming;
            RefreshDeviceCards();
        });
    }

    private void RefreshDeviceCards()
    {
        ChromecastCardBtn.Content = BuildDeviceCard(SupportedRemoteId.Chromecast, _vm.ChromecastConnected, _selectedRemote == SupportedRemoteId.Chromecast);
        X6CardBtn.Content = BuildDeviceCard(SupportedRemoteId.X6, _vm.X6Connected, _selectedRemote == SupportedRemoteId.X6);
    }

    private UIElement BuildDeviceCard(SupportedRemoteId remote, bool connected, bool selected)
    {
        var border = new Border
        {
            Padding = new Thickness(11),
            CornerRadius = new CornerRadius(11),
            Background = selected ? (Brush)FindResource("BrushSurface2") : new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0E)),
            BorderBrush = selected ? (Brush)FindResource("BrushGreen") : (Brush)FindResource("BrushLineSoft"),
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = (Brush)FindResource(connected ? "BrushGreen" : "BrushTertiary"),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        top.Children.Add(new TextBlock
        {
            Text = remote.GetTitle(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("BrushText")
        });
        stack.Children.Add(top);

        var bottom = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        bottom.Children.Add(new TextBlock
        {
            Text = remote.GetSignature(),
            FontSize = 10.5,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)FindResource("BrushTertiary")
        });
        bottom.Children.Add(new TextBlock
        {
            Text = connected ? L10n.Text("已连接", "Connected") : L10n.Text("未连接", "Offline"),
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource(connected ? "BrushGreen" : "BrushTertiary"),
            HorizontalAlignment = HorizontalAlignment.Right
        });
        stack.Children.Add(bottom);
        border.Child = stack;
        return border;
    }

    private void RefreshMappingPanel()
    {
        RemoteTitleText.Text = _selectedRemote.GetTitle();
        bool enabled = _mappings.IsEnabled(_selectedRemote);
        EnableMappingSwitch.IsChecked = enabled;
        UnchangedHint.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        string imgName = _selectedRemote == SupportedRemoteId.Chromecast ? "chromecast-voice-remote.png" : "x6-remote.png";
        try
        {
            RemoteImage.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/RemoteImages/{imgName}"));
        }
        catch { }

        ButtonCountText.Text = _selectedRemote == SupportedRemoteId.Chromecast
            ? L10n.Text("14 个可映射键", "14 mappable buttons")
            : L10n.Text("15 个正面可映射键", "15 mappable front buttons");

        ButtonsListPanel.Children.Clear();
        string voiceName = VoiceInputMethodProfiles.GetShortName(_vm.VoiceInputMethod);
        var allTargets = Enum.GetValues<RemoteMappingTarget>().Where(t => t != RemoteMappingTarget.DoubaoVoice).ToList();

        foreach (var button in RemoteProfiles.GetButtons(_selectedRemote))
        {
            ButtonsListPanel.Children.Add(BuildMappingRow(button, allTargets, voiceName));
        }
    }

    private UIElement BuildMappingRow(RemoteButtonDefinition button, List<RemoteMappingTarget> targets, string voiceName)
    {
        var rowBorder = new Border
        {
            Background = (Brush)FindResource("BrushSurface2"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 5)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = new TextBlock
        {
            Text = button.Glyph,
            Foreground = (Brush)FindResource(button.VoiceControlled ? "BrushGreen" : "BrushSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(glyph);

        var title = new TextBlock
        {
            Text = button.Title,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("BrushText"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        if (!button.Remappable)
        {
            var tb = new TextBlock
            {
                Text = L10n.Text("设备内部功能", "Device-only function"),
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTertiary"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tb, 2);
            grid.Children.Add(tb);
        }
        else if (button.VoiceControlled)
        {
            var tb = new TextBlock
            {
                Text = RemoteMappingTarget.DoubaoVoice.GetTitle(voiceName),
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushGreen"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tb, 2);
            grid.Children.Add(tb);
        }
        else
        {
            var current = _mappings.GetTarget(button, _selectedRemote);
            var options = targets.Select(t => new ComboOption<RemoteMappingTarget>(
                t,
                t == current && t is RemoteMappingTarget.Custom or RemoteMappingTarget.OpenApp
                    ? _mappings.GetTargetTitle(button, _selectedRemote, voiceName)
                    : t.GetTitle(voiceName)))
                .ToList();

            var combo = new ComboBox
            {
                Style = (Style)FindResource("ConsoleComboBoxStyle"),
                ItemsSource = options,
                DisplayMemberPath = nameof(ComboOption<RemoteMappingTarget>.Label),
                SelectedIndex = Math.Max(0, options.FindIndex(o => o.Value == current)),
                Width = 185,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not ComboOption<RemoteMappingTarget> picked) return;
                if (picked.Value == RemoteMappingTarget.Custom)
                {
                    OpenShortcutRecorder(button);
                }
                else if (picked.Value == RemoteMappingTarget.OpenApp)
                {
                    OpenAppPicker(button);
                }
                else
                {
                    _mappings.SetTarget(picked.Value, button, _selectedRemote);
                }
            };
            Grid.SetColumn(combo, 2);
            grid.Children.Add(combo);
        }

        rowBorder.Child = grid;
        return rowBorder;
    }

    private void OpenShortcutRecorder(RemoteButtonDefinition button)
    {
        var dialog = new ShortcutRecorderDialog(button.Title, shortcut =>
        {
            _mappings.SetCustomShortcut(shortcut, button, _selectedRemote);
        }) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        RefreshMappingPanel();
    }

    private void OpenAppPicker(RemoteButtonDefinition button)
    {
        var existing = _mappings.GetAppLaunchConfig(button, _selectedRemote);
        var dialog = new AppLaunchPickerDialog(button.Title, existing, cfg =>
        {
            _mappings.SetAppLaunchConfig(cfg, button, _selectedRemote);
        }, _focusStore) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        RefreshMappingPanel();
    }

    private void OnImeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents || ImeCombo.SelectedItem is not ComboOption<VoiceInputMethodKind> opt) return;
        _vm.VoiceInputMethod = opt.Value;
        RefreshMappingPanel();
    }

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents || ModeCombo.SelectedItem is not ComboOption<InputTriggerMode> opt) return;
        _vm.TriggerMode = opt.Value;
    }

    private void OnTriggerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents || TriggerCombo.SelectedItem is not ComboOption<InputTriggerPreset> opt) return;
        if (opt.Value == InputTriggerPreset.Custom)
        {
            var dialog = new ShortcutRecorderDialog(L10n.Text("语音触发快捷键", "Voice Trigger Key"), chord =>
            {
                InputTriggerSettings.CustomChord = new TriggerKeyChord(
                    BuildTriggerVkList(chord),
                    chord.Label);
                _vm.TriggerPreset = InputTriggerPreset.Custom;
                RefreshTriggerPresetItems();
            }) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            RefreshTriggerPresetItems();
            return;
        }
        _vm.TriggerPreset = opt.Value;
    }

    private static ushort[] BuildTriggerVkList(RemoteCustomShortcut s)
    {
        var list = new List<ushort>();
        if (s.Ctrl) list.Add(0xA2);
        if (s.Shift) list.Add(0xA0);
        if (s.Alt) list.Add(0xA4);
        if (s.Win) list.Add(0x5B);
        if (s.VirtualKey != 0 && !list.Contains(s.VirtualKey)) list.Add(s.VirtualKey);
        return list.ToArray();
    }

    private void OnSelectChromecast(object sender, RoutedEventArgs e)
    {
        _selectedRemote = SupportedRemoteId.Chromecast;
        RefreshDeviceCards();
        RefreshMappingPanel();
    }

    private void OnSelectX6(object sender, RoutedEventArgs e)
    {
        _selectedRemote = SupportedRemoteId.X6;
        RefreshDeviceCards();
        RefreshMappingPanel();
    }

    private void OnEnableMappingClick(object sender, RoutedEventArgs e)
    {
        bool enabled = EnableMappingSwitch.IsChecked == true;
        _mappings.SetEnabled(enabled, _selectedRemote);
        _vm.NotifyRemoteMappingEnabled(_selectedRemote, enabled);
    }

    private void OnResetMappingClick(object sender, RoutedEventArgs e) => _mappings.Reset(_selectedRemote);

    private sealed record ComboOption<T>(T Value, string Label);
}

