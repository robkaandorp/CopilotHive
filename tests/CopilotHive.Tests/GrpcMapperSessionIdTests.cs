using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests verifying that <see cref="GrpcMapper"/> correctly maps
/// <see cref="WorkTask.SessionId"/> to/from <see cref="TaskAssignment.SessionId"/>.
/// </summary>
public sealed class GrpcMapperSessionIdTests
{
    private static WorkTask BuildTask(string sessionId = "goal-1:coder") => new()
    {
        TaskId = "task-1",
        GoalId = "goal-1",
        GoalDescription = "Test",
        Prompt = "Do it",
        Role = DomainWorkerRole.Coder,
        Repositories = [],
        SessionId = sessionId,
    };

    // ── ToGrpc ────────────────────────────────────────────────────────────────

    [Fact]
    public void ToGrpc_SessionId_IsMapped()
    {
        var task = BuildTask("goal-x:coder");
        var assignment = GrpcMapper.ToGrpc(task);
        Assert.Equal("goal-x:coder", assignment.SessionId);
    }

    [Fact]
    public void ToGrpc_EmptySessionId_MapsToEmpty()
    {
        var task = BuildTask("");
        var assignment = GrpcMapper.ToGrpc(task);
        Assert.Equal("", assignment.SessionId);
    }

    // ── ToDomain ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToDomain_SessionId_IsMapped()
    {
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            SessionId = "goal-abc:tester",
        };
        var task = GrpcMapper.ToDomain(assignment);
        Assert.Equal("goal-abc:tester", task.SessionId);
    }

    [Fact]
    public void ToDomain_MissingSessionId_DefaultsToEmpty()
    {
        var assignment = new TaskAssignment
        {
            TaskId = "t",
            GoalId = "g",
            GoalDescription = "d",
            Prompt = "p",
            // SessionId not set — proto3 default is empty string
        };
        var task = GrpcMapper.ToDomain(assignment);
        Assert.Equal("", task.SessionId);
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("goal-1:coder")]
    [InlineData("goal-x:tester")]
    [InlineData("my-goal:reviewer")]
    [InlineData("")]
    public void SessionId_RoundTrip_PreservesValue(string sessionId)
    {
        var original = BuildTask(sessionId);
        var assignment = GrpcMapper.ToGrpc(original);
        var restored = GrpcMapper.ToDomain(assignment);
        Assert.Equal(sessionId, restored.SessionId);
    }
}
