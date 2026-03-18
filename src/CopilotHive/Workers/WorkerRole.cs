namespace CopilotHive.Workers;

/// <summary>
/// gRPC mapping extensions for <see cref="WorkerRole"/>.
/// These live in the main project because they bridge between the domain type
/// and the gRPC transport type — only needed at the communication boundary.
/// </summary>
public static class WorkerRoleGrpcExtensions
{
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
    /// Converts a gRPC <see cref="CopilotHive.Shared.Grpc.WorkerRole"/> to the domain type.
    /// </summary>
    public static WorkerRole ToDomainRole(this CopilotHive.Shared.Grpc.WorkerRole grpcRole) => grpcRole switch
    {
        CopilotHive.Shared.Grpc.WorkerRole.Coder => WorkerRole.Coder,
        CopilotHive.Shared.Grpc.WorkerRole.Tester => WorkerRole.Tester,
        CopilotHive.Shared.Grpc.WorkerRole.Reviewer => WorkerRole.Reviewer,
        CopilotHive.Shared.Grpc.WorkerRole.Improver => WorkerRole.Improver,
        CopilotHive.Shared.Grpc.WorkerRole.DocWriter => WorkerRole.DocWriter,
        _ => throw new InvalidOperationException($"gRPC WorkerRole '{grpcRole}' has no domain equivalent"),
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
