using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using vRemoter.Core.Localization;
using vRemoter.Core.Mapping;
using vRemoter.Windows.Focus;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.UI.Views;

public partial class MainConsoleWindow : Window
{
    private readonly ConsoleViewModel _vm;
    private readonly StudioMixerPage _mixerPage;
    private readonly RemoteMappingPage _mappingPage;
    private readonly InputFocusPage _focusPage;

    public bool AllowRealClose { get; set; }

    public MainConsoleWindow(
        ConsoleViewModel vm,
        RemoteMappingStore mappingStore,
        InputFocusStore focusStore,
        InputFocusCoordinator focusCoordinator)
    {
        _vm = vm;
        InitializeComponent();
        WindowsVersionInfo.ApplyDarkWindowFrame(this);

        Title = L10n.Text("vRemoter 控制台", "vRemoter Console");
        string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.4.0";
        VersionLabel.Text = ver + L10n.Text(" · 小卒优化版", " · Xiaozu Optimized");
        StopMicButton.Content = L10n.Text("结束录音", "Stop");

        _mixerPage = new StudioMixerPage(vm);
        _mappingPage = new RemoteMappingPage(vm, mappingStore, focusStore);
        _focusPage = new InputFocusPage(vm, focusStore, focusCoordinator);

        _vm.PropertyChanged += OnViewModelChanged;
        Closing += OnWindowClosing;

        RenderActiveTab();
        UpdateStatusVisuals();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!AllowRealClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (e.PropertyName is nameof(ConsoleViewModel.SelectedPage))
            {
                RenderActiveTab();
            }
            UpdateStatusVisuals();
        });
    }

    private void RenderActiveTab()
    {
        PageHost.Content = _vm.SelectedPage switch
        {
            ConsolePage.Mapping => _mappingPage,
            ConsolePage.Focus => _focusPage,
            _ => _mixerPage
        };

        StyleTabButton(TabMixerBtn, L10n.Text("音频", "Audio"), _vm.SelectedPage == ConsolePage.Mixer);
        StyleTabButton(TabMappingBtn, L10n.Text("按键映射", "Key Mapping"), _vm.SelectedPage == ConsolePage.Mapping);
        StyleTabButton(TabFocusBtn, L10n.Text("应用聚焦", "App Focus"), _vm.SelectedPage == ConsolePage.Focus);
    }

    private void StyleTabButton(Button btn, string title, bool active)
    {
        btn.Content = title;
        btn.FontSize = 11.5;
        btn.FontWeight = FontWeights.SemiBold;
        btn.Background = active
            ? (Brush)FindResource("BrushSurface2")
            : Brushes.Transparent;
        btn.Foreground = active
            ? (Brush)FindResource("BrushText")
            : (Brush)FindResource("BrushSecondary");
    }

    private void UpdateStatusVisuals()
    {
        StatusHeaderLabel.Text = _vm.HeaderStatusText;
        StopMicButton.Visibility = _vm.RemoteStreaming ? Visibility.Visible : Visibility.Collapsed;

        string brushKey = _vm.RemoteStreaming
            ? "BrushAmber"
            : (!_vm.DriverAvailable || !_vm.KeyboardHookReady)
                ? "BrushRed"
                : (_vm.HidConnected && _vm.BleConnected)
                    ? "BrushGreen"
                    : "BrushAmber";

        var brush = (Brush)FindResource(brushKey);
        StatusDot.Fill = brush;
        StatusHeaderLabel.Foreground = brush;
    }

    private void OnTabMixerClick(object sender, RoutedEventArgs e) => _vm.SelectedPage = ConsolePage.Mixer;
    private void OnTabMappingClick(object sender, RoutedEventArgs e) => _vm.SelectedPage = ConsolePage.Mapping;
    private void OnTabFocusClick(object sender, RoutedEventArgs e) => _vm.SelectedPage = ConsolePage.Focus;
    private void OnStopMicClick(object sender, RoutedEventArgs e) => _vm.RequestStopMicrophone();
}

