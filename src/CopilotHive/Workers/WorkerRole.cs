namespace CopilotHive.Workers;

/// <summary>Role assigned to a worker container.</summary>
public enum WorkerRole
{
    /// <summary>Implements code changes to fulfil a goal.</summary>
    Coder,
    /// <summary>Runs and writes automated tests.</summary>
    Tester,
    /// <summary>Reviews code changes and produces a REVIEW_REPORT.</summary>
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
/// Extension methods and predefined role groups for <see cref="WorkerRole"/>.
/// </summary>
public static class WorkerRoleExtensions
{
    /// <summary>
    /// Converts a <see cref="WorkerRole"/> to its canonical lowercase name
    /// used in file paths, config keys, and logging (e.g. "coder", "docwriter").
    /// </summary>
    public static string ToRoleName(this WorkerRole role) => role switch
    {
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
    /// <param name="role">The role to convert.</param>
    /// <returns>The display name string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="role"/> has no defined display name.</exception>
    public static string ToDisplayName(this WorkerRole role) => role switch
    {
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
    /// Converts a domain <see cref="WorkerRole"/> to the corresponding gRPC protobuf type.
    /// </summary>
    public static CopilotHive.Shared.Grpc.WorkerRole ToGrpcRole(this WorkerRole role) => role switch
    {
        WorkerRole.Coder => CopilotHive.Shared.Grpc.WorkerRole.Coder,
        WorkerRole.Tester => CopilotHive.Shared.Grpc.WorkerRole.Tester,
        WorkerRole.Reviewer => CopilotHive.Shared.Grpc.WorkerRole.Reviewer,
        WorkerRole.Improver => CopilotHive.Shared.Grpc.WorkerRole.Improver,
        WorkerRole.DocWriter => CopilotHive.Shared.Grpc.WorkerRole.DocWriter,
        _ => throw new InvalidOperationException($"WorkerRole '{role}' has no gRPC equivalent"),
    };

    /// <summary>
    /// Converts a gRPC <see cref="CopilotHive.Shared.Grpc.WorkerRole"/> to its canonical lowercase name.
    /// Mirrors <see cref="ToRoleName(WorkerRole)"/> for use at gRPC boundaries.
    /// </summary>
    public static string ToRoleName(this CopilotHive.Shared.Grpc.WorkerRole role) => role switch
    {
        CopilotHive.Shared.Grpc.WorkerRole.Coder => "coder",
        CopilotHive.Shared.Grpc.WorkerRole.Tester => "tester",
        CopilotHive.Shared.Grpc.WorkerRole.Reviewer => "reviewer",
        CopilotHive.Shared.Grpc.WorkerRole.Improver => "improver",
        CopilotHive.Shared.Grpc.WorkerRole.DocWriter => "docwriter",
        _ => throw new InvalidOperationException($"Grpc WorkerRole '{role}' has no role name"),
    };
}

/// <summary>
/// Predefined groups of <see cref="WorkerRole"/> values used in foreach loops.
/// Prevents stringly-typed role lists and ensures consistency across the codebase.
/// </summary>
public static class WorkerRoles
{
    /// <summary>All roles that have an AGENTS.md file in the config repo.</summary>
    public static readonly WorkerRole[] AgentRoles =
        [WorkerRole.Coder, WorkerRole.Tester, WorkerRole.Reviewer, WorkerRole.Improver, WorkerRole.Orchestrator, WorkerRole.DocWriter];

    /// <summary>Roles that run as Docker workers and can receive gRPC broadcasts (excludes Orchestrator, MergeWorker).</summary>
    public static readonly WorkerRole[] BroadcastableRoles =
        [WorkerRole.Coder, WorkerRole.Tester, WorkerRole.Reviewer, WorkerRole.Improver, WorkerRole.DocWriter];

    /// <summary>Roles whose telemetry is aggregated for the improver.</summary>
    public static readonly WorkerRole[] TelemetryRoles =
        [WorkerRole.Coder, WorkerRole.Reviewer, WorkerRole.Tester];

    /// <summary>Roles that the improvement analyzer can produce recommendations for.</summary>
    public static readonly WorkerRole[] ImprovableRoles = AgentRoles;
}
