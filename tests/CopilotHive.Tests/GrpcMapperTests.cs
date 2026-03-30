using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using DomainBranchAction = CopilotHive.Services.BranchAction;
using DomainTaskMetrics = CopilotHive.Services.TaskMetrics;
using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcBranchAction = CopilotHive.Shared.Grpc.BranchAction;
using GrpcTaskMetrics = CopilotHive.Shared.Grpc.TaskMetrics;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for <see cref="GrpcMapper"/>, covering round-trips, enum mappings,
/// null/empty handling, and unknown-value exception behaviour.
/// </summary>
public sealed class GrpcMapperTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WorkTask BuildFullWorkTask() => new()
    {
        TaskId = "task-abc",
        GoalId = "goal-xyz",
        GoalDescription = "Build a widget",
        Prompt = "Write the code",
        Role = DomainWorkerRole.Coder,
        Model = "claude-sonnet-4.6",
        BranchInfo = new BranchSpec
        {
            BaseBranch = "main",
            FeatureBranch = "feature/widget",
            Action = DomainBranchAction.Create,
        },
        Repositories =
        [
            new TargetRepository { Name = "repo1", Url = "https://github.com/org/repo1", DefaultBranch = "main" },
            new TargetRepository { Name = "repo2", Url = "https://github.com/org/repo2", DefaultBranch = "develop" },
        ],
        Metadata = new Dictionary<string, string>
        {
            ["key1"] = "value1",
            ["key2"] = "value2",
        },
    };

    private static TaskResult BuildFullTaskResult() => new()
    {
        TaskId = "task-abc",
        Status = TaskOutcome.Completed,
        Output = "Done!",
        Metrics = new DomainTaskMetrics
        {
            Verdict = "PASS",
            BuildSuccess = true,
            TotalTests = 42,
            PassedTests = 40,
            FailedTests = 2,
            CoveragePercent = 85.5,
            Issues = ["issue-1", "issue-2"],
        },
        GitStatus = new GitChangeSummary
        {
            FilesChanged = 5,
            Insertions = 100,
            Deletions = 20,
            Pushed = true,
        },
    };

    // ── WorkTask round-trip ───────────────────────────────────────────────────

    [Fact]
    public void WorkTask_RoundTrip_AllFieldsMatch()
    {
        var original = BuildFullWorkTask();

        var assignment = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Equal(original.TaskId, restored.TaskId);
        Assert.Equal(original.GoalId, restored.GoalId);
        Assert.Equal(original.GoalDescription, restored.GoalDescription);
        Assert.Equal(original.Prompt, restored.Prompt);
        Assert.Equal(original.Role, restored.Role);
        Assert.Equal(original.Model, restored.Model);

        Assert.NotNull(restored.BranchInfo);
        Assert.Equal(original.BranchInfo!.BaseBranch, restored.BranchInfo.BaseBranch);
        Assert.Equal(original.BranchInfo.FeatureBranch, restored.BranchInfo.FeatureBranch);
        Assert.Equal(original.BranchInfo.Action, restored.BranchInfo.Action);

        Assert.Equal(2, restored.Repositories.Count);
        Assert.Equal("repo1", restored.Repositories[0].Name);
        Assert.Equal("https://github.com/org/repo1", restored.Repositories[0].Url);
        Assert.Equal("main", restored.Repositories[0].DefaultBranch);
        Assert.Equal("repo2", restored.Repositories[1].Name);
        Assert.Equal("develop", restored.Repositories[1].DefaultBranch);

        Assert.Equal(2, restored.Metadata.Count);
        Assert.Equal("value1", restored.Metadata["key1"]);
        Assert.Equal("value2", restored.Metadata["key2"]);
    }

    [Fact]
    public void WorkTask_RoundTrip_NullBranchInfo_IsPreserved()
    {
        var original = BuildFullWorkTask() with { BranchInfo = null };

        var assignment = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Null(restored.BranchInfo);
    }

    [Fact]
    public void WorkTask_RoundTrip_EmptyRepositories_IsPreserved()
    {
        var original = BuildFullWorkTask() with { Repositories = [] };

        var assignment = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Empty(restored.Repositories);
    }

    [Fact]
    public void WorkTask_RoundTrip_EmptyMetadata_IsPreserved()
    {
        var original = BuildFullWorkTask() with { Metadata = new Dictionary<string, string>() };

        var assignment = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Empty(restored.Metadata);
    }

    // ── TaskResult round-trip ─────────────────────────────────────────────────

    [Fact]
    public void TaskResult_RoundTrip_AllFieldsMatch()
    {
        var original = BuildFullTaskResult();

        var complete = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(complete);

        Assert.Equal(original.TaskId, restored.TaskId);
        Assert.Equal(original.Status, restored.Status);
        Assert.Equal(original.Output, restored.Output);

        Assert.NotNull(restored.Metrics);
        Assert.Equal(original.Metrics!.Verdict, restored.Metrics.Verdict);
        Assert.Equal(original.Metrics.BuildSuccess, restored.Metrics.BuildSuccess);
        Assert.Equal(original.Metrics.TotalTests, restored.Metrics.TotalTests);
        Assert.Equal(original.Metrics.PassedTests, restored.Metrics.PassedTests);
        Assert.Equal(original.Metrics.FailedTests, restored.Metrics.FailedTests);
        Assert.Equal(original.Metrics.CoveragePercent, restored.Metrics.CoveragePercent);
        Assert.Equal(original.Metrics.Issues, restored.Metrics.Issues);

        Assert.NotNull(restored.GitStatus);
        Assert.Equal(original.GitStatus!.FilesChanged, restored.GitStatus.FilesChanged);
        Assert.Equal(original.GitStatus.Insertions, restored.GitStatus.Insertions);
        Assert.Equal(original.GitStatus.Deletions, restored.GitStatus.Deletions);
        Assert.Equal(original.GitStatus.Pushed, restored.GitStatus.Pushed);
    }

    [Fact]
    public void TaskResult_RoundTrip_NullMetrics_IsPreserved()
    {
        var original = BuildFullTaskResult() with { Metrics = null };

        var complete = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(complete);

        Assert.Null(restored.Metrics);
    }

    [Fact]
    public void TaskResult_RoundTrip_NullGitStatus_IsPreserved()
    {
        var original = BuildFullTaskResult() with { GitStatus = null };

        var complete = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(complete);

        Assert.Null(restored.GitStatus);
    }

    [Fact]
    public void TaskResult_RoundTrip_IterationStartSha_IsPreserved()
    {
        // Arrange — TaskResult with an IterationStartSha (coder path)
        const string sha = "abc123def456789012345678901234567890abcd";
        var original = BuildFullTaskResult() with { IterationStartSha = sha };

        // Act — round-trip through gRPC mapper
        var complete = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(complete);

        // Assert — SHA survives the gRPC boundary
        Assert.Equal(sha, restored.IterationStartSha);
    }

    [Fact]
    public void TaskResult_RoundTrip_NullIterationStartSha_RestoredAsNull()
    {
        // Arrange — TaskResult without a SHA (reviewer path)
        var original = BuildFullTaskResult() with { IterationStartSha = null };

        // Act
        var complete = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(complete);

        // Assert — null SHA survives as null (not empty string)
        Assert.Null(restored.IterationStartSha);
    }

    // ── BranchAction enum ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(DomainBranchAction.Unspecified, GrpcBranchAction.Unspecified)]
    [InlineData(DomainBranchAction.Create, GrpcBranchAction.Create)]
    [InlineData(DomainBranchAction.Checkout, GrpcBranchAction.Checkout)]
    [InlineData(DomainBranchAction.Merge, GrpcBranchAction.Merge)]
    public void BranchAction_ToGrpc_MapsCorrectly(DomainBranchAction domain, GrpcBranchAction expected)
    {
        Assert.Equal(expected, GrpcMapper.ToGrpc(domain));
    }

    [Theory]
    [InlineData(GrpcBranchAction.Unspecified, DomainBranchAction.Unspecified)]
    [InlineData(GrpcBranchAction.Create, DomainBranchAction.Create)]
    [InlineData(GrpcBranchAction.Checkout, DomainBranchAction.Checkout)]
    [InlineData(GrpcBranchAction.Merge, DomainBranchAction.Merge)]
    public void BranchAction_ToDomain_MapsCorrectly(GrpcBranchAction grpc, DomainBranchAction expected)
    {
        Assert.Equal(expected, GrpcMapper.ToDomain(grpc));
    }

    [Fact]
    public void BranchAction_ToGrpc_UnknownValue_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToGrpc((DomainBranchAction)999));
    }

    [Fact]
    public void BranchAction_ToDomain_UnknownValue_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToDomain((GrpcBranchAction)999));
    }

    // ── TaskOutcome enum ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(TaskOutcome.Completed, Shared.Grpc.TaskStatus.Completed)]
    [InlineData(TaskOutcome.Failed, Shared.Grpc.TaskStatus.Failed)]
    [InlineData(TaskOutcome.Cancelled, Shared.Grpc.TaskStatus.Cancelled)]
    public void TaskOutcome_ToGrpc_MapsCorrectly(TaskOutcome domain, Shared.Grpc.TaskStatus expected)
    {
        var result = new TaskResult
        {
            TaskId = "t",
            Status = domain,
            Output = "",
        };
        var complete = GrpcMapper.ToGrpc(result);
        Assert.Equal(expected, complete.Status);
    }

    [Theory]
    [InlineData(Shared.Grpc.TaskStatus.Completed, TaskOutcome.Completed)]
    [InlineData(Shared.Grpc.TaskStatus.Failed, TaskOutcome.Failed)]
    [InlineData(Shared.Grpc.TaskStatus.Cancelled, TaskOutcome.Cancelled)]
    public void TaskOutcome_ToDomain_MapsCorrectly(Shared.Grpc.TaskStatus grpc, TaskOutcome expected)
    {
        var complete = new TaskComplete { TaskId = "t", Status = grpc, Output = "" };
        var result = GrpcMapper.ToDomain(complete);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void TaskOutcome_ToGrpc_UnknownValue_ThrowsInvalidOperationException()
    {
        var result = new TaskResult { TaskId = "t", Status = (TaskOutcome)999, Output = "" };
        Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToGrpc(result));
    }

    [Fact]
    public void TaskOutcome_ToDomain_UnknownValue_ThrowsInvalidOperationException()
    {
        var complete = new TaskComplete { TaskId = "t", Status = (Shared.Grpc.TaskStatus)999, Output = "" };
        Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToDomain(complete));
    }

    [Fact]
    public void TaskOutcome_ToDomain_Unspecified_ThrowsInvalidOperationException()
    {
        // TaskStatus.Unspecified (0) is the proto3 wire default — mapper has no mapping for it.
        var complete = new TaskComplete { TaskId = "t", Status = Shared.Grpc.TaskStatus.Unspecified, Output = "" };
        Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToDomain(complete));
    }

    [Fact]
    public void TaskOutcome_ToDomain_InProgress_ThrowsInvalidOperationException()
    {
        // TaskStatus.InProgress is a valid proto value that has no corresponding TaskOutcome.
        var complete = new TaskComplete { TaskId = "t", Status = Shared.Grpc.TaskStatus.InProgress, Output = "" };
        Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToDomain(complete));
    }

    // ── WorkerRole enum ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(DomainWorkerRole.Unspecified, GrpcWorkerRole.Unspecified)]
    [InlineData(DomainWorkerRole.Coder, GrpcWorkerRole.Coder)]
    [InlineData(DomainWorkerRole.Tester, GrpcWorkerRole.Tester)]
    [InlineData(DomainWorkerRole.Reviewer, GrpcWorkerRole.Reviewer)]
    [InlineData(DomainWorkerRole.Improver, GrpcWorkerRole.Improver)]
    [InlineData(DomainWorkerRole.DocWriter, GrpcWorkerRole.DocWriter)]
    public void WorkerRole_ToGrpcRole_MapsCorrectly(DomainWorkerRole domain, GrpcWorkerRole expected)
    {
        var task = BuildFullWorkTask() with { Role = domain };
        var assignment = GrpcMapper.ToGrpc(task);
        Assert.Equal(expected, assignment.Role);
    }

    [Theory]
    [InlineData(GrpcWorkerRole.Unspecified, DomainWorkerRole.Unspecified)]
    [InlineData(GrpcWorkerRole.Coder, DomainWorkerRole.Coder)]
    [InlineData(GrpcWorkerRole.Tester, DomainWorkerRole.Tester)]
    [InlineData(GrpcWorkerRole.Reviewer, DomainWorkerRole.Reviewer)]
    [InlineData(GrpcWorkerRole.Improver, DomainWorkerRole.Improver)]
    [InlineData(GrpcWorkerRole.DocWriter, DomainWorkerRole.DocWriter)]
    public void WorkerRole_ToDomainRole_MapsCorrectly(GrpcWorkerRole grpc, DomainWorkerRole expected)
    {
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            Role = grpc,
        };
        var task = GrpcMapper.ToDomain(assignment);
        Assert.Equal(expected, task.Role);
    }

    [Fact]
    public void WorkerRole_ToGrpcRole_UnknownValue_ThrowsInvalidOperationException()
    {
        var task = BuildFullWorkTask() with { Role = (DomainWorkerRole)999 };
        Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToGrpc(task));
    }

    [Fact]
    public void WorkerRole_ToDomainRole_UnknownValue_ThrowsInvalidOperationException()
    {
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            Role = (GrpcWorkerRole)999,
        };
        Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToDomain(assignment));
    }

    // ── Null / empty field handling ───────────────────────────────────────────

    [Fact]
    public void TaskMetrics_WithZeroTestCounts_DoesNotThrow()
    {
        var result = BuildFullTaskResult() with
        {
            Metrics = new DomainTaskMetrics
            {
                Verdict = "PASS",
                BuildSuccess = true,
                TotalTests = 0,
                PassedTests = 0,
                FailedTests = 0,
                CoveragePercent = 0.0,
                Issues = [],
            },
        };

        var complete = GrpcMapper.ToGrpc(result);
        var restored = GrpcMapper.ToDomain(complete);

        Assert.Equal(0, restored.Metrics!.TotalTests);
        Assert.Equal(0, restored.Metrics.PassedTests);
        Assert.Equal(0, restored.Metrics.FailedTests);
        Assert.Equal(0.0, restored.Metrics.CoveragePercent);
        Assert.Empty(restored.Metrics.Issues);
    }

    [Fact]
    public void TaskMetrics_WithFullCoverage_DoesNotThrow()
    {
        var result = BuildFullTaskResult() with
        {
            Metrics = new DomainTaskMetrics
            {
                Verdict = "PASS",
                BuildSuccess = true,
                TotalTests = 100,
                PassedTests = 100,
                FailedTests = 0,
                CoveragePercent = 100.0,
                Issues = [],
            },
        };

        var complete = GrpcMapper.ToGrpc(result);
        var restored = GrpcMapper.ToDomain(complete);

        Assert.Equal(100.0, restored.Metrics!.CoveragePercent);
        Assert.Equal(100, restored.Metrics.TotalTests);
        Assert.Equal(100, restored.Metrics.PassedTests);
    }

    [Fact]
    public void WorkTask_WithEmptyTaskId_DoesNotThrow()
    {
        var task = BuildFullWorkTask() with { TaskId = "" };
        var assignment = GrpcMapper.ToGrpc(task);
        var restored = GrpcMapper.ToDomain(assignment);
        Assert.Equal("", restored.TaskId);
    }

    [Fact]
    public void WorkTask_WithEmptyPrompt_DoesNotThrow()
    {
        var task = BuildFullWorkTask() with { Prompt = "" };
        var assignment = GrpcMapper.ToGrpc(task);
        var restored = GrpcMapper.ToDomain(assignment);
        Assert.Equal("", restored.Prompt);
    }

    [Fact]
    public void GitChangeSummary_AllZero_RoundTripPreservesValues()
    {
        var result = BuildFullTaskResult() with
        {
            GitStatus = new GitChangeSummary
            {
                FilesChanged = 0,
                Insertions = 0,
                Deletions = 0,
                Pushed = false,
            },
        };

        var complete = GrpcMapper.ToGrpc(result);
        var restored = GrpcMapper.ToDomain(complete);

        Assert.Equal(0, restored.GitStatus!.FilesChanged);
        Assert.Equal(0, restored.GitStatus.Insertions);
        Assert.Equal(0, restored.GitStatus.Deletions);
        Assert.False(restored.GitStatus.Pushed);
    }

    [Fact]
    public void TaskMetrics_IssuesList_RoundTripPreservesOrder()
    {
        var issues = new List<string> { "alpha", "beta", "gamma" };
        var result = BuildFullTaskResult() with
        {
            Metrics = new DomainTaskMetrics
            {
                Verdict = "FAIL",
                BuildSuccess = false,
                TotalTests = 10,
                PassedTests = 7,
                FailedTests = 3,
                CoveragePercent = 70.0,
                Issues = issues,
            },
        };

        var complete = GrpcMapper.ToGrpc(result);
        var restored = GrpcMapper.ToDomain(complete);

        Assert.Equal(issues, restored.Metrics!.Issues);
    }
}
