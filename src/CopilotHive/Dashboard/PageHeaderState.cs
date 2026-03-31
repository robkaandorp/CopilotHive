namespace CopilotHive.Dashboard;

/// <summary>Scoped service that carries the current page title for the shared header bar.</summary>
public sealed class PageHeaderState
{
    private string _title = string.Empty;

    /// <summary>Gets the current page title.</summary>
    public string Title => _title;

    /// <summary>Raised whenever the title changes so the layout can re-render.</summary>
    public event Action? OnChanged;

    /// <summary>Sets the page title and notifies subscribers.</summary>
    /// <param name="title">The new title to display.</param>
    public void SetTitle(string title)
    {
        _title = title;
        OnChanged?.Invoke();
    }
}
