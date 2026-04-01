namespace CopilotHive.Dashboard;

/// <summary>Scoped service that carries the current page title for the shared header bar.</summary>
public sealed class PageHeaderState
{
    private string _title = string.Empty;

    /// <summary>Gets the current page title.</summary>
    public string Title => _title;

    /// <summary>Gets the optional back URL rendered as a back arrow in the header.</summary>
    public string? BackUrl { get; private set; }

    /// <summary>Raised whenever the title changes so the layout can re-render.</summary>
    public event Action? OnChanged;

    /// <summary>Sets the page title and notifies subscribers. Clears the back URL.</summary>
    /// <param name="title">The new title to display.</param>
    public void SetTitle(string title)
    {
        _title = title;
        BackUrl = null;
        OnChanged?.Invoke();
    }

    /// <summary>Sets the back URL to display as an arrow in the header.</summary>
    /// <param name="url">The URL to navigate to when the back arrow is clicked.</param>
    public void SetBack(string? url)
    {
        BackUrl = url;
        OnChanged?.Invoke();
    }
}
