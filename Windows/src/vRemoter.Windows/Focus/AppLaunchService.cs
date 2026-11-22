using System.Diagnostics;
using System.IO;
using vRemoter.Core.Focus;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Focus;

public sealed class AppLaunchService
{
    private readonly InputFocusCoordinator _focusCoordinator;

    public AppLaunchService(InputFocusCoordinator focusCoordinator)
    {
        _focusCoordinator = focusCoordinator;
    }

    public void LaunchOrActivate(RemoteAppLaunchConfig config)
    {
        if (config.AutoFocusInput)
        {
            _focusCoordinator.RequestPostLaunchFocus(config.AppIdentifier);
        }

        string targetProcName = Path.GetFileNameWithoutExtension(config.AppIdentifier);
        foreach (var proc in Process.GetProcessesByName(targetProcName))
        {
            try
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    AppSwitcherSession.BringWindowToForeground(proc.MainWindowHandle);
                    LogService.Write($"[APP-LAUNCH] activated running {config.AppIdentifier} (pid={proc.Id})");
                    return;
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        try
        {
            var psi = new ProcessStartInfo(config.AppPath)
            {
                UseShellExecute = true
            };
            Process.Start(psi);
            LogService.Write($"[APP-LAUNCH] launched {config.AppPath}");
        }
        catch (Exception ex)
        {
            LogService.Write($"[APP-LAUNCH] failed to launch {config.AppPath}: {ex.Message}");
        }
    }
}

