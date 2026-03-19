using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Converts between domain types and gRPC protobuf types. Used only at communication boundaries.
/// </summary>
public static class GrpcMapper
{
    /// <summary>Converts a <see cref="DomainTask"/> to a gRPC <see cref="TaskAssignment"/>.</summary>
    public static TaskAssignment ToGrpc(DomainTask task)
    {
        var assignment = new TaskAssignment
        {
            TaskId = task.TaskId,
            GoalId = task.GoalId,
            GoalDescription = task.GoalDescription,
            Prompt = task.Prompt,
            Role = ToGrpcRole(task.Role),
            Model = task.Model,
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
                Shared.Grpc.TaskStatus.Completed => DomainTaskStatus.Completed,
                Shared.Grpc.TaskStatus.Failed => DomainTaskStatus.Failed,
                Shared.Grpc.TaskStatus.Cancelled => DomainTaskStatus.Cancelled,
                _ => throw new InvalidOperationException($"Unknown TaskStatus: {complete.Status}"),
            },
            Output = complete.Output,
            Metrics = complete.Metrics is not null ? ToDomain(complete.Metrics) : null,
            GitStatus = complete.GitStatus is not null ? ToDomain(complete.GitStatus) : null,
        };
    }

    /// <summary>Converts a gRPC <see cref="TaskMetrics"/> to a domain <see cref="DomainTaskMetrics"/>.</summary>
    public static DomainTaskMetrics ToDomain(TaskMetrics metrics)
    {
        return new DomainTaskMetrics
        {
            Verdict = metrics.Verdict,
            BuildSuccess = metrics.BuildSuccess,
            TotalTests = metrics.TotalTests,
            PassedTests = metrics.PassedTests,
            FailedTests = metrics.FailedTests,
            CoveragePercent = metrics.CoveragePercent,
            Issues = [.. metrics.Issues],
        };
    }

    /// <summary>Converts a gRPC <see cref="GitStatus"/> to a domain <see cref="DomainGitStatus"/>.</summary>
    public static DomainGitStatus ToDomain(GitStatus status)
    {
        return new DomainGitStatus
        {
            FilesChanged = status.FilesChanged,
            Insertions = status.Insertions,
            Deletions = status.Deletions,
            Pushed = status.Pushed,
        };
    }

    /// <summary>Converts a gRPC <see cref="BranchInfo"/> to a domain <see cref="DomainBranchInfo"/>.</summary>
    public static DomainBranchInfo ToDomain(BranchInfo info)
    {
        return new DomainBranchInfo
        {
            BaseBranch = info.BaseBranch,
            FeatureBranch = info.FeatureBranch,
            Action = ToDomain(info.Action),
        };
    }

    /// <summary>Converts a domain <see cref="DomainBranchAction"/> to the gRPC equivalent.</summary>
    public static BranchAction ToGrpc(DomainBranchAction action) => action switch
    {
        DomainBranchAction.Create => BranchAction.Create,
        DomainBranchAction.Checkout => BranchAction.Checkout,
        DomainBranchAction.Merge => BranchAction.Merge,
        DomainBranchAction.Unspecified => BranchAction.Unspecified,
        _ => throw new InvalidOperationException($"Unknown DomainBranchAction: {action}"),
    };

    /// <summary>Converts a gRPC <see cref="BranchAction"/> to the domain equivalent.</summary>
    public static DomainBranchAction ToDomain(BranchAction action) => action switch
    {
        BranchAction.Create => DomainBranchAction.Create,
        BranchAction.Checkout => DomainBranchAction.Checkout,
        BranchAction.Merge => DomainBranchAction.Merge,
        BranchAction.Unspecified => DomainBranchAction.Unspecified,
        _ => throw new InvalidOperationException($"Unknown BranchAction: {action}"),
    };

    /// <summary>Converts a gRPC <see cref="TaskAssignment"/> to a domain <see cref="DomainTask"/>.</summary>
    public static DomainTask ToDomain(TaskAssignment assignment)
    {
        return new DomainTask
        {
            TaskId = assignment.TaskId,
            GoalId = assignment.GoalId,
            GoalDescription = assignment.GoalDescription,
            Prompt = assignment.Prompt,
            Role = ToDomainRole(assignment.Role),
            Model = assignment.Model,
            BranchInfo = assignment.BranchInfo is not null ? ToDomain(assignment.BranchInfo) : null,
            Repositories = [.. assignment.Repositories.Select(r => new DomainRepositoryInfo
            {
                Name = r.Name,
                Url = r.Url,
                DefaultBranch = r.DefaultBranch,
            })],
            Metadata = new Dictionary<string, string>(assignment.Metadata),
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
                DomainTaskStatus.Completed => Shared.Grpc.TaskStatus.Completed,
                DomainTaskStatus.Failed => Shared.Grpc.TaskStatus.Failed,
                DomainTaskStatus.Cancelled => Shared.Grpc.TaskStatus.Cancelled,
                _ => throw new InvalidOperationException($"Unknown DomainTaskStatus: {result.Status}"),
            },
            Output = result.Output,
        };
        if (result.Metrics is not null)
        {
            complete.Metrics = new TaskMetrics
            {
                Verdict = result.Metrics.Verdict,
                BuildSuccess = result.Metrics.BuildSuccess,
                TotalTests = result.Metrics.TotalTests,
                PassedTests = result.Metrics.PassedTests,
                FailedTests = result.Metrics.FailedTests,
                CoveragePercent = result.Metrics.CoveragePercent,
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
