using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

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
}
