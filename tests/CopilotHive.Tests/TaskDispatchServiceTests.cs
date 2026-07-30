using System.Collections.Concurrent;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for <see cref="TaskDispatchService"/> verifying that behaviors
/// extracted from <see cref="GoalDispatcher"/> are preserved.
/// </summary>
public sealed class TaskDispatchServiceTests
{
    // ── ResolveRepositories ───────────────────────────────────────────────

    [Fact]
    public void ResolveRepositories_AllValidNames_ReturnsAllRepositories()
    {
        var service = CreateService(config: new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
                new RepositoryConfig { Name = "RepoB", Url = "https://github.com/org/repo-b" },
            ],
        });
        var goal = new Goal { Id = "goal-1", Description = "Test", RepositoryNames = ["RepoA", "RepoB"] };

        var result = service.ResolveRepositories(goal);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Name == "RepoA");
        Assert.Contains(result, r => r.Name == "RepoB");
    }

    [Fact]
    public void ResolveRepositories_UnknownName_ThrowsInvalidOperationExceptionWithGoalIdAndRepoName()
    {
        var service = CreateService(config: new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
            ],
        });
        var goal = new Goal { Id = "goal-42", Description = "Test", RepositoryNames = ["missing-repo"] };

        var ex = Assert.Throws<InvalidOperationException>(() => service.ResolveRepositories(goal));

        Assert.Contains("goal-42", ex.Message);
        Assert.Contains("missing-repo", ex.Message);
    }

    [Fact]
    public void ResolveRepositories_MixOfValidAndInvalid_FailsWithoutPartialResults()
    {
        var service = CreateService(config: new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
            ],
        });
        var goal = new Goal { Id = "goal-3", Description = "Test", RepositoryNames = ["RepoA", "bad-repo"] };

        Assert.Throws<InvalidOperationException>(() => service.ResolveRepositories(goal));
    }

    // ── DispatchToRole: premium model selection ───────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenPremiumTierAndPremiumModelConfigured_UsesPremiumModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            PremiumModel = "premium-coder-model",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("premium-coder-model", capturedTask!.Model);
    }

    [Fact]
    public async Task DispatchToRole_WhenPremiumTierButNoPremiumModel_FallsBackToStandardModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            // No PremiumModel configured
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model", capturedTask!.Model);
    }

    [Fact]
    public async Task DispatchToRole_WhenStandardTier_UsesStandardModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            PremiumModel = "premium-coder-model",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model", capturedTask!.Model);
    }

    // ── DispatchToRole: reasoning suffix ──────────────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenModelHasReasoningEffort_AppliesSuffixToDispatchedModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "standard-coder-model", ReasoningEffort = "high" }
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model:high", capturedTask!.Model);
    }

    [Fact]
    public async Task DispatchToRole_WhenModelHasNoReasoningEffort_DoesNotAppendSuffix()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "standard-coder-model" }
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model", capturedTask!.Model);
    }

    // ── DispatchToRole: compaction model metadata ─────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenCompactionModelConfigured_PropagatesCompactionMetadata()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            CompactionModel = "gpt-mini",
            AvailableModels =
            [
                new ModelEntry { Name = "gpt-mini", ContextWindow = 8000 },
                new ModelEntry { Name = "standard-coder-model" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(capturedTask!.Metadata.ContainsKey("compaction_model"));
        Assert.Equal("gpt-mini", capturedTask.Metadata["compaction_model"]);
        Assert.True(capturedTask.Metadata.ContainsKey("compaction_max_tokens"));
        Assert.Equal("8000", capturedTask.Metadata["compaction_max_tokens"]);
    }

    [Fact]
    public async Task DispatchToRole_WhenCompactionModelHasReasoningEffort_AppliesSuffixToCompactionModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            CompactionModel = "gpt-mini",
            AvailableModels =
            [
                new ModelEntry { Name = "gpt-mini", ContextWindow = 8000, ReasoningEffort = "low" },
                new ModelEntry { Name = "standard-coder-model" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("gpt-mini:low", capturedTask!.Metadata["compaction_model"]);
    }

    [Fact]
    public async Task DispatchToRole_WhenNoCompactionModel_DoesNotSetCompactionMetadata()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        // No Models config → no compaction model

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("compaction_model"));
    }

    // ── DispatchToRole: iteration SHA metadata ────────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenIterationStartShaSet_PropagatesShaMetadata()
    {
        var config = CreateConfig();
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Review, config, ModelTier.Default);

        pipeline.IterationStartSha = "abc123sha";

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Reviewer, "Review it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(capturedTask!.Metadata.ContainsKey("iteration_start_sha"));
        Assert.Equal("abc123sha", capturedTask.Metadata["iteration_start_sha"]);
    }

    [Fact]
    public async Task DispatchToRole_WhenIterationStartShaNull_DoesNotSetShaMetadata()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        // IterationStartSha is null by default
        Assert.Null(pipeline.IterationStartSha);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("iteration_start_sha"));
    }

    // ── DispatchToRole: tester report metadata for reviewer role ──────────

    [Fact]
    public async Task DispatchToRole_WhenReviewerRoleAndTesterOutputExists_PropagatesTesterReport()
    {
        var config = CreateConfig();
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Review, config, ModelTier.Default);

        // Add a testing phase log entry with worker output for the current iteration
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            WorkerOutput = "All 50 tests passed.",
        });

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Reviewer, "Review it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(capturedTask!.Metadata.ContainsKey("tester_report"));
        Assert.Equal("All 50 tests passed.", capturedTask.Metadata["tester_report"]);
    }

    [Fact]
    public async Task DispatchToRole_WhenReviewerRoleButNoTesterOutput_DoesNotSetTesterReport()
    {
        var config = CreateConfig();
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Review, config, ModelTier.Default);

        // No testing phase log entry

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Reviewer, "Review it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("tester_report"));
    }

    [Fact]
    public async Task DispatchToRole_WhenNonReviewerRole_DoesNotSetTesterReport()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        // Even with tester output in the log, coder should not get tester_report
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            WorkerOutput = "Tests passed.",
        });

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("tester_report"));
    }

    // ── DispatchToRole: improver branch downgrade ─────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenImproverRole_DowngradesBranchActionToUnspecified()
    {
        var config = CreateConfig();
        config.Workers["improver"] = new WorkerConfig { Model = "improver-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Improve, config, ModelTier.Default);

        // Set a coder branch so the task has BranchInfo
        pipeline.SetActiveTask("previous-task-id", "feature/test-branch");

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Improver, "Improve it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.NotNull(capturedTask!.BranchInfo);
        Assert.Equal(BranchAction.Unspecified, capturedTask.BranchInfo!.Action);
    }

    [Fact]
    public async Task DispatchToRole_WhenNonImproverRole_KeepsBranchAction()
    {
        var config = CreateConfig();
        config.Workers["tester"] = new WorkerConfig { Model = "tester-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Testing, config, ModelTier.Default);

        // Set a coder branch so the task has BranchInfo (Checkout action)
        pipeline.SetActiveTask("previous-task-id", "feature/test-branch");

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Tester, "Test it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.NotNull(capturedTask!.BranchInfo);
        // Tester reuses the existing branch → action should be Checkout, not Unspecified
        Assert.Equal(BranchAction.Checkout, capturedTask.BranchInfo!.Action);
    }

    // ── DispatchToRole: task registration and queue enqueue ───────────────

    [Fact]
    public async Task DispatchToRole_RegistersTaskAndEnqueuesToQueue()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue);

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        WorkTask? enqueuedTask = null;
        taskQueue.OnEnqueue = t => enqueuedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // Task was enqueued
        Assert.NotNull(enqueuedTask);

        // Task was registered in pipeline manager (taskId → goalId mapping)
        var lookupPipeline = pipelineManager.GetByTaskId(enqueuedTask!.TaskId);
        Assert.NotNull(lookupPipeline);
        Assert.Equal(goal.Id, lookupPipeline!.GoalId);

        // Active task was set on the pipeline
        Assert.Equal(enqueuedTask.TaskId, pipeline.ActiveTaskId);
    }

    // ── DispatchToRole: idle worker direct dispatch ───────────────────────

    [Fact]
    public async Task DispatchToRole_WhenIdleWorkerAvailable_DispatchesDirectlyToWorker()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var workerPool = new WorkerPool();
        var idleWorker = workerPool.RegisterWorker("worker-1", []);
        var gateway = new GrpcWorkerGateway(workerPool);

        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue,
            workerGateway: gateway);

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // The idle worker should now be busy with the dispatched task
        Assert.True(idleWorker.IsBusy);
        Assert.NotNull(idleWorker.CurrentTaskId);
        Assert.Equal("coder-model", idleWorker.CurrentModel);
        Assert.Equal(WorkerRole.Coder, idleWorker.Role);

        // The task should have been removed from the pending queue (activated)
        Assert.Null(taskQueue.TryDequeueAny());
    }

    [Fact]
    public async Task DispatchToRole_WhenNoIdleWorker_EnqueuesButDoesNotDispatch()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        // Empty worker pool → no idle worker
        var workerPool = new WorkerPool();
        var gateway = new GrpcWorkerGateway(workerPool);

        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue,
            workerGateway: gateway);

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // Task should remain in the queue (no worker to dispatch to)
        var queuedTask = taskQueue.TryDequeueAny();
        Assert.NotNull(queuedTask);
    }

    // ── DispatchToRole: repository failure → MarkGoalFailedAsync ─────────

    [Fact]
    public async Task DispatchToRole_WhenRepositoryResolutionFails_CallsMarkGoalFailedAsync()
    {
        // Config with no repositories — the goal references a repo that doesn't exist
        var config = new HiveConfigFile
        {
            Repositories = [], // empty — no repos defined
        };

        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();

        // The goal references "test-repo" which is not in the config → ResolveRepositories throws
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        // Pass the actual goal to the GoalManager so MarkGoalFailedAsync can update it
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue,
            goal: goal);

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // Pipeline should be marked as Failed
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    // ── DispatchToRole: sub-agent model catalog ──────────────────────────

    [Fact]
    public async Task DispatchToRole_PopulatesCatalogFromAvailableModels()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a", ContextWindow = 200_000 },
                new ModelEntry { Name = "model-b", ContextWindow = null },
                new ModelEntry { Name = "model-c", ContextWindow = 128_000 },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal(3, capturedTask!.SubAgentModels.Count);

        Assert.Equal("model-a", capturedTask.SubAgentModels[0].Id);
        Assert.Equal(200_000, capturedTask.SubAgentModels[0].ContextWindow);
        Assert.Contains("K context", capturedTask.SubAgentModels[0].Description);

        Assert.Equal("model-b", capturedTask.SubAgentModels[1].Id);
        Assert.Null(capturedTask.SubAgentModels[1].ContextWindow);
        Assert.Equal("Configured model", capturedTask.SubAgentModels[1].Description);

        Assert.Equal("model-c", capturedTask.SubAgentModels[2].Id);
        Assert.Equal(128_000, capturedTask.SubAgentModels[2].ContextWindow);
        Assert.Contains("K context", capturedTask.SubAgentModels[2].Description);
    }

    [Fact]
    public async Task DispatchToRole_DescriptionContainsContextInfo_WhenContextWindowKnown()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a", ContextWindow = 200_000 },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Single(capturedTask!.SubAgentModels);
        Assert.Equal("Configured model, 200K context", capturedTask.SubAgentModels[0].Description);
    }

    [Fact]
    public async Task DispatchToRole_DescriptionIsConfiguredModel_WhenContextWindowNull()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-unknown" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Single(capturedTask!.SubAgentModels);
        Assert.Equal("Configured model", capturedTask.SubAgentModels[0].Description);
    }

    [Fact]
    public async Task DispatchToRole_ConfiguredDescription_FlowsToCatalog()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a", ContextWindow = 200_000, Description = "Deep reasoning workhorse" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("Deep reasoning workhorse", Assert.Single(capturedTask!.SubAgentModels).Description);
    }

    [Fact]
    public async Task DispatchToRole_BlankDescription_FallsBackToAutoGenerated()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a", ContextWindow = 200_000, Description = "   " },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("Configured model, 200K context", Assert.Single(capturedTask!.SubAgentModels).Description);
    }

    [Fact]
    public async Task DispatchToRole_CuratedSubAgentModels_TakePrecedence()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "model-a" }],
            SubAgentModels = [new ModelEntry { Name = "curated-b", Description = "Curated pick" }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        var entry = Assert.Single(capturedTask!.SubAgentModels);
        Assert.Equal("curated-b", entry.Id);
        Assert.Equal("Curated pick", entry.Description);
    }

    [Fact]
    public async Task DispatchToRole_WhenModelsConfigNull_CatalogIsEmpty()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        // config.Models left null

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Empty(capturedTask!.SubAgentModels);
    }

    [Fact]
    public async Task DispatchToRole_WhenAvailableModelsNull_CatalogIsEmpty()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = null,
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Empty(capturedTask!.SubAgentModels);
    }

    [Fact]
    public async Task DispatchToRole_WhenAvailableModelsEmpty_CatalogIsEmpty()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Empty(capturedTask!.SubAgentModels);
    }

    [Fact]
    public async Task DispatchToRole_FiltersBlankModelNames()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "valid-model", ContextWindow = 100_000 },
                new ModelEntry { Name = "  " },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Single(capturedTask!.SubAgentModels);
        Assert.Equal("valid-model", capturedTask.SubAgentModels[0].Id);
    }

    [Fact]
    public async Task DispatchToRole_NoReasoningSuffixAppliedToCatalogIds()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "my-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "my-model", ReasoningEffort = "high" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Single(capturedTask!.SubAgentModels);
        // The dispatched model gets the reasoning suffix, but the catalog ID must NOT.
        Assert.Equal("my-model", capturedTask.SubAgentModels[0].Id);
    }

    // ── DispatchToRole: null prompt defaults to description ───────────────

    [Fact]
    public async Task DispatchToRole_WhenPromptIsNull_UsesDefaultPromptWithDescription()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, null, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Contains(pipeline.Description, capturedTask!.Prompt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static HiveConfigFile CreateConfig()
    {
        var config = new HiveConfigFile();
        if (!config.Repositories.Any(r => r.Name == "test-repo"))
        {
            config.Repositories.Add(new RepositoryConfig
            {
                Name = "test-repo",
                Url = "https://example.com/test-repo.git",
                DefaultBranch = "develop",
            });
        }
        return config;
    }

    private static void SetPlan(GoalPipeline pipeline, ModelTier tier)
    {
        var plan = IterationPlan.Default();
        // Set the requested tier on all worker phases
        foreach (var phase in plan.Phases)
        {
            if (phase is GoalPhase.Planning or GoalPhase.Merging or GoalPhase.Done or GoalPhase.Failed)
                continue;
            plan.PhaseTiers[phase] = tier;
        }
        pipeline.SetPlan(plan);
        pipeline.StateMachine.StartIteration(plan.Phases);
    }

    /// <summary>
    /// Creates a <see cref="TaskDispatchService"/> with the given config and optional overrides.
    /// Dependencies are constructed the same way <see cref="GoalDispatcher"/> does.
    /// </summary>
    private static TaskDispatchService CreateService(
        HiveConfigFile? config = null,
        GoalPipelineManager? pipelineManager = null,
        TaskQueue? taskQueue = null,
        IWorkerGateway? workerGateway = null,
        Goal? goal = null)
    {
        config ??= CreateConfig();
        pipelineManager ??= new GoalPipelineManager();
        taskQueue ??= new TaskQueue();
        workerGateway ??= new GrpcWorkerGateway(new WorkerPool());

        var goalManager = new GoalManager();
        // Register the actual goal (if provided) so UpdateGoalStatusAsync can find it;
        // otherwise use a throwaway setup goal to populate the internal map.
        goalManager.AddSource(new DispatchTestGoalSource(
            goal ?? new Goal { Id = "setup-goal", Description = "Setup" }));
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var logger = NullLogger<TaskDispatchService>.Instance;

        // GoalLifecycleService — constructed the same way as GoalDispatcher
        var lifecycleService = new GoalLifecycleService(
            goalManager, logger);

        // DispatcherMaintenance — constructed the same way as GoalDispatcher
        var redispatchQueue = new ConcurrentQueue<string>();
        var maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway,
            brain: null,
            agentsManager: null,
            configRepo: null,
            redispatchQueue,
            logger,
            config: config);

        var taskBuilder = new TaskBuilder(new BranchCoordinator());

        return new TaskDispatchService(
            taskQueue, workerGateway, taskBuilder, config,
            logger, pipelineManager, lifecycleService, maintenance);
    }

    /// <summary>
    /// Creates a <see cref="TaskDispatchService"/> and a <see cref="GoalPipeline"/> ready for
    /// DispatchToRole testing at the given phase with the given model tier.
    /// </summary>
    private static (TaskDispatchService service, GoalPipeline pipeline, TaskQueue taskQueue)
        CreateServiceWithPipeline(GoalPhase phase, HiveConfigFile config, ModelTier tier)
    {
        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(phase);
        SetPlan(pipeline, tier);

        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue,
            goal: goal);

        return (service, pipeline, taskQueue);
    }

    /// <summary>
    /// Minimal <see cref="IGoalSource"/> that returns a single pre-configured goal.
    /// </summary>
    private sealed class DispatchTestGoalSource : IGoalSource
    {
        private readonly Goal _goal;
        public DispatchTestGoalSource(Goal goal) => _goal = goal;
        public string Name => "dispatch-test-fake";
        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([_goal]);
        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}