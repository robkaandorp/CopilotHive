using CopilotHive.Goals;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using GrpcBranchAction = CopilotHive.Shared.Grpc.BranchAction;
using GrpcTaskMetrics = CopilotHive.Shared.Grpc.TaskMetrics;

namespace CopilotHive.Services;

/// <summary>
/// Converts between domain types and gRPC protobuf types. Used only at communication boundaries.
/// </summary>
public static class GrpcMapper
{
    /// <summary>Converts a <see cref="WorkTask"/> to a gRPC <see cref="TaskAssignment"/>.</summary>
    public static TaskAssignment ToGrpc(WorkTask task)
    {
        var assignment = new TaskAssignment
        {
            TaskId = task.TaskId,
            GoalId = task.GoalId,
            GoalDescription = task.GoalDescription,
            Prompt = task.Prompt,
            Role = ToGrpcRole(task.Role),
            Model = task.Model,
            SessionId = task.SessionId,
            MaxContextTokens = task.MaxContextTokens,
        };
        foreach (var repo in task.Repositories)
        {
            assignment.Repositories.Add(new RepositoryInfo
            {
                Name = repo.Name,
                Url = repo.Url,
                DefaultBranch = repo.DefaultBranch,
            });
        }
        if (task.BranchInfo is not null)
        {
            assignment.BranchInfo = new BranchInfo
            {
                BaseBranch = task.BranchInfo.BaseBranch,
                FeatureBranch = task.BranchInfo.FeatureBranch,
                Action = ToGrpc(task.BranchInfo.Action),
            };
        }
        foreach (var (key, value) in task.Metadata)
        {
            assignment.Metadata[key] = value;
        }
        return assignment;
    }

    /// <summary>Converts a gRPC <see cref="TaskComplete"/> to a domain <see cref="TaskResult"/>.</summary>
    public static TaskResult ToDomain(TaskComplete complete)
    {
        return new TaskResult
        {
            TaskId = complete.TaskId,
            Status = complete.Status switch
            {
                Shared.Grpc.TaskStatus.Completed => TaskOutcome.Completed,
                Shared.Grpc.TaskStatus.Failed => TaskOutcome.Failed,
                Shared.Grpc.TaskStatus.Cancelled => TaskOutcome.Cancelled,
                _ => throw new InvalidOperationException($"Unknown TaskStatus: {complete.Status}"),
            },
            Output = complete.Output,
            Metrics = complete.Metrics is not null ? ToDomain(complete.Metrics) : null,
            GitStatus = complete.GitStatus is not null ? ToDomain(complete.GitStatus) : null,
            IterationStartSha = string.IsNullOrEmpty(complete.IterationStartSha) ? null : complete.IterationStartSha,
        };
    }

    /// <summary>Converts a gRPC <see cref="GrpcTaskMetrics"/> to a domain <see cref="TaskMetrics"/>.</summary>
    public static TaskMetrics ToDomain(GrpcTaskMetrics metrics)
    {
        return new TaskMetrics
        {
            Verdict = metrics.Verdict,
            BuildSuccess = metrics.BuildSuccess,
            TotalTests = metrics.TotalTests,
            PassedTests = metrics.PassedTests,
            FailedTests = metrics.FailedTests,
            CoveragePercent = metrics.CoveragePercent,
            Issues = [.. metrics.Issues],
            Summary = metrics.Summary,
        };
    }

    /// <summary>Converts a gRPC <see cref="GitStatus"/> to a domain <see cref="GitChangeSummary"/>.</summary>
    public static GitChangeSummary ToDomain(GitStatus status)
    {
        return new GitChangeSummary
        {
            FilesChanged = status.FilesChanged,
            Insertions = status.Insertions,
            Deletions = status.Deletions,
            Pushed = status.Pushed,
        };
    }

    /// <summary>Converts a gRPC <see cref="BranchInfo"/> to a domain <see cref="BranchSpec"/>.</summary>
    public static BranchSpec ToDomain(BranchInfo info)
    {
        return new BranchSpec
        {
            BaseBranch = info.BaseBranch,
            FeatureBranch = info.FeatureBranch,
            Action = ToDomain(info.Action),
        };
    }

    /// <summary>Converts a domain <see cref="BranchAction"/> to the gRPC equivalent.</summary>
    public static GrpcBranchAction ToGrpc(BranchAction action) => action switch
    {
        BranchAction.Create => GrpcBranchAction.Create,
        BranchAction.Checkout => GrpcBranchAction.Checkout,
        BranchAction.Merge => GrpcBranchAction.Merge,
        BranchAction.Unspecified => GrpcBranchAction.Unspecified,
        _ => throw new InvalidOperationException($"Unknown BranchAction: {action}"),
    };

    /// <summary>Converts a gRPC <see cref="GrpcBranchAction"/> to the domain equivalent.</summary>
    public static BranchAction ToDomain(GrpcBranchAction action) => action switch
    {
        GrpcBranchAction.Create => BranchAction.Create,
        GrpcBranchAction.Checkout => BranchAction.Checkout,
        GrpcBranchAction.Merge => BranchAction.Merge,
        GrpcBranchAction.Unspecified => BranchAction.Unspecified,
        _ => throw new InvalidOperationException($"Unknown BranchAction: {action}"),
    };

    /// <summary>Converts a gRPC <see cref="TaskAssignment"/> to a domain <see cref="WorkTask"/>.</summary>
    public static WorkTask ToDomain(TaskAssignment assignment)
    {
        return new WorkTask
        {
            TaskId = assignment.TaskId,
            GoalId = assignment.GoalId,
            GoalDescription = assignment.GoalDescription,
            Prompt = assignment.Prompt,
            Role = ToDomainRole(assignment.Role),
            Model = assignment.Model,
            SessionId = assignment.SessionId,
            BranchInfo = assignment.BranchInfo is not null ? ToDomain(assignment.BranchInfo) : null,
            Repositories = [.. assignment.Repositories.Select(r => new TargetRepository
            {
                Name = r.Name,
                Url = r.Url,
                DefaultBranch = r.DefaultBranch,
            })],
            Metadata = new Dictionary<string, string>(assignment.Metadata),
            MaxContextTokens = assignment.MaxContextTokens > 0 ? assignment.MaxContextTokens : SharedConstants.DefaultBrainContextWindow,
        };
    }

    /// <summary>Converts a domain <see cref="TaskResult"/> to a gRPC <see cref="TaskComplete"/>.</summary>
    public static TaskComplete ToGrpc(TaskResult result)
    {
        var complete = new TaskComplete
        {
            TaskId = result.TaskId,
            Status = result.Status switch
            {
                TaskOutcome.Completed => Shared.Grpc.TaskStatus.Completed,
                TaskOutcome.Failed => Shared.Grpc.TaskStatus.Failed,
                TaskOutcome.Cancelled => Shared.Grpc.TaskStatus.Cancelled,
                _ => throw new InvalidOperationException($"Unknown TaskOutcome: {result.Status}"),
            },
            Output = result.Output,
            IterationStartSha = result.IterationStartSha ?? "",
        };
        if (result.Metrics is not null)
        {
            complete.Metrics = new GrpcTaskMetrics
            {
                Verdict = result.Metrics.Verdict,
                BuildSuccess = result.Metrics.BuildSuccess,
                TotalTests = result.Metrics.TotalTests,
                PassedTests = result.Metrics.PassedTests,
                FailedTests = result.Metrics.FailedTests,
                CoveragePercent = result.Metrics.CoveragePercent,
                Summary = result.Metrics.Summary,
            };
            complete.Metrics.Issues.AddRange(result.Metrics.Issues);
        }
        if (result.GitStatus is not null)
        {
            complete.GitStatus = new GitStatus
            {
                FilesChanged = result.GitStatus.FilesChanged,
                Insertions = result.GitStatus.Insertions,
                Deletions = result.GitStatus.Deletions,
                Pushed = result.GitStatus.Pushed,
            };
        }
        return complete;
    }

    private static Shared.Grpc.WorkerRole ToGrpcRole(Workers.WorkerRole role) => role switch
    {
        Workers.WorkerRole.Unspecified => Shared.Grpc.WorkerRole.Unspecified,
        Workers.WorkerRole.Coder => Shared.Grpc.WorkerRole.Coder,
        Workers.WorkerRole.Tester => Shared.Grpc.WorkerRole.Tester,
        Workers.WorkerRole.Reviewer => Shared.Grpc.WorkerRole.Reviewer,
        Workers.WorkerRole.Improver => Shared.Grpc.WorkerRole.Improver,
        Workers.WorkerRole.DocWriter => Shared.Grpc.WorkerRole.DocWriter,
        _ => throw new InvalidOperationException($"WorkerRole '{role}' has no gRPC equivalent"),
    };

    private static Workers.WorkerRole ToDomainRole(Shared.Grpc.WorkerRole grpcRole) => grpcRole switch
    {
        Shared.Grpc.WorkerRole.Unspecified => Workers.WorkerRole.Unspecified,
        Shared.Grpc.WorkerRole.Coder => Workers.WorkerRole.Coder,
        Shared.Grpc.WorkerRole.Tester => Workers.WorkerRole.Tester,
        Shared.Grpc.WorkerRole.Reviewer => Workers.WorkerRole.Reviewer,
        Shared.Grpc.WorkerRole.Improver => Workers.WorkerRole.Improver,
        Shared.Grpc.WorkerRole.DocWriter => Workers.WorkerRole.DocWriter,
        _ => throw new InvalidOperationException($"gRPC WorkerRole '{grpcRole}' has no domain equivalent"),
    };
}
