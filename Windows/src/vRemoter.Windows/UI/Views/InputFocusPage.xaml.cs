using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using vRemoter.Core.Focus;
using vRemoter.Core.Localization;
using vRemoter.Windows.Focus;

namespace vRemoter.Windows.UI.Views;

public partial class InputFocusPage : UserControl
{
    private readonly ConsoleViewModel _vm;
    private readonly InputFocusStore _store;
    private readonly InputFocusCoordinator _coordinator;

    public InputFocusPage(ConsoleViewModel vm, InputFocusStore store, InputFocusCoordinator coordinator)
    {
        _vm = vm;
        _store = store;
        _coordinator = coordinator;
        InitializeComponent();

        ApplyLabels();
        _store.Changed += () => Dispatcher.BeginInvoke(RefreshUi);
        RefreshUi();
    }

    private void ApplyLabels()
    {
        FocusTitle.Text = L10n.Text("应用切换与输入框自动聚焦", "App Switch & Input Auto-Focus");
        AutoSwitchLabel.Text = L10n.Text("切回应用时自动聚焦", "Auto-focus on switch");
        FocusDesc.Text = L10n.Text(
            "切到下方勾选的应用时自动将光标拉回输入框；也可在「按键映射」里把任意按键设为「聚焦当前输入框」，随时一键聚焦。",
            "Automatically focuses the input box when switching to enabled apps. You can also bind a remote key to Focus Input Box.");
        PullCodeCheck.Content = L10n.Text("从代码区切回时拉回输入框（保护搜索框）", "Pull from code editor on switch (protects Search)");
        FocusBeforeVoiceCheck.Content = L10n.Text("按语音键前检查聚焦", "Focus before voice");
        AddAntigravityBtn.Content = L10n.Text("+ 添加 Antigravity IDE", "+ Add Antigravity IDE");
        AddRunningBtn.Content = L10n.Text("从运行中添加…", "Add Running…");
        BrowseExeBtn.Content = L10n.Text("浏览 .exe…", "Browse .exe…");
        CancelBindBtn.Content = L10n.Text("取消", "Cancel");
        ConfirmBindBtn.Content = L10n.Text("确认指定", "Confirm");
    }

    private void RefreshUi()
    {
        AutoFocusSwitch.IsChecked = _store.AutoFocusOnSwitchEnabled;
        PullCodeCheck.IsChecked = _store.PullFromOtherEditorsOnSwitch;
        FocusBeforeVoiceCheck.IsChecked = _store.FocusBeforeVoiceEnabled;
        FeedbackLabel.Text = _store.LastFeedback;

        var apps = _store.Apps;
        ConfiguredAppsHeader.Text = L10n.Text($"已配置应用（{apps.Count}）", $"Configured Apps ({apps.Count})");
        AddAntigravityBtn.Visibility = FindAntigravityCandidate() is not null ? Visibility.Visible : Visibility.Collapsed;

        var bind = _store.BindingState;
        if (bind.IsSelecting)
        {
            BindingBanner.Visibility = Visibility.Visible;
            BindingBanner.Background = bind.CandidateReady
                ? new SolidColorBrush(Color.FromRgb(0x0F, 0x33, 0x21))
                : (Brush)FindResource("BrushAmberDeep");
            BindingBanner.BorderBrush = (Brush)FindResource(bind.CandidateReady ? "BrushGreen" : "BrushAmber");
            BindingTitleText.Foreground = (Brush)FindResource(bind.CandidateReady ? "BrushGreen" : "BrushAmber");
            BindingTitleText.Text = L10n.Text(
                $"正在为「{bind.SelectingAppName}」指定输入框（剩余 {bind.SecondsLeft} 秒）",
                $"Binding input for “{bind.SelectingAppName}” ({bind.SecondsLeft}s left)");
            BindingMessageText.Text = bind.CaptureStatus.GetMessage();
            ConfirmBindBtn.IsEnabled = bind.CandidateReady;
        }
        else
        {
            BindingBanner.Visibility = Visibility.Collapsed;
        }

        AppsListPanel.Children.Clear();
        if (apps.Count == 0)
        {
            AppsListPanel.Children.Add(new TextBlock
            {
                Text = L10n.Text(
                    "尚未添加自动聚焦的应用。点击上方「从运行中添加…」或「浏览 .exe…」。",
                    "No apps configured yet. Use Add Running… or Browse .exe… above."),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTertiary"),
                Margin = new Thickness(8, 36, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        foreach (var entry in apps)
        {
            AppsListPanel.Children.Add(BuildAppRow(entry, bind.IsSelecting));
        }
    }

    private UIElement BuildAppRow(InputFocusAppEntry entry, bool isBinding)
    {
        var status = _store.GetAppState(entry.AppIdentifier);
        bool hasLocator = entry.Locator is not null;
        string statusText = status is { } s
            ? s.ShortDescription()
            : entry.Mode == InputFocusMode.Manual
                ? (hasLocator
                    ? L10n.Text("已指定输入框（结构指纹已保存）", "Input bound (locator saved)")
                    : L10n.Text("尚未指定输入框，请点右侧「指定输入框」", "Not bound yet; click Bind Input"))
                : L10n.Text("自动识别模式：切到该应用时自动查找输入框", "Auto mode: discovers input on switch");

        var card = new Border
        {
            Background = (Brush)FindResource("BrushSurface2"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 0, 0, 7)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sw = new CheckBox
        {
            Style = (Style)FindResource("ConsoleSwitchStyle"),
            IsChecked = entry.Enabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        sw.Click += (_, _) => _store.SetAppEnabled(sw.IsChecked == true, entry.AppIdentifier);
        Grid.SetColumn(sw, 0);
        grid.Children.Add(sw);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(entry.Enabled ? "BrushText" : "BrushTertiary"),
            Margin = new Thickness(0, 0, 8, 0)
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = entry.AppIdentifier,
            FontSize = 10.5,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)FindResource("BrushTertiary"),
            VerticalAlignment = VerticalAlignment.Center
        });
        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = statusText,
            FontSize = 10.5,
            Foreground = (Brush)FindResource(status?.NeedsUserAction() == true ? "BrushAmber" : "BrushSecondary"),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(info, 2);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (entry.Mode == InputFocusMode.Manual)
        {
            var autoBtn = new Button
            {
                Content = L10n.Text("切回自动", "Use Auto"),
                Style = (Style)FindResource("ConsoleButtonNeutral"),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 0)
            };
            autoBtn.Click += (_, _) => _store.ResetToAuto(entry.AppIdentifier);
            actions.Children.Add(autoBtn);
        }

        var bindBtn = new Button
        {
            Content = hasLocator ? L10n.Text("重新指定", "Rebind") : L10n.Text("指定输入框", "Bind Input"),
            Style = (Style)FindResource(hasLocator ? "ConsoleButtonNeutral" : "ConsoleButtonGood"),
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(0, 0, 6, 0),
            IsEnabled = !isBinding
        };
        bindBtn.Click += (_, _) => _coordinator.BeginBinding(entry.AppIdentifier, entry.Name);
        actions.Children.Add(bindBtn);

        var delBtn = new Button
        {
            Content = L10n.Text("移除", "Remove"),
            Style = (Style)FindResource("ConsoleButtonNeutral"),
            Padding = new Thickness(8, 4, 8, 4)
        };
        delBtn.Click += (_, _) => _store.RemoveApp(entry.AppIdentifier);
        actions.Children.Add(delBtn);

        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);
        card.Child = grid;
        return card;
    }

    private FocusAppInfo? FindAntigravityCandidate()
    {
        if (_store.GetEntry("Antigravity.exe") is not null) return null;
        var running = WinUiaFocusBackend.GetRunningAppsForSelection()
            .FirstOrDefault(a => a.AppIdentifier.Contains("Antigravity", StringComparison.OrdinalIgnoreCase));
        if (running is not null) return running;

        string candidatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Antigravity", "Antigravity.exe");
        return File.Exists(candidatePath) ? WinUiaFocusBackend.GetAppInfoFromPath(candidatePath) : null;
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        _store.AutoFocusOnSwitchEnabled = AutoFocusSwitch.IsChecked == true;
        _store.PullFromOtherEditorsOnSwitch = PullCodeCheck.IsChecked == true;
        _store.FocusBeforeVoiceEnabled = FocusBeforeVoiceCheck.IsChecked == true;
    }

    private void OnAddAntigravityClick(object sender, RoutedEventArgs e)
    {
        if (FindAntigravityCandidate() is { } ide)
        {
            _store.AddOrEnableApp(ide.AppIdentifier, ide.Name, ide.AppPath);
        }
    }

    private void OnAddRunningClick(object sender, RoutedEventArgs e)
    {
        var apps = WinUiaFocusBackend.GetRunningAppsForSelection();
        if (apps.Count == 0) return;

        var menu = new ContextMenu();
        foreach (var app in apps)
        {
            var item = new MenuItem { Header = $"{app.Name} ({app.AppIdentifier})" };
            item.Click += (_, _) => _store.AddOrEnableApp(app.AppIdentifier, app.Name, app.AppPath);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = AddRunningBtn;
        menu.IsOpen = true;
    }

    private void OnBrowseExeClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Applications (*.exe)|*.exe",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;
        foreach (string file in dlg.FileNames)
        {
            if (WinUiaFocusBackend.GetAppInfoFromPath(file) is { } info)
            {
                _store.AddOrEnableApp(info.AppIdentifier, info.Name, info.AppPath);
            }
        }
    }

    private void OnConfirmBindClick(object sender, RoutedEventArgs e) => _coordinator.ConfirmBinding();
    private void OnCancelBindClick(object sender, RoutedEventArgs e) => _coordinator.CancelBinding();
}

