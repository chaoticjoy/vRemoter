using vRemoter.Core.Focus;
using vRemoter.Windows.Input;

namespace vRemoter.Windows.Focus;

internal static class FocusAttemptHelper
{
    public static InputFocusResultStatus AttemptFocus(
        WinUiaFocusBackend backend,
        InputFocusStore store,
        LowLevelKeyboardGuard keyboardGuard,
        IDictionary<string, (int Pid, UiaNode Window, UiaNode Element)> sessionCache,
        FocusAppInfo app,
        InputFocusAppEntry? entry,
        long initialStamp,
        bool allowSingleFallback,
        bool allowOverrideOtherEditor,
        bool allowOverrideNonSearchEditor,
        bool updateStoreState)
    {
        backend.PrepareEnhancedUi(app);
        var window = backend.FocusedWindow(app);
        if (window is null)
        {
            if (updateStoreState) store.UpdateState(InputFocusResultStatus.Loading, app.AppIdentifier);
            return InputFocusResultStatus.Loading;
        }
        if (backend.IsModal(window))
        {
            if (updateStoreState) store.UpdateState(InputFocusResultStatus.Blocked, app.AppIdentifier);
            return InputFocusResultStatus.Blocked;
        }

        UiaNode? target = null;
        if (sessionCache.TryGetValue(app.AppIdentifier, out var cached) &&
            cached.Pid == app.ProcessId &&
            backend.AreEqual(cached.Window, window) &&
            InputFocusLocatorBuilder.IsAttached(backend, cached.Element, window))
        {
            target = cached.Element;
        }

        if (target is null && entry?.Locator is { } locator)
        {
            target = InputFocusLocatorBuilder.Resolve(backend, window, locator, app.BuildHash);
            target ??= InputFocusDiscovery.DiscoverTarget(backend, window, allowSingleFallback || allowOverrideOtherEditor);
            if (target is null)
            {
                if (updateStoreState) store.UpdateState(InputFocusResultStatus.Rebind, app.AppIdentifier);
                return InputFocusResultStatus.Rebind;
            }
        }

        if (target is null)
        {
            target = InputFocusDiscovery.DiscoverTarget(backend, window, allowSingleFallback);
        }

        if (target is null)
        {
            var missing = entry?.Locator is not null ? InputFocusResultStatus.Rebind : InputFocusResultStatus.ManualNeeded;
            if (updateStoreState) store.UpdateState(missing, app.AppIdentifier);
            return missing;
        }

        sessionCache[app.AppIdentifier] = (app.ProcessId, window, target);
        var focused = backend.FocusedElement(app);

        var status = InputFocusDiscovery.Restore(
            backend,
            window,
            target,
            focused,
            allowOverrideOtherEditor,
            allowOverrideNonSearchEditor,
            () =>
            {
                var current = backend.FrontmostApp();
                if (current is null || !string.Equals(current.AppIdentifier, app.AppIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var currWin = backend.FocusedWindow(current);
                return currWin is not null && backend.AreEqual(currWin, window);
            },
            () => keyboardGuard.InputActivityStamp != initialStamp,
            () => false);

        if (updateStoreState) store.UpdateState(status, app.AppIdentifier);
        return status;
    }
}

