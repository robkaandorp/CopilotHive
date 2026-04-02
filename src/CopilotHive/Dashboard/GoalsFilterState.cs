namespace CopilotHive.Dashboard;

/// <summary>
/// Holds the Goals page filter state so it persists across Blazor Server navigation.
/// Registered as a scoped service so each circuit (user session) has its own state.
/// </summary>
public sealed class GoalsFilterState
{
    /// <summary>Free-text search string matched against goal ID, description, and failure reason.</summary>
    public string SearchText { get; set; } = "";
    /// <summary>Selected goal status filter (empty means no filter).</summary>
    public string StatusFilter { get; set; } = "";
    /// <summary>Selected goal priority filter (empty means no filter).</summary>
    public string PriorityFilter { get; set; } = "";
    /// <summary>Selected repository name filter (empty means no filter).</summary>
    public string RepoFilter { get; set; } = "";
    /// <summary>Selected release filter: ReleaseFilterUnreleased, ReleaseFilterAll, or a specific release ID.</summary>
    public string ReleaseFilter { get; set; } = ReleaseFilterUnreleased;

    /// <summary>Internal sentinel value meaning "show only unreleased goals".</summary>
    public const string ReleaseFilterUnreleased = "__unreleased__";

    /// <summary>Internal sentinel value meaning "show goals from all releases".</summary>
    public const string ReleaseFilterAll = "";

    /// <summary>True when all filter properties are at their default values.</summary>
    public bool IsDefault =>
        SearchText == "" &&
        StatusFilter == "" &&
        PriorityFilter == "" &&
        RepoFilter == "" &&
        ReleaseFilter == ReleaseFilterUnreleased;

    /// <summary>Resets all filter properties to their default values.</summary>
    public void Reset()
    {
        SearchText = "";
        StatusFilter = "";
        PriorityFilter = "";
        RepoFilter = "";
        ReleaseFilter = ReleaseFilterUnreleased;
    }
}
