using CopilotHive.Services;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Tests;

public sealed class ConnectedWorkerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ConnectedWorker CreateWorker(string id = "worker-1") =>
        new()
        {
            Id = id,
            Role = WorkerRole.Coder,
            Capabilities = [],
        };

    // ── Constructor defaults ─────────────────────────────────────────────────

    #region Constructor_InitializesDefaults

    [Fact]
    public void Constructor_InitializesDefaults()
    {
        var before = DateTime.UtcNow;

        var worker = CreateWorker();

        Assert.False(worker.IsBusy);
        Assert.Null(worker.CurrentTaskId);
        Assert.False(worker.IsGeneric);
        Assert.True(worker.LastHeartbeat >= before);
    }

    #endregion

    // ── MarkBusy ─────────────────────────────────────────────────────────────

    #region MarkBusy_SetsIsBusyAndCurrentTaskId

    [Fact]
    public void MarkBusy_SetsIsBusyAndCurrentTaskId()
    {
        var worker = CreateWorker();
        const string taskId = "task-42";

        worker.IsBusy = true;
        worker.CurrentTaskId = taskId;

        Assert.True(worker.IsBusy);
        Assert.Equal(taskId, worker.CurrentTaskId);
    }

    #endregion

    // ── MarkIdle ─────────────────────────────────────────────────────────────

    #region MarkIdle_ClearsIsBusyAndCurrentTaskId

    [Fact]
    public void MarkIdle_ClearsIsBusyAndCurrentTaskId()
    {
        var worker = CreateWorker();
        worker.IsBusy = true;
        worker.CurrentTaskId = "task-42";

        worker.IsBusy = false;
        worker.CurrentTaskId = null;

        Assert.False(worker.IsBusy);
        Assert.Null(worker.CurrentTaskId);
    }

    #endregion

    // ── UpdateHeartbeat ──────────────────────────────────────────────────────

    #region UpdateHeartbeat_UpdatesLastHeartbeat

    [Fact]
    public void UpdateHeartbeat_UpdatesLastHeartbeat()
    {
        var worker = CreateWorker();
        var before = DateTime.UtcNow;

        worker.LastHeartbeat = DateTime.UtcNow;

        Assert.True(worker.LastHeartbeat >= before);
    }

    #endregion

    // ── MessageChannel ───────────────────────────────────────────────────────

    #region MessageChannel_IsWritableAndReadable

    [Fact]
    public async Task MessageChannel_IsWritableAndReadable()
    {
        var worker = CreateWorker();
        var message = new OrchestratorMessage
        {
            Assignment = new TaskAssignment { TaskId = "task-99" },
        };

        await worker.MessageChannel.Writer.WriteAsync(message, TestContext.Current.CancellationToken);
        worker.MessageChannel.Writer.Complete();

        var received = await worker.MessageChannel.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(message, received);
    }

    #endregion

    // ── IsStale ──────────────────────────────────────────────────────────────

    #region IsStale_ReturnsTrueWhenHeartbeatExceedsTimeout

    [Fact]
    public void IsStale_ReturnsTrueWhenHeartbeatExceedsTimeout()
    {
        var worker = CreateWorker();
        var timeout = TimeSpan.FromMinutes(1);

        worker.LastHeartbeat = DateTime.UtcNow - timeout - TimeSpan.FromMilliseconds(1);

        var isStale = DateTime.UtcNow - worker.LastHeartbeat > timeout;

        Assert.True(isStale);
    }

    #endregion
}
