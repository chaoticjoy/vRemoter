using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using NAudio.CoreAudioApi;
using vRemoter.Core.Localization;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Audio;

public sealed record VirtualAudioRoute(
    string DriverKind,
    string RenderDeviceId,
    string RenderFriendlyName,
    string CaptureDeviceId,
    string CaptureFriendlyName)
{
    public bool IsVbCable => DriverKind.Equals("VB-Cable", StringComparison.OrdinalIgnoreCase);
}

public static class VirtualAudioDetector
{
    public const string VbCableDownloadPage = "https://vb-audio.com/Cable/";
    public const string VbCablePackUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";

    private static readonly (string Kind, string[] RenderHints, string[] CaptureHints)[] KnownDrivers =
    [
        ("vRemoteDr", ["vRemoteDr Input", "vRemoteDr"], ["vRemoteDr Output", "vRemoteDr"]),
        ("VB-Cable", ["CABLE Input", "VB-Audio Virtual Cable"], ["CABLE Output", "VB-Audio Virtual Cable"]),
        ("VoiceMeeter", ["VoiceMeeter Input", "VoiceMeeter Aux Input"], ["VoiceMeeter Output", "VoiceMeeter Aux Output"]),
        ("VAC", ["Line 1 (Virtual Audio Cable)"], ["Line 1 (Virtual Audio Cable)"])
    ];

    public static VirtualAudioRoute? DetectRoute(string? preferredRenderHint = null)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

            if (!string.IsNullOrWhiteSpace(preferredRenderHint))
            {
                var customRender = renders.FirstOrDefault(d =>
                    d.FriendlyName.Contains(preferredRenderHint, StringComparison.OrdinalIgnoreCase));
                if (customRender is not null)
                {
                    var matchingCapture = captures.FirstOrDefault(c =>
                        c.FriendlyName.Contains(preferredRenderHint.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                        ?? captures.FirstOrDefault(IsVirtualCaptureDevice);
                    if (matchingCapture is not null)
                    {
                        return new VirtualAudioRoute(
                            "Custom",
                            customRender.ID,
                            customRender.FriendlyName,
                            matchingCapture.ID,
                            matchingCapture.FriendlyName);
                    }
                }
            }

            foreach (var (kind, renderHints, captureHints) in KnownDrivers)
            {
                var render = renders.FirstOrDefault(d => renderHints.Any(h =>
                    d.FriendlyName.Contains(h, StringComparison.OrdinalIgnoreCase)));
                var capture = captures.FirstOrDefault(d => captureHints.Any(h =>
                    d.FriendlyName.Contains(h, StringComparison.OrdinalIgnoreCase)));

                if (render is not null && capture is not null)
                {
                    return new VirtualAudioRoute(
                        kind,
                        render.ID,
                        render.FriendlyName,
                        capture.ID,
                        capture.FriendlyName);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Write($"[AUDIO] virtual route detection failed: {ex.Message}");
        }

        return null;
    }

    public static bool IsVirtualCaptureDevice(MMDevice device)
    {
        string name = device.FriendlyName;
        return name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vRemoteDr", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<(bool Launched, string Message)> InstallVbCableGuidedAsync(
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default)
    {
        if (WindowsVersionInfo.IsArm64)
        {
            return (false, L10n.Text(
                "检测到 Windows ARM64 设备。VB-Cable 仅提供 x86/x64 内核驱动，请确认官方是否有 ARM64 驱动或使用兼容虚拟声卡。",
                "Windows ARM64 detected. VB-Cable primarily ships x86/x64 kernel drivers; please check VB-Audio website for compatible drivers."));
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"vRemoter-VBCable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string zipPath = Path.Combine(tempDir, "VBCABLE_Driver_Pack.zip");

        try
        {
            statusProgress?.Report(L10n.Text("正在下载 VB-Cable 官方驱动包…", "Downloading official VB-Cable driver pack…"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("vRemoter/1.4.0");
            using var resp = await http.GetAsync(VbCablePackUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = File.Create(zipPath))
            {
                await stream.CopyToAsync(fs, ct);
            }

            statusProgress?.Report(L10n.Text("正在解压驱动包…", "Extracting driver package…"));
            string extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            string setupExeName = Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe";
            string? setupExe = Directory.EnumerateFiles(extractDir, setupExeName, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? Directory.EnumerateFiles(extractDir, "VBCABLE_Setup*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

            if (setupExe is null)
            {
                return (false, L10n.Text("未在驱动包中找到安装程序。", "Setup executable not found in downloaded archive."));
            }

            statusProgress?.Report(L10n.Text("请在 UAC 弹窗中确认安装 VB-Cable…", "Confirm the UAC prompt to install VB-Cable…"));
            var psi = new ProcessStartInfo(setupExe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setupExe) ?? extractDir
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, L10n.Text("无法启动 VB-Cable 安装程序。", "Failed to start VB-Cable setup."));
            }
            await proc.WaitForExitAsync(ct);

            var route = DetectRoute();
            if (route is not null)
            {
                return (true, L10n.Text(
                    $"虚拟麦克风已就绪：{route.CaptureFriendlyName}",
                    $"Virtual microphone ready: {route.CaptureFriendlyName}"));
            }
            return (true, L10n.Text(
                "安装程序已完成；若系统尚未刷新音频设备，请点击「刷新检测」或重启电脑。",
                "Installer exited. Click Refresh or reboot if Windows has not enumerated the new audio device yet."));
        }
        catch (OperationCanceledException)
        {
            return (false, L10n.Text("已取消安装。", "Installation cancelled."));
        }
        catch (Exception ex)
        {
            LogService.Write($"[AUDIO] VB-Cable install failed: {ex.Message}");
            return (false, L10n.Text(
                $"自动下载失败（{ex.Message}），可点击「打开官网」手动下载安装。",
                $"Download failed ({ex.Message}). You can open the official website to install manually."));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

