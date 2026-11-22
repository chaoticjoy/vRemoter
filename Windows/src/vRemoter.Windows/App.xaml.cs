using System.Windows;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\vRemoter.Community.SingleInstance";
    private const string ShowConsoleEventName = @"Local\vRemoter.Community.ShowConsole";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _waitHandle;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowConsoleEventName);
                existing.Set();
            }
            catch { }
            Shutdown(0);
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowConsoleEventName);
        _waitHandle = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _controller?.ShowConsole()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Write($"[FATAL] Dispatcher exception: {args.Exception}");
            args.Handled = true;
        };

        _controller = new AppController();
        _controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _waitHandle?.Unregister(null);
        _showEvent?.Dispose();
        _controller?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

