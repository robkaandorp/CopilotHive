namespace CopilotHive.Dashboard;

/// <summary>
/// Broadcasts state-change notifications to dashboard subscribers.
/// </summary>
public sealed class DashboardNotifier
{
    /// <summary>
    /// Fired when dashboard state has changed and listeners should re-render.
    /// </summary>
    public event Action? OnStateChanged;

    /// <summary>
    /// Notifies subscribers that dashboard state has changed.
    /// </summary>
    public void NotifyStateChanged()
    {
        var handlers = OnStateChanged;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch { }
        }
    }
}
