using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using vRemoter.Core.Localization;
using vRemoter.Core.Updates;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.UI.Views;

public partial class UpdateWindow : Window
{
    private readonly GitHubUpdateService _service;
    private readonly string _currentVersion;
    private GitHubRelease? _availableRelease;
    private bool _isBusy;

    public bool AllowRealClose { get; set; }

    public UpdateWindow()
    {
        InitializeComponent();
        WindowsVersionInfo.ApplyDarkWindowFrame(this);

        _currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.4.0";
        _service = new GitHubUpdateService(GitHubUpdateService.DefaultReleasesUrl, _currentVersion);

        Title = L10n.Text("版本与更新", "Version & Updates");
        VersionText.Text = L10n.Text($"版本 {_currentVersion}", $"Version {_currentVersion}");
        CheckBtn.Content = L10n.Text("检查更新", "Check for Updates");
        AutoUpdateCheck.Content = L10n.Text("自动更新", "Automatic updates");
        AutoUpdateCheck.IsChecked = AppStorage.Shared.AutomaticUpdates;

        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!AllowRealClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public async Task CheckAutomaticallyAsync()
    {
        if (!AppStorage.Shared.AutomaticUpdates) return;
        try
        {
            var result = await _service.CheckForUpdateAsync();
            if (result is UpdateCheckResult.Available avail)
            {
                Dispatcher.Invoke(() =>
                {
                    PresentAvailable(avail.Release);
                    Show();
                    Activate();
                });
            }
        }
        catch
        {
        }
    }

    private async void OnCheckOrInstallClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_availableRelease is not null)
        {
            await DownloadAndLaunchAsync(_availableRelease);
            return;
        }

        _isBusy = true;
        CheckBtn.IsEnabled = false;
        CheckBtn.Content = L10n.Text("正在检查…", "Checking…");
        StatusText.Text = string.Empty;

        try
        {
            var result = await _service.CheckForUpdateAsync();
            if (result is UpdateCheckResult.UpToDate)
            {
                StatusText.Foreground = (Brush)FindResource("BrushGreen");
                StatusText.Text = L10n.Text("当前已是最新版本", "You're up to date");
                CheckBtn.Content = L10n.Text("检查更新", "Check for Updates");
            }
            else if (result is UpdateCheckResult.Available avail)
            {
                PresentAvailable(avail.Release);
            }
        }
        catch (UpdateSourceUnavailableException)
        {
            StatusText.Foreground = (Brush)FindResource("BrushAmber");
            StatusText.Text = L10n.Text("更新服务尚未上线，暂时无法检查版本。", "The update service is not available yet.");
            CheckBtn.Content = L10n.Text("检查更新", "Check for Updates");
        }
        catch
        {
            StatusText.Foreground = (Brush)FindResource("BrushRed");
            StatusText.Text = L10n.Text("无法连接更新服务器，请检查网络后重试。", "Could not reach the update server.");
            CheckBtn.Content = L10n.Text("检查更新", "Check for Updates");
        }
        finally
        {
            _isBusy = false;
            CheckBtn.IsEnabled = true;
        }
    }

    private void PresentAvailable(GitHubRelease release)
    {
        _availableRelease = release;
        StatusText.Foreground = (Brush)FindResource("BrushGreen");
        StatusText.Text = L10n.Text($"发现新版本 {release.DisplayVersion}", $"New version available: {release.DisplayVersion}");
        ReleaseNotesText.Text = !string.IsNullOrWhiteSpace(release.Body)
            ? release.Body
            : L10n.Text("本版本暂无更新说明。", "No release notes were provided.");
        ReleaseNotesBox.Visibility = Visibility.Visible;
        CheckBtn.Content = L10n.Text("立即更新", "Update Now");
    }

    private async Task DownloadAndLaunchAsync(GitHubRelease release)
    {
        _isBusy = true;
        CheckBtn.IsEnabled = false;
        CheckBtn.Content = L10n.Text("正在下载…", "Downloading…");
        try
        {
            string? path = await _service.DownloadInstallerAsync(release);
            if (!string.IsNullOrEmpty(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Hide();
            }
            else
            {
                Process.Start(new ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
            }
        }
        catch
        {
            StatusText.Foreground = (Brush)FindResource("BrushRed");
            StatusText.Text = L10n.Text("更新包下载或校验失败，请稍后重试。", "Download or SHA-256 verification failed.");
            CheckBtn.Content = L10n.Text("立即更新", "Update Now");
        }
        finally
        {
            _isBusy = false;
            CheckBtn.IsEnabled = true;
        }
    }

    private void OnAutoUpdateClick(object sender, RoutedEventArgs e) =>
        AppStorage.Shared.AutomaticUpdates = AutoUpdateCheck.IsChecked == true;
}

