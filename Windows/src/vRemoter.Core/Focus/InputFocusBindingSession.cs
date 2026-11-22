namespace vRemoter.Core.Focus;

public interface IFocusSessionBackend<TNode> : IUiTreeBackend<TNode>
    where TNode : class
{
    FocusAppInfo? FrontmostApp();
    TNode? FocusedWindow(FocusAppInfo app);
    TNode? FocusedElement(FocusAppInfo app);
}

public sealed class InputFocusBindingSession<TNode>
    where TNode : class
{
    public string AppIdentifier { get; }
    public string AppName { get; }
    public DateTimeOffset Deadline { get; }
    public FocusAppInfo? DraftApp { get; private set; }
    public TNode? DraftWindow { get; private set; }
    public TNode? DraftElement { get; private set; }

    public InputFocusBindingSession(string appIdentifier, string appName, DateTimeOffset deadline)
    {
        AppIdentifier = appIdentifier;
        AppName = appName;
        Deadline = deadline;
    }

    public InputFocusBindingState Tick(IFocusSessionBackend<TNode> backend, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        int remaining = Math.Max(0, (int)Math.Ceiling((Deadline - current).TotalSeconds));
        if (remaining <= 0)
        {
            return new InputFocusBindingState(
                AppIdentifier,
                AppName,
                DraftElement is not null,
                DraftElement is not null ? FocusCaptureStatus.Captured : FocusCaptureStatus.FocusUnavailable,
                0);
        }

        var front = backend.FrontmostApp();
        if (front is null || !string.Equals(front.AppIdentifier, AppIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return new InputFocusBindingState(
                AppIdentifier,
                AppName,
                DraftElement is not null,
                DraftElement is not null ? FocusCaptureStatus.Captured : FocusCaptureStatus.Waiting,
                remaining);
        }

        var window = backend.FocusedWindow(front);
        if (window is null)
        {
            return new InputFocusBindingState(
                AppIdentifier,
                AppName,
                false,
                FocusCaptureStatus.WindowUnavailable,
                remaining);
        }

        var target = backend.FocusedElement(front);
        if (target is null)
        {
            return new InputFocusBindingState(
                AppIdentifier,
                AppName,
                false,
                FocusCaptureStatus.FocusUnavailable,
                remaining);
        }

        string role = backend.GetRole(target) ?? string.Empty;
        string subrole = backend.GetSubrole(target) ?? string.Empty;
        bool isValidEditor = InputFocusRoles.EditableRoles.Contains(role) &&
                             !InputFocusRoles.BlockedSubroles.Contains(subrole) &&
                             backend.IsEnabled(target) &&
                             backend.IsFocusedSettable(target) &&
                             InputFocusLocatorBuilder.IsAttached(backend, target, window);

        if (isValidEditor)
        {
            DraftApp = front;
            DraftWindow = window;
            DraftElement = target;
            return new InputFocusBindingState(
                AppIdentifier,
                AppName,
                true,
                FocusCaptureStatus.Captured,
                remaining);
        }

        return new InputFocusBindingState(
            AppIdentifier,
            AppName,
            DraftElement is not null,
            DraftElement is not null ? FocusCaptureStatus.Captured : FocusCaptureStatus.NotEditor,
            remaining);
    }
}

