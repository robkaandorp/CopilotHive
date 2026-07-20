using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the <c>report_narrative</c> tool handler on
/// <see cref="HiveOrchestratorService.HandleToolCallRequestAsync"/>. Verifies that a
/// narrative is stored on the matching pipeline, that empty/whitespace narratives are
/// ignored, and that a missing pipeline does not cause a failure.
/// </summary>
public sealed class HiveOrchestratorNarrativeToolTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (HiveOrchestratorService service, GoalPipelineManager pipelineManager, WorkerPool pool)
        CreateService()
    {
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var completionNotifier = new TaskCompletionNotifier();
        var goalManager = new GoalManager();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(pool),
            completionNotifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        var service = new HiveOrchestratorService(
            pool,
            taskQueue,
            pipelineManager,
            completionNotifier,
            dispatcher,
            NullLogger<HiveOrchestratorService>.Instance);

        return (service, pipelineManager, pool);
    }

    private static GoalPipeline CreatePipelineForTask(
        GoalPipelineManager manager, string goalId, string taskId)
    {
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        var pipeline = manager.CreatePipeline(goal);
        manager.RegisterTask(taskId, goalId);
        pipeline.SetActiveTask(taskId);
        return pipeline;
    }

    // ── Test 1: valid narrative stored ────────────────────────────────────────

    [Fact]
    public async Task ReportNarrative_WithValidNarrative_StoresEntryOnPipeline()
    {
        var (service, pipelineManager, pool) = CreateService();
        var worker = pool.RegisterWorker("worker-1", []);
        var pipeline = CreatePipelineForTask(pipelineManager, "goal-1", "task-1");

        const string content = "I tried refactoring the service layer and it worked well";
        await service.HandleToolCallRequestAsync(
            worker,
            new ToolCallRequest
            {
                RequestId = "req-1",
                TaskId = "task-1",
                ToolName = "report_narrative",
                ArgumentsJson = $"{{\"narrative\":\"{content}\"}}",
            },
            CancellationToken.None);

        var response = await worker.MessageChannel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(response.ToolResponse.Success);
        Assert.Contains("\"acknowledged\":true", response.ToolResponse.ResultJson);

        var entry = Assert.Single(pipeline.Narratives);
        Assert.Equal("worker-1", entry.WorkerId);
        Assert.Equal("task-1", entry.TaskId);
        Assert.Equal(content, entry.Content);
    }

    // ── Test 2: empty/whitespace narrative ignored ────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReportNarrative_WithEmptyNarrative_DoesNotStoreEntry(string narrative)
    {
        var (service, pipelineManager, pool) = CreateService();
        var worker = pool.RegisterWorker("worker-1", []);
        var pipeline = CreatePipelineForTask(pipelineManager, "goal-1", "task-1");

        await service.HandleToolCallRequestAsync(
            worker,
            new ToolCallRequest
            {
                RequestId = "req-1",
                TaskId = "task-1",
                ToolName = "report_narrative",
                ArgumentsJson = $"{{\"narrative\":\"{narrative}\"}}",
            },
            CancellationToken.None);

        var response = await worker.MessageChannel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(response.ToolResponse.Success);
        Assert.Contains("\"acknowledged\":true", response.ToolResponse.ResultJson);

        Assert.Empty(pipeline.Narratives);
    }

    // ── Test 3: missing pipeline does not throw ───────────────────────────────

    [Fact]
    public async Task ReportNarrative_WithMissingPipeline_DoesNotThrow()
    {
        var (service, _, pool) = CreateService();
        var worker = pool.RegisterWorker("worker-1", []);

        await service.HandleToolCallRequestAsync(
            worker,
            new ToolCallRequest
            {
                RequestId = "req-1",
                TaskId = "nonexistent-task",
                ToolName = "report_narrative",
                ArgumentsJson = "{\"narrative\":\"A narrative with no pipeline\"}",
            },
            CancellationToken.None);

        var response = await worker.MessageChannel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(response.ToolResponse.Success);
        Assert.Contains("\"acknowledged\":true", response.ToolResponse.ResultJson);
    }
}
