using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace vRemoter.Windows.SystemSupport;

public static class LaunchAtLogin
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "vRemoter";
    private const string ScheduledTaskName = @"vRemoter\vRemoterElevated";

    public static string ExecutablePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "vRemoter.exe");

    public static bool IsEnabled
    {
        get
        {
            if (AppStorage.Shared.ElevatedLaunchAtLogin && IsScheduledTaskPresent())
            {
                return true;
            }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void RepairPathIfNeeded()
    {
        if (!IsEnabled) return;
        try
        {
            if (AppStorage.Shared.ElevatedLaunchAtLogin && WindowsVersionInfo.IsElevated)
            {
                CreateOrUpdateElevatedTask(ExecutablePath);
                RemoveRegistryRun();
            }
            else if (!AppStorage.Shared.ElevatedLaunchAtLogin)
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(ValueName) is string current &&
                    !string.Equals(current.Trim('"'), ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, $"\"{ExecutablePath}\"");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Write($"[STARTUP] repair failed: {ex.Message}");
        }
    }

    public static void SetEnabled(bool enabled, bool elevatedMode)
    {
        AppStorage.Shared.ElevatedLaunchAtLogin = elevatedMode;
        if (!enabled)
        {
            RemoveRegistryRun();
            RemoveElevatedTask();
            return;
        }

        if (elevatedMode)
        {
            RemoveRegistryRun();
            CreateOrUpdateElevatedTask(ExecutablePath);
        }
        else
        {
            RemoveElevatedTask();
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(ValueName, $"\"{ExecutablePath}\"");
        }
    }

    public static bool RestartProcess(bool asAdministrator)
    {
        try
        {
            var psi = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            if (asAdministrator)
            {
                psi.Verb = "runas";
            }
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Write($"[APP] restart failed (admin={asAdministrator}): {ex.Message}");
            return false;
        }
    }

    private static void RemoveRegistryRun()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    private static bool IsScheduledTaskPresent()
    {
        try
        {
            var (exit, _) = RunSchtasks($"/Query /TN \"{ScheduledTaskName}\"");
            return exit == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CreateOrUpdateElevatedTask(string exePath)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "S-1-5-32-544";
        string escapedExe = SecurityElement.Escape(exePath) ?? exePath;
        string escapedWorkDir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{sid}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{escapedExe}</Command>
                  <WorkingDirectory>{escapedWorkDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        string tempXml = Path.Combine(Path.GetTempPath(), $"vremoter-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempXml, xml, System.Text.Encoding.Unicode);
            var (exit, output) = RunSchtasks($"/Create /TN \"{ScheduledTaskName}\" /XML \"{tempXml}\" /F");
            if (exit != 0)
            {
                throw new InvalidOperationException($"schtasks failed ({exit}): {output}");
            }
        }
        finally
        {
            try { File.Delete(tempXml); } catch { }
        }
    }

    private static void RemoveElevatedTask()
    {
        try
        {
            _ = RunSchtasks($"/Delete /TN \"{ScheduledTaskName}\" /F");
        }
        catch
        {
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start schtasks.exe");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        return (process.ExitCode, output);
    }
}

