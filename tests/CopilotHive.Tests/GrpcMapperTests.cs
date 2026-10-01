using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Google.Protobuf;

using Microsoft.Extensions.AI;

using DomainBranchAction = CopilotHive.Services.BranchAction;
using DomainTaskMetrics = CopilotHive.Services.TaskMetrics;
using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcBranchAction = CopilotHive.Shared.Grpc.BranchAction;
using GrpcTaskMetrics = CopilotHive.Shared.Grpc.TaskMetrics;
using GrpcUsageSourceKind = CopilotHive.Shared.Grpc.UsageSourceKind;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;
using SharpEstimatedTokenBreakdown = SharpCoder.EstimatedTokenBreakdown;
using SharpTokenUsage = SharpCoder.TokenUsage;
using SharpUsageEntry = SharpCoder.UsageEntry;
using SharpUsageSource = SharpCoder.UsageSource;
using SharpUsageSummary = SharpCoder.UsageSummary;

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

    // ── GitChangeSummary.ChangedFiles ─────────────────────────────────────────

    /// <summary>
    /// The changed-file path list must survive a full domain → gRPC → domain round-trip,
    /// preserving order and the real repository-relative paths (not basenames).
    /// </summary>
    [Fact]
    public void GitChangeSummary_ChangedFiles_RoundTripPreservesPathsAndOrder()
    {
        List<string> paths =
        [
            "src/Services/Foo.cs",
            "src/CopilotHive.Worker/GitOperations.cs",
            "tests/CopilotHive.Tests/GrpcMapperTests.cs",
        ];

        var result = BuildFullTaskResult() with
        {
            GitStatus = new GitChangeSummary
            {
                FilesChanged = 3,
                Insertions = 12,
                Deletions = 4,
                Pushed = false,
                ChangedFiles = paths,
            },
        };

        var complete = GrpcMapper.ToGrpc(result);

        // gRPC side carries the same repeated field content
        Assert.Equal(paths, complete.GitStatus.ChangedFiles);

        var restored = GrpcMapper.ToDomain(complete);

        Assert.NotNull(restored.GitStatus);
        Assert.Equal(paths, restored.GitStatus!.ChangedFiles);
        // Count fields still map correctly alongside the new list
        Assert.Equal(3, restored.GitStatus.FilesChanged);
        Assert.Equal(12, restored.GitStatus.Insertions);
        Assert.Equal(4, restored.GitStatus.Deletions);
        Assert.False(restored.GitStatus.Pushed);
    }

    /// <summary>
    /// An empty changed-file list round-trips as a NON-NULL empty list on the domain side.
    /// </summary>
    [Fact]
    public void GitChangeSummary_EmptyChangedFiles_RoundTripsAsNonNullEmptyList()
    {
        var result = BuildFullTaskResult() with
        {
            GitStatus = new GitChangeSummary
            {
                FilesChanged = 0,
                Insertions = 0,
                Deletions = 0,
                Pushed = true,
                ChangedFiles = [],
            },
        };

        var complete = GrpcMapper.ToGrpc(result);
        Assert.Empty(complete.GitStatus.ChangedFiles);

        var restored = GrpcMapper.ToDomain(complete);

        Assert.NotNull(restored.GitStatus);
        Assert.NotNull(restored.GitStatus!.ChangedFiles);
        Assert.Empty(restored.GitStatus.ChangedFiles);
        Assert.True(restored.GitStatus.Pushed);
    }

    /// <summary>
    /// A gRPC <see cref="GitStatus"/> built without ever touching <c>changed_files</c>
    /// maps to a non-null empty domain list.
    /// </summary>
    [Fact]
    public void GitStatus_ToDomain_WithoutChangedFiles_YieldsEmptyList()
    {
        var status = new GitStatus
        {
            FilesChanged = 7,
            Insertions = 1,
            Deletions = 2,
            Pushed = false,
        };

        var domain = GrpcMapper.ToDomain(status);

        Assert.NotNull(domain.ChangedFiles);
        Assert.Empty(domain.ChangedFiles);
        Assert.Equal(7, domain.FilesChanged);
        Assert.Equal(1, domain.Insertions);
        Assert.Equal(2, domain.Deletions);
        Assert.False(domain.Pushed);
    }

    /// <summary>
    /// Repository-qualified paths (multi-repo aggregation form) survive the round-trip verbatim.
    /// </summary>
    [Fact]
    public void GitChangeSummary_RepoQualifiedChangedFiles_RoundTripVerbatim()
    {
        List<string> paths = ["repoA:src/A.cs", "repoB:tests/B.cs"];

        var result = BuildFullTaskResult() with
        {
            GitStatus = new GitChangeSummary
            {
                FilesChanged = 2,
                Insertions = 5,
                Deletions = 1,
                Pushed = false,
                ChangedFiles = paths,
            },
        };

        var restored = GrpcMapper.ToDomain(GrpcMapper.ToGrpc(result));

        Assert.Equal(paths, restored.GitStatus!.ChangedFiles);
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

    [Fact]
    public void TaskResult_WithMetricsSummary_RoundTripsThroughGrpcMapper()
    {
        // Arrange
        var original = new TaskResult
        {
            TaskId = "task-1",
            Status = TaskOutcome.Completed,
            Output = "some output",
            Metrics = new DomainTaskMetrics
            {
                Verdict = "PASS",
                BuildSuccess = true,
                TotalTests = 10,
                PassedTests = 10,
                FailedTests = 0,
                CoveragePercent = 85.0,
                Issues = [],
                Summary = "Coder implemented feature X; all tests passed",
            },
        };

        // Act
        var grpc = GrpcMapper.ToGrpc(original);
        var roundTripped = GrpcMapper.ToDomain(grpc);

        // Assert
        Assert.Equal(original.Metrics!.Summary, roundTripped.Metrics!.Summary);
    }

    // ── SubAgentModels round-trip ───────────────────────────────────────────

    [Fact]
    public void SubAgentModels_RoundTrip_PreservesEntriesAndContextWindowBoundary()
    {
        var original = BuildFullWorkTask() with
        {
            SubAgentModels =
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000, Description = "Big model" },
                new SubAgentModelDto { Id = "model-b", ContextWindow = null, Description = "Unknown ctx" },
            ],
        };

        var assignment = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Equal(2, restored.SubAgentModels.Count);
        Assert.Equal("model-a", restored.SubAgentModels[0].Id);
        Assert.Equal(200_000, restored.SubAgentModels[0].ContextWindow);
        Assert.Equal("Big model", restored.SubAgentModels[0].Description);
        Assert.Equal("model-b", restored.SubAgentModels[1].Id);
        Assert.Null(restored.SubAgentModels[1].ContextWindow);
        Assert.Equal("Unknown ctx", restored.SubAgentModels[1].Description);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void SubAgentModels_ToDomain_ContextWindowNonPositive_MapsToNull(int protoContextWindow)
    {
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            Role = GrpcWorkerRole.Coder,
        };
        assignment.SubAgentModels.Add(new SubAgentModel
        {
            Id = "model-x",
            ContextWindow = protoContextWindow,
            Description = "test",
        });

        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Single(restored.SubAgentModels);
        Assert.Equal("model-x", restored.SubAgentModels[0].Id);
        Assert.Null(restored.SubAgentModels[0].ContextWindow);
    }

    [Fact]
    public void SubAgentModels_ToGrpc_NullContextWindow_EncodesAsZero()
    {
        var original = BuildFullWorkTask() with
        {
            SubAgentModels =
            [
                new SubAgentModelDto { Id = "model-null", ContextWindow = null },
            ],
        };

        var assignment = GrpcMapper.ToGrpc(original);

        Assert.Single(assignment.SubAgentModels);
        Assert.Equal("model-null", assignment.SubAgentModels[0].Id);
        Assert.Equal(0, assignment.SubAgentModels[0].ContextWindow);
    }

    [Fact]
    public void SubAgentModels_NullToZero_RoundTripsBackToNull()
    {
        var original = BuildFullWorkTask() with
        {
            SubAgentModels =
            [
                new SubAgentModelDto { Id = "model-null", ContextWindow = null },
            ],
        };

        var assignment = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Single(restored.SubAgentModels);
        Assert.Null(restored.SubAgentModels[0].ContextWindow);
    }

    /// <summary>
    /// A non-positive domain <c>ContextWindow</c> (zero or negative) must be encoded as
    /// exactly 0 on the proto side — negative values must never travel over the wire.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void SubAgentModels_ToGrpc_NonPositiveContextWindow_EncodesAsZero(int domainContextWindow)
    {
        var original = BuildFullWorkTask() with
        {
            SubAgentModels =
            [
                new SubAgentModelDto { Id = "model-np", ContextWindow = domainContextWindow },
            ],
        };

        var assignment = GrpcMapper.ToGrpc(original);

        Assert.Single(assignment.SubAgentModels);
        Assert.Equal("model-np", assignment.SubAgentModels[0].Id);
        Assert.Equal(0, assignment.SubAgentModels[0].ContextWindow);
    }

    /// <summary>
    /// A non-positive domain <c>ContextWindow</c> must survive a full round-trip as
    /// <c>null</c> on the domain side (encoded 0 outbound, decoded null inbound).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void SubAgentModels_NonPositiveContextWindow_RoundTripsBackToNull(int domainContextWindow)
    {
        var original = BuildFullWorkTask() with
        {
            SubAgentModels =
            [
                new SubAgentModelDto { Id = "model-np", ContextWindow = domainContextWindow },
            ],
        };

        var assignment = GrpcMapper.ToGrpc(original);
        Assert.Equal(0, assignment.SubAgentModels[0].ContextWindow);

        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Single(restored.SubAgentModels);
        Assert.Equal("model-np", restored.SubAgentModels[0].Id);
        Assert.Null(restored.SubAgentModels[0].ContextWindow);
    }

    // ── SubAgentModels blank-name filtering ─────────────────────────────────

    [Fact]
    public void SubAgentModels_ToGrpc_FiltersBlankAndWhitespaceNames()
    {
        var original = BuildFullWorkTask() with
        {
            SubAgentModels =
            [
                new SubAgentModelDto { Id = "", ContextWindow = 1000 },
                new SubAgentModelDto { Id = "   ", ContextWindow = 2000 },
                new SubAgentModelDto { Id = "valid-model", ContextWindow = 3000 },
            ],
        };

        var assignment = GrpcMapper.ToGrpc(original);

        Assert.Single(assignment.SubAgentModels);
        Assert.Equal("valid-model", assignment.SubAgentModels[0].Id);
        Assert.Equal(3000, assignment.SubAgentModels[0].ContextWindow);
    }

    [Fact]
    public void SubAgentModels_ToDomain_FiltersBlankAndWhitespaceNames()
    {
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            Role = GrpcWorkerRole.Coder,
        };
        assignment.SubAgentModels.Add(new SubAgentModel { Id = "", ContextWindow = 1000 });
        assignment.SubAgentModels.Add(new SubAgentModel { Id = "   ", ContextWindow = 2000 });
        assignment.SubAgentModels.Add(new SubAgentModel { Id = "valid-model", ContextWindow = 3000 });

        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Single(restored.SubAgentModels);
        Assert.Equal("valid-model", restored.SubAgentModels[0].Id);
    }

    [Fact]
    public void SubAgentModels_EmptyCatalog_RoundTripsToEmpty()
    {
        var original = BuildFullWorkTask() with { SubAgentModels = [] };

        var assignment = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Empty(assignment.SubAgentModels);
        Assert.Empty(restored.SubAgentModels);
    }

    // ── SubAgentModels SupportsVision round-trip (non-nullable bool) ──────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SubAgentModels_SupportsVision_RoundTripsBothDirections(bool vision)
    {
        var original = BuildFullWorkTask() with
        {
            SubAgentModels =
            [
                new SubAgentModelDto { Id = "vision-model", ContextWindow = 200_000, SupportsVision = vision },
            ],
        };

        var assignment = GrpcMapper.ToGrpc(original);
        Assert.Equal(vision, assignment.SubAgentModels[0].SupportsVision);

        var restored = GrpcMapper.ToDomain(assignment);
        Assert.Equal(vision, restored.SubAgentModels[0].SupportsVision);
    }

    [Fact]
    public void SubAgentModels_SupportsVision_DefaultsToFalseOnDto()
    {
        var dto = new SubAgentModelDto { Id = "test" };
        Assert.False(dto.SupportsVision);
    }

    [Fact]
    public void SubAgentModels_SupportsVision_UnsetProtoMessage_DecodesAsFalse()
    {
        // A proto SubAgentModel with supports_vision unset (proto3 default = false)
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            Role = GrpcWorkerRole.Coder,
        };
        assignment.SubAgentModels.Add(new SubAgentModel
        {
            Id = "default-vision",
            ContextWindow = 1000,
            Description = "test",
            // SupportsVision not set — proto3 default is false
        });

        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Single(restored.SubAgentModels);
        Assert.False(restored.SubAgentModels[0].SupportsVision);
    }

    // ── reasoning_effort mapping ──────────────────────────────────────────────

    [Fact]
    public void ToGrpc_WithReasoningEffort_MapsToLowercaseString()
    {
        var task = BuildFullWorkTask() with { ReasoningEffort = ReasoningEffort.High };

        var assignment = GrpcMapper.ToGrpc(task);

        Assert.Equal("high", assignment.ReasoningEffort);
    }

    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "extra_high")]
    public void ToGrpc_AllReasoningEfforts_MapToCanonicalStrings(ReasoningEffort effort, string expected)
    {
        var task = BuildFullWorkTask() with { ReasoningEffort = effort };

        var assignment = GrpcMapper.ToGrpc(task);

        Assert.Equal(expected, assignment.ReasoningEffort);
    }

    /// <summary>
    /// Proto3 has no null string — an unset reasoning effort must serialize as the empty string,
    /// never as a null that would throw when assigned to the generated message property.
    /// </summary>
    [Fact]
    public void ToGrpc_WithNullReasoningEffort_MapsToEmptyString()
    {
        var task = BuildFullWorkTask();
        Assert.Null(task.ReasoningEffort);

        var assignment = GrpcMapper.ToGrpc(task);

        Assert.Equal("", assignment.ReasoningEffort);
    }

    [Fact]
    public void ToDomain_WithEmptyReasoningEffort_MapsToNull()
    {
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            Role = GrpcWorkerRole.Coder,
            ReasoningEffort = "",
        };

        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Null(restored.ReasoningEffort);
    }

    /// <summary>
    /// A TaskAssignment that never sets reasoning_effort (proto3 default "") must decode as null.
    /// </summary>
    [Fact]
    public void ToDomain_WithUnsetReasoningEffort_MapsToNull()
    {
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            Role = GrpcWorkerRole.Coder,
        };

        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Null(restored.ReasoningEffort);
    }

    [Fact]
    public void ToDomain_WithExtraHigh_MapsToEnum()
    {
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            Role = GrpcWorkerRole.Coder,
            ReasoningEffort = "extra_high",
        };

        var restored = GrpcMapper.ToDomain(assignment);

        Assert.Equal(ReasoningEffort.ExtraHigh, restored.ReasoningEffort);
    }

    [Theory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.ExtraHigh)]
    public void ReasoningEffort_RoundTrip_Preserved(ReasoningEffort effort)
    {
        var original = BuildFullWorkTask() with { ReasoningEffort = effort };

        var restored = GrpcMapper.ToDomain(GrpcMapper.ToGrpc(original));

        Assert.Equal(effort, restored.ReasoningEffort);
    }

    [Fact]
    public void ReasoningEffort_NullRoundTrip_StaysNull()
    {
        var original = BuildFullWorkTask();

        var restored = GrpcMapper.ToDomain(GrpcMapper.ToGrpc(original));

        Assert.Null(restored.ReasoningEffort);
    }

    // ── TaskComplete.model (field 7) ──────────────────────────────────────────
    //
    // These tests operate on ACTUAL protobuf bytes: every case serializes the produced
    // TaskComplete and re-parses it with the generated parser, so presence (HasModel) is
    // asserted on the decoded message rather than on the in-memory object the mapper built.
    // A mapper that merely assigned a local property without the presence bit would fail here.

    /// <summary>
    /// The wire number is part of the cross-process contract: field 7 must not be renumbered,
    /// and it must not collide with any of the pre-existing completion fields. Old/new
    /// compatibility derives from additive optional protobuf semantics — not a handshake.
    /// <para>
    /// Field 8 (<c>usage</c>) is asserted to be exactly 8 and to be an ADDITION: fields 1–7 keep
    /// their numbers, so an old server still reads the pre-existing fields and simply ignores
    /// the usage it does not know.
    /// </para>
    /// </summary>
    [Fact]
    public void TaskComplete_Model_UsesWireFieldNumberSevenWithoutRenumberingExistingFields()
    {
        Assert.Equal(7, TaskComplete.ModelFieldNumber);

        int[] existing =
        [
            TaskComplete.TaskIdFieldNumber,
            TaskComplete.StatusFieldNumber,
            TaskComplete.OutputFieldNumber,
            TaskComplete.GitStatusFieldNumber,
            TaskComplete.MetricsFieldNumber,
            TaskComplete.IterationStartShaFieldNumber,
        ];

        Assert.Equal([1, 2, 3, 4, 5, 6], existing);
        Assert.DoesNotContain(TaskComplete.ModelFieldNumber, existing);

        Assert.Equal(8, TaskComplete.UsageFieldNumber);

        int[] fieldsOneToSeven =
        [
            TaskComplete.TaskIdFieldNumber,
            TaskComplete.StatusFieldNumber,
            TaskComplete.OutputFieldNumber,
            TaskComplete.GitStatusFieldNumber,
            TaskComplete.MetricsFieldNumber,
            TaskComplete.IterationStartShaFieldNumber,
            TaskComplete.ModelFieldNumber,
        ];

        Assert.Equal([1, 2, 3, 4, 5, 6, 7], fieldsOneToSeven);
        Assert.DoesNotContain(TaskComplete.UsageFieldNumber, fieldsOneToSeven);
    }

    /// <summary>
    /// The assignment's model is unchanged and stays on its own wire number — the completion's
    /// new field must not have moved it.
    /// </summary>
    [Fact]
    public void TaskAssignment_Model_WireFieldNumberIsUnchanged()
    {
        Assert.Equal(9, TaskAssignment.ModelFieldNumber);
    }

    /// <summary>
    /// A NONEMPTY domain model is written, survives binary serialization, and decodes with the
    /// value intact and presence set.
    /// </summary>
    [Fact]
    public void TaskComplete_Model_NonemptyValue_RoundTripsWithPresence()
    {
        var original = BuildFullTaskResult() with { Model = "copilot/claude-sonnet-4.6" };

        var complete = GrpcMapper.ToGrpc(original);
        Assert.True(complete.HasModel);

        var decoded = TaskComplete.Parser.ParseFrom(complete.ToByteArray());
        Assert.True(decoded.HasModel);
        Assert.Equal("copilot/claude-sonnet-4.6", decoded.Model);
        Assert.Equal("copilot/claude-sonnet-4.6", GrpcMapper.ToDomain(decoded).Model);
    }

    /// <summary>
    /// THE PRESENCE-CRITICAL CASE: an EMPTY domain model is still WRITTEN, so the serialized
    /// bytes carry the presence bit and decode with <c>HasModel == true</c> and an empty value.
    /// That is "upgraded sender, assigned model unknown/empty" — never absence.
    /// </summary>
    [Fact]
    public void TaskComplete_Model_EmptyValue_IsWrittenAndDecodesWithPresence()
    {
        var original = BuildFullTaskResult() with { Model = "" };

        var complete = GrpcMapper.ToGrpc(original);
        Assert.True(complete.HasModel);
        Assert.Equal("", complete.Model);

        var decoded = TaskComplete.Parser.ParseFrom(complete.ToByteArray());
        Assert.True(decoded.HasModel, "A present empty model must keep its explicit presence bit.");
        Assert.Equal("", decoded.Model);
        Assert.Equal("", GrpcMapper.ToDomain(decoded).Model);
    }

    /// <summary>
    /// A runtime-NULL domain model is normalized to empty and STILL written, so it too has
    /// explicit presence rather than being silently dropped from the wire.
    /// </summary>
    [Fact]
    public void TaskComplete_Model_NullDomainValue_NormalizesToPresentEmpty()
    {
        var original = BuildFullTaskResult() with { Model = null! };
        Assert.Null(original.Model);

        var complete = GrpcMapper.ToGrpc(original);

        var decoded = TaskComplete.Parser.ParseFrom(complete.ToByteArray());
        Assert.True(decoded.HasModel, "A normalized null model must still be a PRESENT (empty) value.");
        Assert.Equal("", decoded.Model);
        Assert.Equal("", GrpcMapper.ToDomain(decoded).Model);
    }

    /// <summary>
    /// WHITESPACE is passed through VERBATIM — never trimmed or normalized into emptiness — and
    /// still carries presence.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("  copilot/gpt-5  ")]
    public void TaskComplete_Model_WhitespaceValue_PassesThroughVerbatimWithPresence(string value)
    {
        var original = BuildFullTaskResult() with { Model = value };

        var complete = GrpcMapper.ToGrpc(original);

        var decoded = TaskComplete.Parser.ParseFrom(complete.ToByteArray());
        Assert.True(decoded.HasModel);
        Assert.Equal(value, decoded.Model);
        Assert.Equal(value, GrpcMapper.ToDomain(decoded).Model);
    }

    /// <summary>
    /// A LEGACY sender's message — field 7 never present, whether the bytes were produced by an
    /// old writer that never knew the field or by an upgraded one that cleared it — decodes with
    /// <c>HasModel == false</c> and maps to the domain empty default. Absence is a property of
    /// the wire bytes, not of the value's content.
    /// </summary>
    [Fact]
    public void TaskComplete_Model_LegacyAbsentField_DecodesWithoutPresence()
    {
        // A legacy message that never touched field 7.
        var legacy = new TaskComplete
        {
            TaskId = "t",
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "done",
        };
        Assert.False(legacy.HasModel);

        var decoded = TaskComplete.Parser.ParseFrom(legacy.ToByteArray());
        Assert.False(decoded.HasModel, "Absent field 7 must stay absent after a binary round-trip.");
        Assert.Equal("", decoded.Model);
        Assert.Equal("", GrpcMapper.ToDomain(decoded).Model);

        // And an explicitly cleared (present-then-removed) value is likewise absent.
        var cleared = GrpcMapper.ToGrpc(BuildFullTaskResult() with { Model = "m" });
        cleared.ClearModel();
        var decodedCleared = TaskComplete.Parser.ParseFrom(cleared.ToByteArray());
        Assert.False(decodedCleared.HasModel);
    }

    /// <summary>
    /// ALL OTHER completion fields keep their existing mapping alongside the new model field, so
    /// adding field 7 cannot have disturbed the pre-existing wire layout.
    /// </summary>
    [Fact]
    public void TaskComplete_Model_DoesNotDisturbExistingFieldMapping()
    {
        const string sha = "abc123def456789012345678901234567890abcd";
        var original = BuildFullTaskResult() with { Model = "model-x", IterationStartSha = sha };

        var complete = GrpcMapper.ToGrpc(original);
        var decoded = TaskComplete.Parser.ParseFrom(complete.ToByteArray());
        var restored = GrpcMapper.ToDomain(decoded);

        Assert.Equal(original.TaskId, restored.TaskId);
        Assert.Equal(original.Status, restored.Status);
        Assert.Equal(original.Output, restored.Output);
        Assert.Equal(sha, restored.IterationStartSha);
        Assert.Equal("model-x", restored.Model);

        Assert.NotNull(restored.Metrics);
        Assert.Equal(original.Metrics!.Verdict, restored.Metrics.Verdict);
        Assert.Equal(original.Metrics.BuildSuccess, restored.Metrics.BuildSuccess);
        Assert.Equal(original.Metrics.TotalTests, restored.Metrics.TotalTests);
        Assert.Equal(original.Metrics.PassedTests, restored.Metrics.PassedTests);
        Assert.Equal(original.Metrics.FailedTests, restored.Metrics.FailedTests);
        Assert.Equal(original.Metrics.CoveragePercent, restored.Metrics.CoveragePercent);
        Assert.Equal(original.Metrics.Issues, restored.Metrics.Issues);
        Assert.Equal(original.Metrics.Summary, restored.Metrics.Summary);

        Assert.NotNull(restored.GitStatus);
        Assert.Equal(original.GitStatus!.FilesChanged, restored.GitStatus.FilesChanged);
        Assert.Equal(original.GitStatus.Insertions, restored.GitStatus.Insertions);
        Assert.Equal(original.GitStatus.Deletions, restored.GitStatus.Deletions);
        Assert.Equal(original.GitStatus.Pushed, restored.GitStatus.Pushed);
    }

    // ── TaskComplete.usage (field 8) ──────────────────────────────────────────
    //
    // These tests go through the REAL wire path — ToGrpc → ToByteArray → Parser.ParseFrom →
    // ToDomain — so every assertion is made on values that survived binary serialization of the
    // generated message, not on the in-memory object the mapper happened to build. A mapper that
    // assigned the wrong field number, dropped a count or lost the entry-model presence bit fails
    // here.

    /// <summary>
    /// Builds a fully populated entry: DISTINCT non-zero values in every TokenUsage field, both
    /// reported-calls counters and all 13 estimated fields, so a dropped or mis-numbered wire field
    /// is always visible.
    /// </summary>
    private static SharpUsageEntry BuildFullUsageEntry(SharpUsageSource source, string? model)
        => new(source, model, new SharpTokenUsage
        {
            InputTokens = 101,
            OutputTokens = 202,
            CachedInputTokens = 303,
            ReasoningTokens = 404,
            Calls = 7,
            // Deliberately BELOW Calls: at least one call did not report this category, so the
            // category is "unknown" rather than a measurement.
            CachedInputReportedCalls = 5,
            ReasoningReportedCalls = 7,
            Estimated = new SharpEstimatedTokenBreakdown
            {
                SystemPrompt = 11,
                ToolDefinitions = 12,
                UserText = 13,
                AssistantText = 14,
                ToolCalls = 15,
                ToolResults = 16,
                Reasoning = 17,
                Images = 18,
                OutputText = 19,
                OutputToolCalls = 20,
                OutputReasoning = 21,
                InputEstimatedCalls = 22,
                OutputEstimatedCalls = 23,
            },
        });

    private static TaskResult RoundTripThroughWire(TaskResult original)
    {
        var complete = GrpcMapper.ToGrpc(original);
        var decoded = TaskComplete.Parser.ParseFrom(complete.ToByteArray());
        return GrpcMapper.ToDomain(decoded);
    }

    /// <summary>
    /// The FULL payload survives the wire: two entries with different (source, model) pairs keep
    /// every TokenUsage field, both reported-calls counters — including one deliberately below
    /// <c>Calls</c>, the incomplete/"unknown" case — and all 13 estimated fields with their exact
    /// distinct values.
    /// </summary>
    [Fact]
    public void TaskComplete_Usage_TwoEntries_RoundTripKeepsEveryCount()
    {
        var original = BuildFullTaskResult() with
        {
            Usage = new SharpUsageSummary(
            [
                BuildFullUsageEntry(SharpUsageSource.Agent, "model-a"),
                BuildFullUsageEntry(SharpUsageSource.SubAgent, "model-b"),
            ]),
        };

        var restored = RoundTripThroughWire(original);

        Assert.NotNull(restored.Usage);
        Assert.Equal(2, restored.Usage.Entries.Count);

        var agent = Assert.Single(restored.Usage.Entries, e => e.Source == SharpUsageSource.Agent);
        Assert.Equal("model-a", agent.Model);
        AssertTokenUsageIsFullyPreserved(agent.Usage);

        var subAgent = Assert.Single(restored.Usage.Entries, e => e.Source == SharpUsageSource.SubAgent);
        Assert.Equal("model-b", subAgent.Model);
        AssertTokenUsageIsFullyPreserved(subAgent.Usage);
    }

    /// <summary>
    /// Asserts every exact count of a fully populated usage entry: the four token sums, the call
    /// count, both reported-calls counters and all 13 estimated fields.
    /// </summary>
    private static void AssertTokenUsageIsFullyPreserved(SharpTokenUsage usage)
    {
        Assert.Equal(101, usage.InputTokens);
        Assert.Equal(202, usage.OutputTokens);
        Assert.Equal(303, usage.CachedInputTokens);
        Assert.Equal(404, usage.ReasoningTokens);
        Assert.Equal(7, usage.Calls);
        Assert.Equal(5, usage.CachedInputReportedCalls);
        Assert.Equal(7, usage.ReasoningReportedCalls);

        // The counters say which categories were actually reported by every call.
        Assert.NotEqual(usage.CachedInputReportedCalls, usage.Calls);
        Assert.Equal(usage.ReasoningReportedCalls, usage.Calls);

        var estimated = usage.Estimated;
        Assert.Equal(11, estimated.SystemPrompt);
        Assert.Equal(12, estimated.ToolDefinitions);
        Assert.Equal(13, estimated.UserText);
        Assert.Equal(14, estimated.AssistantText);
        Assert.Equal(15, estimated.ToolCalls);
        Assert.Equal(16, estimated.ToolResults);
        Assert.Equal(17, estimated.Reasoning);
        Assert.Equal(18, estimated.Images);
        Assert.Equal(19, estimated.OutputText);
        Assert.Equal(20, estimated.OutputToolCalls);
        Assert.Equal(21, estimated.OutputReasoning);
        Assert.Equal(22, estimated.InputEstimatedCalls);
        Assert.Equal(23, estimated.OutputEstimatedCalls);
    }

    /// <summary>
    /// An entry whose model is <c>null</c> must come back <c>null</c> — NOT the empty string.
    /// Unlike the completion's assigned model (field 7), this field's presence carries the
    /// difference between "model unknown" and "model reported as empty".
    /// </summary>
    [Fact]
    public void TaskComplete_Usage_NullModel_StaysNullNotEmptyString()
    {
        var original = BuildFullTaskResult() with
        {
            Usage = new SharpUsageSummary([BuildFullUsageEntry(SharpUsageSource.Agent, null)]),
        };

        var complete = GrpcMapper.ToGrpc(original);
        Assert.False(complete.Usage.Entries[0].HasModel, "A null entry model must be absent on the wire.");

        var decoded = TaskComplete.Parser.ParseFrom(complete.ToByteArray());
        Assert.False(decoded.Usage.Entries[0].HasModel);

        var restored = RoundTripThroughWire(original);
        var entry = Assert.Single(restored.Usage!.Entries);
        Assert.Null(entry.Model);
    }

    /// <summary>
    /// An entry with an EMPTY (non-null) model keeps that empty string with explicit presence —
    /// the value is never upgraded into absence, nor is a null ever coerced to empty.
    /// </summary>
    [Fact]
    public void TaskComplete_Usage_EmptyModel_StaysPresentEmpty()
    {
        var original = BuildFullTaskResult() with
        {
            Usage = new SharpUsageSummary([BuildFullUsageEntry(SharpUsageSource.Agent, "")]),
        };

        var complete = GrpcMapper.ToGrpc(original);
        Assert.True(complete.Usage.Entries[0].HasModel);
        Assert.Equal("", complete.Usage.Entries[0].Model);

        var restored = RoundTripThroughWire(original);
        var entry = Assert.Single(restored.Usage!.Entries);
        Assert.Equal("", entry.Model);
    }

    /// <summary>
    /// ABSENT usage — a legacy sender, or a test double that never reports usage — maps to
    /// <c>Usage == null</c>, which is NOT the same as an empty summary.
    /// </summary>
    [Fact]
    public void TaskComplete_Usage_Absent_BecomesNull()
    {
        // A legacy message that never touched field 8, round-tripped through raw bytes.
        var legacy = new TaskComplete
        {
            TaskId = "t",
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "done",
        };
        Assert.Null(legacy.Usage);

        var decoded = TaskComplete.Parser.ParseFrom(legacy.ToByteArray());
        Assert.Null(decoded.Usage);
        Assert.Null(GrpcMapper.ToDomain(decoded).Usage);

        // And a domain result without usage writes no field 8 at all.
        var complete = GrpcMapper.ToGrpc(BuildFullTaskResult() with { Usage = null });
        Assert.Null(complete.Usage);
        Assert.Null(TaskComplete.Parser.ParseFrom(complete.ToByteArray()).Usage);
        Assert.Null(GrpcMapper.ToDomain(TaskComplete.Parser.ParseFrom(complete.ToByteArray())).Usage);
    }

    /// <summary>
    /// PRESENT-but-empty usage is a REAL measurement: "usage was reported and the task made zero
    /// model calls". It must decode as an empty, non-null summary — never as absence, never as a
    /// null. The presence survives the wire because the mapper assigns the message.
    /// </summary>
    [Fact]
    public void TaskComplete_Usage_PresentButEmpty_BecomesEmptyNonNullSummary()
    {
        var original = BuildFullTaskResult() with { Usage = new SharpUsageSummary() };

        var complete = GrpcMapper.ToGrpc(original);
        Assert.NotNull(complete.Usage);

        var decoded = TaskComplete.Parser.ParseFrom(complete.ToByteArray());
        Assert.NotNull(decoded.Usage);
        Assert.Empty(decoded.Usage.Entries);

        var restored = GrpcMapper.ToDomain(decoded);
        Assert.NotNull(restored.Usage);
        Assert.Empty(restored.Usage.Entries);
        Assert.Equal(0, restored.Usage.Total.Calls);
    }

    /// <summary>
    /// Every usage source maps both ways across the wire and back, so no source can be silently
    /// collapsed onto another (or onto Unspecified).
    /// </summary>
    [Theory]
    [InlineData(SharpUsageSource.Agent, GrpcUsageSourceKind.Agent)]
    [InlineData(SharpUsageSource.Compaction, GrpcUsageSourceKind.Compaction)]
    [InlineData(SharpUsageSource.SubAgent, GrpcUsageSourceKind.SubAgent)]
    [InlineData(SharpUsageSource.SubAgentCompaction, GrpcUsageSourceKind.SubAgentCompaction)]
    public void TaskComplete_Usage_EachSource_RoundTripsBothWays(SharpUsageSource source, GrpcUsageSourceKind wire)
    {
        Assert.Equal(wire, GrpcMapper.ToGrpc(source));
        Assert.Equal(source, GrpcMapper.ToDomain(wire));

        var original = BuildFullTaskResult() with
        {
            Usage = new SharpUsageSummary([BuildFullUsageEntry(source, "model-x")]),
        };

        var restored = RoundTripThroughWire(original);

        var entry = Assert.Single(restored.Usage!.Entries);
        Assert.Equal(source, entry.Source);
        Assert.Equal("model-x", entry.Model);
        AssertTokenUsageIsFullyPreserved(entry.Usage);
    }

    /// <summary>
    /// An UNSPECIFIED source is a mapping failure, exactly like an unknown <c>TaskStatus</c>: it is
    /// never silently resolved to a default source. An unknown NUMERIC value (a source added by a
    /// newer sender than this receiver) fails the same way.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void TaskComplete_Usage_UnspecifiedOrUnknownSource_Throws(int rawSource)
    {
        var complete = BuildFullTaskResultWithWireUsage((GrpcUsageSourceKind)rawSource);

        var decoded = TaskComplete.Parser.ParseFrom(complete.ToByteArray());
        Assert.Equal((GrpcUsageSourceKind)rawSource, decoded.Usage.Entries[0].Source);

        var ex = Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToDomain(decoded));
        Assert.Contains(((GrpcUsageSourceKind)rawSource).ToString(), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a completion with a single wire usage entry whose source is the raw value supplied by
    /// the caller, bypassing the mapper's own source conversion so an invalid source can be put on
    /// the wire on purpose.
    /// </summary>
    private static TaskComplete BuildFullTaskResultWithWireUsage(GrpcUsageSourceKind rawSource)
    {
        var complete = GrpcMapper.ToGrpc(BuildFullTaskResult() with
        {
            Usage = new SharpUsageSummary([BuildFullUsageEntry(SharpUsageSource.Agent, "model-a")]),
        });
        complete.Usage.Entries[0].Source = rawSource;
        return complete;
    }

    /// <summary>
    /// The EXISTING orchestrator caller path is unchanged: <c>GrpcMapper.ToDomain(complete) with
    /// { Model = ... }</c> keeps the usage the mapper produced, because <c>with</c> copies every
    /// other property. This is the path HiveOrchestratorService uses to substitute the assigned
    /// model, and it must not silently drop the usage report.
    /// </summary>
    [Fact]
    public void TaskComplete_Usage_SurvivesTheOrchestratorModelSubstitution()
    {
        var original = BuildFullTaskResult() with
        {
            Usage = new SharpUsageSummary(
            [
                BuildFullUsageEntry(SharpUsageSource.Agent, "model-a"),
                BuildFullUsageEntry(SharpUsageSource.Compaction, null),
            ]),
        };

        var decoded = TaskComplete.Parser.ParseFrom(GrpcMapper.ToGrpc(original).ToByteArray());
        var substituted = GrpcMapper.ToDomain(decoded) with { Model = "active-queue-model" };

        Assert.Equal("active-queue-model", substituted.Model);
        Assert.NotNull(substituted.Usage);
        Assert.Equal(2, substituted.Usage.Entries.Count);
        var agent = Assert.Single(substituted.Usage.Entries, e => e.Source == SharpUsageSource.Agent);
        AssertTokenUsageIsFullyPreserved(agent.Usage);
        var compaction = Assert.Single(substituted.Usage.Entries, e => e.Source == SharpUsageSource.Compaction);
        Assert.Null(compaction.Model);
    }

    /// <summary>
    /// Bucketing is per (source, MODEL) pair, not per source alone: two entries that share the same
    /// source but differ in model, and that carry DIFFERENT counts, both survive the wire with their
    /// own values. A mapper that collapsed same-source entries, or mixed one entry's counts into the
    /// other, fails here. Each field also uses a seed-distinct value per entry so token counts cannot
    /// masquerade as each other.
    /// </summary>
    [Fact]
    public void TaskComplete_Usage_SameSourceDifferentModels_StayDistinctWithOwnCounts()
    {
        static SharpUsageEntry SeededEntry(string model, long seed) => new(
            SharpUsageSource.Agent,
            model,
            new SharpTokenUsage
            {
                InputTokens = 100 + seed,
                OutputTokens = 200 + seed,
                CachedInputTokens = 300 + seed,
                ReasoningTokens = 400 + seed,
                Calls = 1,
                CachedInputReportedCalls = 1,
                ReasoningReportedCalls = 0,
            });

        var original = BuildFullTaskResult() with
        {
            Usage = new SharpUsageSummary(
            [
                SeededEntry("model-1", seed: 1),
                SeededEntry("model-2", seed: 2),
            ]),
        };

        var restored = RoundTripThroughWire(original);

        Assert.NotNull(restored.Usage);
        Assert.Equal(2, restored.Usage.Entries.Count);

        var first = Assert.Single(restored.Usage.Entries, e => e.Model == "model-1");
        var second = Assert.Single(restored.Usage.Entries, e => e.Model == "model-2");
        Assert.Equal(SharpUsageSource.Agent, first.Source);
        Assert.Equal(SharpUsageSource.Agent, second.Source);

        Assert.Equal(101, first.Usage.InputTokens);
        Assert.Equal(201, first.Usage.OutputTokens);
        Assert.Equal(301, first.Usage.CachedInputTokens);
        Assert.Equal(401, first.Usage.ReasoningTokens);
        Assert.Equal(102, second.Usage.InputTokens);
        Assert.Equal(202, second.Usage.OutputTokens);
        Assert.Equal(302, second.Usage.CachedInputTokens);
        Assert.Equal(402, second.Usage.ReasoningTokens);
        // Counter semantics survive per entry: calls that did not report a category are NOT added,
        // so here ReasoningReportedCalls (0) < Calls (1) — the incomplete/"unknown" case per entry.
        Assert.Equal(1, first.Usage.Calls);
        Assert.Equal(0, first.Usage.ReasoningReportedCalls);
        Assert.NotEqual(first.Usage.ReasoningReportedCalls, first.Usage.Calls);
        Assert.Equal(0, second.Usage.ReasoningReportedCalls);
    }

    /// <summary>
    /// The restored usage is a REAL functioning <see cref="SharpUsageSummary"/>, not just a filled
    /// entries list: its <c>Total</c> aggregates the token sums and the call counter across every
    /// entry, exactly as UsageSummary.Add would accumulate them.
    /// </summary>
    [Fact]
    public void TaskComplete_Usage_RestoredSummary_TotalAggregatesAcrossEntries()
    {
        var original = BuildFullTaskResult() with
        {
            Usage = new SharpUsageSummary(
            [
                BuildFullUsageEntry(SharpUsageSource.Agent, "model-a"),
                BuildFullUsageEntry(SharpUsageSource.SubAgent, "model-b"),
            ]),
        };

        var restored = RoundTripThroughWire(original);

        Assert.NotNull(restored.Usage);
        var total = restored.Usage.Total;
        Assert.Equal(202, total.InputTokens);            // 101 + 101
        Assert.Equal(404, total.OutputTokens);           // 202 + 202
        Assert.Equal(606, total.CachedInputTokens);      // 303 + 303
        Assert.Equal(808, total.ReasoningTokens);        // 404 + 404
        Assert.Equal(14, total.Calls);                   // 7 + 7
        Assert.Equal(10, total.CachedInputReportedCalls); // 5 + 5 — below Calls
        Assert.Equal(14, total.ReasoningReportedCalls);
    }

    // ── ToGrpc(UsageSummary): the SHARED usage writer ─────────────────────────

    /// <summary>
    /// The public <see cref="GrpcMapper.ToGrpc(SharpUsageSummary)"/> overload — the same writer the
    /// completion path and the heartbeat's live usage use — round-trips TWO entries through the wire
    /// and back through <see cref="GrpcMapper.ToDomain(TaskUsage)"/>, keeping every token count,
    /// both reported-calls counters (including one deliberately below <c>Calls</c>) and all 13
    /// estimated fields of each entry.
    /// </summary>
    [Fact]
    public void UsageSummary_ToGrpc_TwoEntries_RoundTripKeepsEveryCount()
    {
        var original = new SharpUsageSummary(
        [
            BuildFullUsageEntry(SharpUsageSource.Agent, "model-a"),
            BuildFullUsageEntry(SharpUsageSource.SubAgentCompaction, "model-b"),
        ]);

        var wire = GrpcMapper.ToGrpc(original);
        var decoded = TaskUsage.Parser.ParseFrom(wire.ToByteArray());
        var restored = GrpcMapper.ToDomain(decoded);

        Assert.Equal(2, restored.Entries.Count);

        var agent = Assert.Single(restored.Entries, e => e.Source == SharpUsageSource.Agent);
        Assert.Equal("model-a", agent.Model);
        AssertTokenUsageIsFullyPreserved(agent.Usage);

        var compaction = Assert.Single(restored.Entries, e => e.Source == SharpUsageSource.SubAgentCompaction);
        Assert.Equal("model-b", compaction.Model);
        AssertTokenUsageIsFullyPreserved(compaction.Usage);
    }

    /// <summary>
    /// THE EXTRACTION IS WIRE-IDENTICAL: the standalone overload produces EXACTLY the bytes the
    /// completion path writes for the same summary, so moving the loop behind
    /// <c>ToGrpc(UsageSummary)</c> cannot have changed the completion's wire output.
    /// </summary>
    /// <remarks>
    /// The comparison is made on RE-PARSED bytes, so it observes the serialized message rather than
    /// the in-memory object the mapper happened to build.
    /// </remarks>
    [Fact]
    public void UsageSummary_ToGrpc_MatchesTheCompletionPath_ByteForByte()
    {
        var usage = new SharpUsageSummary(
        [
            BuildFullUsageEntry(SharpUsageSource.Agent, "model-a"),
            BuildFullUsageEntry(SharpUsageSource.Compaction, null),
        ]);

        var complete = GrpcMapper.ToGrpc(BuildFullTaskResult() with { Usage = usage });
        var viaCompletion = TaskUsage.Parser.ParseFrom(complete.Usage.ToByteArray());

        var viaStandalone = TaskUsage.Parser.ParseFrom(GrpcMapper.ToGrpc(usage).ToByteArray());

        Assert.Equal(viaCompletion.ToByteArray(), viaStandalone.ToByteArray());
    }

    /// <summary>
    /// A PRESENT-but-EMPTY summary maps to a message with zero entries — a real measurement — and
    /// decodes back to a non-null empty summary. The overload itself never decides presence: it
    /// always returns a message.
    /// </summary>
    [Fact]
    public void UsageSummary_ToGrpc_EmptySummary_IsAnEmptyPresentMessage()
    {
        var wire = GrpcMapper.ToGrpc(new SharpUsageSummary());

        Assert.NotNull(wire);
        Assert.Empty(wire.Entries);

        var decoded = TaskUsage.Parser.ParseFrom(wire.ToByteArray());
        Assert.NotNull(decoded);
        Assert.Empty(decoded.Entries);

        var restored = GrpcMapper.ToDomain(decoded);
        Assert.Empty(restored.Entries);
        Assert.Equal(0, restored.Total.Calls);
    }

    /// <summary>
    /// The entry-model PRESENCE BIT is written by the overload exactly as the completion path wrote
    /// it: a <c>null</c> model stays absent on the wire (never normalized to the empty string), and a
    /// model reported as empty stays PRESENT with an empty value.
    /// </summary>
    [Fact]
    public void UsageSummary_ToGrpc_ModelPresence_IsWrittenExactly()
    {
        var nullModel = GrpcMapper.ToGrpc(new SharpUsageSummary([BuildFullUsageEntry(SharpUsageSource.Agent, null)]));
        Assert.False(nullModel.Entries[0].HasModel, "A null entry model must be absent on the wire.");
        Assert.Null(GrpcMapper.ToDomain(TaskUsage.Parser.ParseFrom(nullModel.ToByteArray())).Entries[0].Model);

        var emptyModel = GrpcMapper.ToGrpc(new SharpUsageSummary([BuildFullUsageEntry(SharpUsageSource.Agent, "")]));
        Assert.True(emptyModel.Entries[0].HasModel, "An empty (non-null) model must stay PRESENT on the wire.");
        Assert.Equal("", emptyModel.Entries[0].Model);
        Assert.Equal("", GrpcMapper.ToDomain(TaskUsage.Parser.ParseFrom(emptyModel.ToByteArray())).Entries[0].Model);
    }

    /// <summary>
    /// The overload is the INVERSE of <see cref="GrpcMapper.ToDomain(TaskUsage)"/> for every usage
    /// source, so a live-usage heartbeat and a completion cannot disagree about how a source is
    /// spelled on the wire.
    /// </summary>
    [Theory]
    [InlineData(SharpUsageSource.Agent, GrpcUsageSourceKind.Agent)]
    [InlineData(SharpUsageSource.Compaction, GrpcUsageSourceKind.Compaction)]
    [InlineData(SharpUsageSource.SubAgent, GrpcUsageSourceKind.SubAgent)]
    [InlineData(SharpUsageSource.SubAgentCompaction, GrpcUsageSourceKind.SubAgentCompaction)]
    public void UsageSummary_ToGrpc_EachSource_MatchesTheInboundMapping(SharpUsageSource source, GrpcUsageSourceKind wire)
    {
        var message = GrpcMapper.ToGrpc(new SharpUsageSummary(
        [
            new SharpUsageEntry(source, "model-x", new SharpTokenUsage { InputTokens = 5, Calls = 1 }),
        ]));

        var entry = Assert.Single(message.Entries);
        Assert.Equal(wire, entry.Source);
        Assert.Equal(source, GrpcMapper.ToDomain(entry.Source));
    }

    /// <summary>
    /// An UNSPECIFIED source cannot be written by the overload either: the outbound source mapping
    /// refuses every value without a wire equivalent rather than silently emitting
    /// <c>USAGE_SOURCE_KIND_UNSPECIFIED</c>.
    /// </summary>
    [Fact]
    public void UsageSummary_ToGrpc_UnmappableSource_Throws()
    {
        var summary = new SharpUsageSummary(
        [
            new SharpUsageEntry(
                (SharpUsageSource)99,
                "model-x",
                new SharpTokenUsage { InputTokens = 5, Calls = 1 }),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => GrpcMapper.ToGrpc(summary));
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    // ── HeartbeatRequest.live_usage (field 7) ────────────────────────────────

    /// <summary>
    /// THE HEARTBEAT'S LIVE-USAGE FIELD IS ADDITIVE: it is exactly field 7 and collides with none of
    /// the pre-existing heartbeat fields (1, 3, 4, 5, 6 — 2 is reserved), so an old server reads the
    /// heartbeat exactly as before and simply ignores the usage it does not know. No handshake and no
    /// renumbering is involved.
    /// </summary>
    [Fact]
    public void HeartbeatRequest_LiveUsage_UsesFieldSevenWithoutRenumberingExistingFields()
    {
        Assert.Equal(7, HeartbeatRequest.LiveUsageFieldNumber);

        int[] existing =
        [
            HeartbeatRequest.WorkerIdFieldNumber,
            HeartbeatRequest.BusyFieldNumber,
            HeartbeatRequest.CurrentTaskIdFieldNumber,
            HeartbeatRequest.CurrentRoleFieldNumber,
            HeartbeatRequest.ContextUsagePercentFieldNumber,
        ];

        Assert.Equal([1, 3, 4, 5, 6], existing);
        Assert.DoesNotContain(HeartbeatRequest.LiveUsageFieldNumber, existing);
        // Field 2 stays reserved: the removed fixed WorkerRole is never reused for usage.
        Assert.DoesNotContain(2, existing);
    }

    /// <summary>
    /// The heartbeat's presence bit is the ONLY trustworthy signal: a heartbeat that never touches
    /// field 7 decodes with <c>LiveUsage == null</c> ("the worker reports no live usage"), while a
    /// PRESENT-but-EMPTY usage decodes as a real measurement with zero entries — never as absence.
    /// </summary>
    [Fact]
    public void HeartbeatRequest_LiveUsage_PresenceDistinguishesAbsenceFromEmptyReport()
    {
        var legacy = new HeartbeatRequest { WorkerId = "w1", Busy = false, ContextUsagePercent = 10 };
        var decodedLegacy = HeartbeatRequest.Parser.ParseFrom(legacy.ToByteArray());
        Assert.Null(decodedLegacy.LiveUsage);

        var reporting = new HeartbeatRequest
        {
            WorkerId = "w1",
            Busy = true,
            CurrentTaskId = "task-1",
            LiveUsage = new TaskUsage(),
        };
        var decodedReporting = HeartbeatRequest.Parser.ParseFrom(reporting.ToByteArray());
        Assert.NotNull(decodedReporting.LiveUsage);
        Assert.Empty(decodedReporting.LiveUsage.Entries);
        Assert.Equal("task-1", decodedReporting.CurrentTaskId);
    }
}
