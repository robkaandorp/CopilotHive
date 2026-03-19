namespace CopilotHive.Workers;

/// <summary>Role assigned to a worker container.</summary>
public enum WorkerRole
{
    /// <summary>No role assigned yet. Used for idle workers before task assignment.</summary>
    Unspecified,
    /// <summary>Implements code changes to fulfil a goal.</summary>
    Coder,
    /// <summary>Runs and writes automated tests.</summary>
    Tester,
    /// <summary>Reviews code changes and produces a review verdict.</summary>
    Reviewer,
    /// <summary>Improves AGENTS.md files based on iteration feedback.</summary>
    Improver,
    /// <summary>LLM-powered orchestrator that plans and directs the other workers.</summary>
    Orchestrator,
    /// <summary>Writes or updates documentation.</summary>
    DocWriter,
    /// <summary>Merges feature branches into the main branch.</summary>
    MergeWorker,
}

/// <summary>
/// Extension methods for <see cref="WorkerRole"/>.
/// </summary>
public static class WorkerRoleExtensions
{
    /// <summary>
    /// Converts a <see cref="WorkerRole"/> to its canonical lowercase name
    /// used in file paths, config keys, and logging (e.g. "coder", "docwriter").
    /// </summary>
    public static string ToRoleName(this WorkerRole role) => role switch
    {
        WorkerRole.Unspecified => "unspecified",
        WorkerRole.Coder => "coder",
        WorkerRole.Tester => "tester",
        WorkerRole.Reviewer => "reviewer",
        WorkerRole.Improver => "improver",
        WorkerRole.Orchestrator => "orchestrator",
        WorkerRole.DocWriter => "docwriter",
        WorkerRole.MergeWorker => "mergeworker",
        _ => throw new InvalidOperationException($"Unhandled WorkerRole: {role}"),
    };

    /// <summary>
    /// Returns a human-readable display name for the given <see cref="WorkerRole"/>
    /// (e.g. "Doc Writer" for <see cref="WorkerRole.DocWriter"/>).
    /// </summary>
    public static string ToDisplayName(this WorkerRole role) => role switch
    {
        WorkerRole.Unspecified  => "Unspecified",
        WorkerRole.Coder        => "Coder",
        WorkerRole.Tester       => "Tester",
        WorkerRole.Reviewer     => "Reviewer",
        WorkerRole.Improver     => "Improver",
        WorkerRole.Orchestrator => "Orchestrator",
        WorkerRole.DocWriter    => "Doc Writer",
        WorkerRole.MergeWorker  => "Merge Worker",
        _ => throw new InvalidOperationException($"No display name defined for WorkerRole '{role}'.")
    };

    /// <summary>
    /// Parses a role name string into the <see cref="WorkerRole"/> enum.
    /// </summary>
    /// <returns>The parsed role, or <c>null</c> if the string doesn't match any known role.</returns>
    public static WorkerRole? ParseRole(string? role) => role?.ToLowerInvariant() switch
    {
        "coder" => WorkerRole.Coder,
        "tester" => WorkerRole.Tester,
        "reviewer" => WorkerRole.Reviewer,
        "improver" => WorkerRole.Improver,
        "orchestrator" => WorkerRole.Orchestrator,
        "docwriter" or "doc_writer" => WorkerRole.DocWriter,
        "mergeworker" or "merge_worker" => WorkerRole.MergeWorker,
        _ => null,
    };
}
