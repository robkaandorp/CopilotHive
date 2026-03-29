using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the <c>GetSession</c> and <c>SaveSession</c> gRPC RPCs
/// implemented by <see cref="HiveOrchestratorService"/>.
/// </summary>
public sealed class HiveOrchestratorSessionRpcTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (HiveOrchestratorService service, GoalPipelineManager pipelineManager)
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

        return (service, pipelineManager);
    }

    private static ServerCallContext MockContext() =>
        new Mock<ServerCallContext>().Object;

    private static GoalPipeline CreatePipeline(GoalPipelineManager manager, string goalId = "goal-1")
    {
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        return manager.CreatePipeline(goal);
    }

    // ── GetSession — found ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSession_WhenSessionExists_ReturnsFoundTrueAndSessionJson()
    {
        var (service, pipelineManager) = CreateService();
        CreatePipeline(pipelineManager, "goal-1");

        pipelineManager.SetRoleSession("goal-1", "coder", "{\"key\":\"value\"}");

        var response = await service.GetSession(
            new GetSessionRequest { SessionId = "goal-1:coder" },
            MockContext());

        Assert.True(response.Found);
        Assert.Equal("{\"key\":\"value\"}", response.SessionJson);
    }

    // ── GetSession — not found ────────────────────────────────────────────────

    [Fact]
    public async Task GetSession_WhenGoalDoesNotExist_ReturnsFoundFalse()
    {
        var (service, _) = CreateService();

        var response = await service.GetSession(
            new GetSessionRequest { SessionId = "nonexistent-goal:coder" },
            MockContext());

        Assert.False(response.Found);
        Assert.Equal("", response.SessionJson);
    }

    [Fact]
    public async Task GetSession_WhenRoleSessionNotSet_ReturnsFoundFalse()
    {
        var (service, pipelineManager) = CreateService();
        CreatePipeline(pipelineManager, "goal-2");
        // Pipeline exists but no session set for "tester"

        var response = await service.GetSession(
            new GetSessionRequest { SessionId = "goal-2:tester" },
            MockContext());

        Assert.False(response.Found);
        Assert.Equal("", response.SessionJson);
    }

    // ── SaveSession — persists data ───────────────────────────────────────────

    [Fact]
    public async Task SaveSession_ReturnsSuccessTrue()
    {
        var (service, pipelineManager) = CreateService();
        CreatePipeline(pipelineManager, "goal-3");

        var response = await service.SaveSession(
            new SaveSessionRequest { SessionId = "goal-3:coder", SessionJson = "{}" },
            MockContext());

        Assert.True(response.Success);
    }

    [Fact]
    public async Task SaveSession_ThenGetSession_ReturnsSavedValue()
    {
        var (service, pipelineManager) = CreateService();
        CreatePipeline(pipelineManager, "goal-4");

        const string sessionJson = "{\"iteration\":3,\"messages\":[]}";

        await service.SaveSession(
            new SaveSessionRequest { SessionId = "goal-4:tester", SessionJson = sessionJson },
            MockContext());

        var getResponse = await service.GetSession(
            new GetSessionRequest { SessionId = "goal-4:tester" },
            MockContext());

        Assert.True(getResponse.Found);
        Assert.Equal(sessionJson, getResponse.SessionJson);
    }

    [Fact]
    public async Task SaveSession_Overwrite_ReturnsUpdatedValue()
    {
        var (service, pipelineManager) = CreateService();
        CreatePipeline(pipelineManager, "goal-5");

        await service.SaveSession(
            new SaveSessionRequest { SessionId = "goal-5:coder", SessionJson = "first" },
            MockContext());

        await service.SaveSession(
            new SaveSessionRequest { SessionId = "goal-5:coder", SessionJson = "second" },
            MockContext());

        var response = await service.GetSession(
            new GetSessionRequest { SessionId = "goal-5:coder" },
            MockContext());

        Assert.True(response.Found);
        Assert.Equal("second", response.SessionJson);
    }

    [Fact]
    public async Task SaveSession_DifferentRoles_AreStoredIndependently()
    {
        var (service, pipelineManager) = CreateService();
        CreatePipeline(pipelineManager, "goal-6");

        await service.SaveSession(
            new SaveSessionRequest { SessionId = "goal-6:coder", SessionJson = "coder-data" },
            MockContext());

        await service.SaveSession(
            new SaveSessionRequest { SessionId = "goal-6:tester", SessionJson = "tester-data" },
            MockContext());

        var coderResponse = await service.GetSession(
            new GetSessionRequest { SessionId = "goal-6:coder" },
            MockContext());

        var testerResponse = await service.GetSession(
            new GetSessionRequest { SessionId = "goal-6:tester" },
            MockContext());

        Assert.Equal("coder-data", coderResponse.SessionJson);
        Assert.Equal("tester-data", testerResponse.SessionJson);
    }

    // ── SaveSession — unknown goal (does not throw) ───────────────────────────

    [Fact]
    public async Task SaveSession_UnknownGoalId_DoesNotThrow()
    {
        var (service, _) = CreateService();

        // No pipeline created — should silently do nothing
        var response = await service.SaveSession(
            new SaveSessionRequest { SessionId = "ghost-goal:coder", SessionJson = "{}" },
            MockContext());

        Assert.True(response.Success);
    }
}
