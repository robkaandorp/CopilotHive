using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Extensions.AI;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Operation-time freshness (not merely creation-time eligibility). Reuses the real-account matrix fixture and
/// credential observer without modifying its protected files. Every observation window starts AFTER setup writes.
/// </summary>
public sealed partial class ComposerRuntimeManagerTests
{
    public static IEnumerable<object[]> OperationRepairInvalidations =>
        from original in new[] { false, true }
        from delete in new[] { false, true }
        from path in new[] { ProviderMatrixPath.Send, ProviderMatrixPath.CompactFull, ProviderMatrixPath.CompactPartial }
        select new object[] { original, delete, path };

    public static IEnumerable<object?[]> OperationRepairEnabledCases =>
        from selection in new (bool Original, string? Compaction)[]
        {
            (false, null), (false, "ollama-local/compact"),
            (true, null), (true, "copilot/compact"), (true, "   "),
        }
        from path in new[] { ProviderMatrixPath.Send, ProviderMatrixPath.CompactFull, ProviderMatrixPath.CompactPartial }
        select new object?[] { selection.Original, selection.Compaction, path };

    public static IEnumerable<object[]> OperationRepairNotificationInvalidations =>
        from original in new[] { false, true }
        from delete in new[] { false, true }
        from queued in new[] { false, true }
        select new object[] { original, delete, queued };

    private sealed record OperationObservation(int Queries, int OriginalMaterializations);

    private async Task ObserveOperationAccountsAsync()
    {
        var accounts = await _users.ListActivatedUsersAsync(Ct); // secret-free setup, not an operation under test
        _factory.CredentialReads.Start(accounts.Min(account => account.Id));
    }

    private OperationObservation OperationCheckpoint() => new(_factory.CredentialReads.Queries.Length,
        _factory.CredentialReads.OriginalAccountMaterializations);

    private void AssertOperationCredentialFree(OperationObservation before)
    {
        var queries = _factory.CredentialReads.Queries.Skip(before.Queries).ToArray();
        Assert.NotEmpty(queries); // this operation REALLY reached the observed current-account boundary
        Assert.DoesNotContain(queries, SelectsCredentialColumns);
        Assert.Equal(before.OriginalMaterializations, _factory.CredentialReads.OriginalAccountMaterializations);
    }

    private async Task InvalidateOperationAccountAsync(ProviderMatrixContext context, bool delete)
    {
        if (delete) await DeleteStoredAccountAsync(context.Principal.UserId);
        else await _users.SetUserEnabledAsync(context.Principal.UserId, false, Ct);
    }

    private static async Task<string?> OperationSendAndDrainAsync(Composer composer)
    {
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Updated() { if (!composer.IsStreaming) terminal.TrySetResult(); }
        composer.OnStreamingUpdate += Updated;
        try
        {
            var admission = composer.SendMessageWithEventsAsync("operation-repair-question");
            await GuardAsync(admission, "operation repair send admission");
            await GuardAsync(terminal.Task, "operation repair terminal stream callback");
            Assert.Contains("matrix-reply", composer.StreamingContent);
            return await admission;
        }
        finally { composer.OnStreamingUpdate -= Updated; }
    }

    // Unlike MatrixRunPathAsync, this does not seed/mutate history: denial must leave the pre-seeded session intact.
    private static async Task InvokeCachedOperationAsync(ProviderMatrixContext context, ProviderMatrixPath path)
    {
        switch (path)
        {
            case ProviderMatrixPath.Send:
                await OperationSendAndDrainAsync(context.Composer);
                break;
            case ProviderMatrixPath.CompactFull:
                Assert.True(await context.Composer.CompactSessionAsync(Ct));
                break;
            case ProviderMatrixPath.CompactPartial:
                Assert.True(await context.Composer.CompactOldestPercentAsync(50, Ct));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(path));
        }
    }

    [Theory]
    [MemberData(nameof(OperationRepairInvalidations))]
    public async Task OperationRepair_DenialPreservesManagerRuntimeAndHistory_WithFreshSecretFreeRead(
        bool original, bool delete, ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(original: original, primary: original ? "copilot/main" : "ollama-local/main");
        Assert.True(context.Composer.IsConnected);
        MatrixSeedCompaction(context.Service);
        var session = context.Service.Session;
        var history = session.MessageHistory.ToArray();
        var agent = context.Service.Agent;
        var client = context.Service.ChatClient;
        await ObserveOperationAccountsAsync();
        await InvalidateOperationAccountAsync(context, delete);
        var observation = OperationCheckpoint();
        var counts = MatrixCounts(context);
        var compacting = 0;
        var compacted = 0;
        var streaming = 0;
        void Compacting() => Interlocked.Increment(ref compacting);
        void Compacted() => Interlocked.Increment(ref compacted);
        void Streaming() => Interlocked.Increment(ref streaming);
        context.Composer.OnCompactingStarted += Compacting;
        context.Composer.OnCompacted += Compacted;
        context.Composer.OnStreamingUpdate += Streaming;
        try
        {
            var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => InvokeCachedOperationAsync(context, path));
            Assert.Equal(delete ? ProviderDenyReason.UnknownAccount : ProviderDenyReason.DisabledAccount, denied.Reason);
            AssertOperationCredentialFree(observation);
            AssertMatrixNoProviderWork(context, counts);
            Assert.True(context.Composer.IsConnected);
            Assert.False(context.Composer.IsStreaming);
            Assert.False(context.Composer.IsCompacting);
            Assert.Same(agent, context.Service.Agent);
            Assert.Same(client, context.Service.ChatClient);
            Assert.Same(session, context.Service.Session);
            Assert.Equal(history, session.MessageHistory.ToArray());
            Assert.Equal(0, compacting);
            Assert.Equal(0, compacted);
            Assert.Equal(0, streaming); // no start/error/finish publication on a caller's admission refusal

            foreach (var result in new[] { await Request(context.Manager, context.Principal), await Retry(context.Manager, context.Principal) })
            {
                Assert.Equal(ComposerRuntimeStatus.Refused, result.Status);
                Assert.Equal(delete ? ComposerRuntimeRefusal.UnknownAccount : ComposerRuntimeRefusal.DisabledAccount, result.Refusal);
                Assert.Null(result.Runtime);
                Assert.Null(result.Failure);
            }
            if (!delete)
            {
                await _users.SetUserEnabledAsync(context.Principal.UserId, true, Ct);
                var recovered = await Request(context.Manager, context.Principal);
                Assert.Equal(ComposerRuntimeStatus.Connected, recovered.Status);
                Assert.Same(context.Runtime, recovered.Runtime); // refusal did not poison or remove the cache entry
                var restored = OperationCheckpoint();
                await InvokeCachedOperationAsync(context, path);
                AssertOperationCredentialFree(restored);
                Assert.Same(agent, context.Service.Agent);
                Assert.Same(client, context.Service.ChatClient);
                Assert.Equal(counts.Requests + 1, context.Telemetry.Requests);
                Assert.Equal(counts.Clients, _clientCreations);
                Assert.Equal(counts.Probes, Volatile.Read(ref _tokenProbes));
                Assert.Equal(counts.Disposals, context.Telemetry.DisposedModels.Count);
            }
        }
        finally
        {
            context.Composer.OnCompactingStarted -= Compacting;
            context.Composer.OnCompacted -= Compacted;
            context.Composer.OnStreamingUpdate -= Streaming;
        }
    }

    [Theory]
    [MemberData(nameof(OperationRepairEnabledCases))]
    public async Task OperationRepair_EnabledCachedOperation_ReadsCurrentAccountWithoutCredentials_AndStillInvokesExpectedModel(
        bool original, string? compaction, ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(original: original, primary: original ? "copilot/main" : "ollama-local/main", compaction: compaction);
        MatrixSeedCompaction(context.Service);
        await ObserveOperationAccountsAsync();
        var before = OperationCheckpoint();
        var counts = MatrixCounts(context);
        await InvokeCachedOperationAsync(context, path);
        AssertOperationCredentialFree(before);
        Assert.Equal(counts.Requests + 1, context.Telemetry.Requests);
        Assert.Equal(counts.Clients, _clientCreations);
        Assert.Equal(counts.Probes, Volatile.Read(ref _tokenProbes));
        Assert.Equal(counts.Connects, context.Telemetry.Connects);
        Assert.Equal(counts.Disposals, context.Telemetry.DisposedModels.Count);
        var expected = path == ProviderMatrixPath.Send || string.IsNullOrEmpty(compaction) ? context.Service.Model : compaction;
        Assert.Equal(new[] { expected }, context.Telemetry.InvokedModels.Skip(counts.Requests).ToArray());
    }

    [Theory]
    [InlineData(ProviderMatrixPath.Send)]
    [InlineData(ProviderMatrixPath.CompactFull)]
    [InlineData(ProviderMatrixPath.CompactPartial)]
    public async Task OperationRepair_UsesActualSelectionAndCapturedCompaction_NotReloadedCopilotDefaults(ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(primary: "copilot/main");
        Assert.False(context.Composer.IsConnected);
        await context.Composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct);
        Assert.True(context.Composer.IsConnected);
        context.Config.SetCompactionModel("copilot/compact");
        MatrixSeedCompaction(context.Service);
        var counts = MatrixCounts(context);
        var before = OperationCheckpoint();
        await InvokeCachedOperationAsync(context, path);
        AssertOperationCredentialFree(before);
        Assert.Equal("ollama-local/other", context.Composer.CurrentModel);
        Assert.Null(context.Runtime.Configuration.CompactionModel);
        Assert.Equal(new[] { "ollama-local/other" }, context.Telemetry.InvokedModels.Skip(counts.Requests).ToArray());
        Assert.Equal(counts.Clients, _clientCreations);
        Assert.Equal(counts.Probes, Volatile.Read(ref _tokenProbes));
    }

    [Fact]
    public async Task OperationRepair_SendDenialRestoresPendingEvents_AndAValidRetryDeliversThemOnce()
    {
        var context = await ProviderMatrixAsync();
        var events = new[]
        {
            new SystemEvent(EventType.GoalCompleted, "operation-event-one", GoalId: "operation-one"),
            new SystemEvent(EventType.GoalCompleted, "operation-event-two", GoalId: "operation-two"),
        };
        foreach (var item in events) _bus.Publish(item);
        await _users.SetUserEnabledAsync(context.Principal.UserId, false, Ct);
        var counts = MatrixCounts(context);
        var before = OperationCheckpoint();
        var error = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => context.Composer.SendMessageWithEventsAsync("rejected"));
        Assert.Equal(ProviderDenyReason.DisabledAccount, error.Reason);
        AssertOperationCredentialFree(before);
        AssertMatrixNoProviderWork(context, counts);
        var pending = context.Runtime.Subscriber!.PeekPendingEvents();
        Assert.Equal(2, pending.Count);
        Assert.Same(events[0], pending[0]);
        Assert.Same(events[1], pending[1]);
        await _users.SetUserEnabledAsync(context.Principal.UserId, true, Ct);
        var block = await OperationSendAndDrainAsync(context.Composer);
        Assert.NotNull(block);
        Assert.Equal(1, CountOccurrences(block, "operation-event-one"));
        Assert.Equal(1, CountOccurrences(block, "operation-event-two"));
        Assert.True(block.IndexOf("operation-event-one", StringComparison.Ordinal) < block.IndexOf("operation-event-two", StringComparison.Ordinal));
        Assert.Empty(context.Runtime.Subscriber.PeekPendingEvents());
        Assert.Null(await OperationSendAndDrainAsync(context.Composer));
        Assert.Equal(counts.Requests + 2, context.Telemetry.Requests);
    }

    /// <summary>A deterministic, child-owned first-call rendezvous; invocation counted at entry, before parking.</summary>
    private sealed class OperationNotificationClient(string model, MatrixTelemetry telemetry, bool parkFirst) : IChatClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Payloads { get; } = new();
        public ChatClientMetadata Metadata => new("operation-notification", null, model);
        public void ReleaseFirst() => _release.TrySetResult();
        public void Dispose() { ReleaseFirst(); telemetry.DisposedModels.Enqueue(model); }
        public object? GetService(Type type, object? key = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("Only streams are used by notification tests.");
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            Interlocked.Increment(ref telemetry.Requests);
            telemetry.InvokedModels.Enqueue(model);
            Payloads.Enqueue(string.Join("\n", messages.Select(message => message.Text)));
            FirstEntered.TrySetResult();
            if (parkFirst && call == 1) await _release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "matrix-reply") { FinishReason = ChatFinishReason.Stop };
        }
    }

    private async Task<OperationNotificationClient> InstallOperationNotificationClientAsync(ProviderMatrixContext context, bool parkFirst)
    {
        var client = new OperationNotificationClient(context.Service.Model!, context.Telemetry, parkFirst);
        _clientFactory = _ => client;
        try { await context.Composer.ConnectAsync(Ct); }
        catch { client.ReleaseFirst(); throw; }
        return client; // runtime owns disposal; test owns release and joining every started operation
    }

    private static async Task ObserveNotificationTerminalAsync(Composer composer, Action launch)
    {
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Updated() { if (!composer.IsStreaming) terminal.TrySetResult(); }
        composer.OnStreamingUpdate += Updated;
        try
        {
            launch();
            await GuardAsync(terminal.Task, "notification actor terminal callback");
        }
        finally { composer.OnStreamingUpdate -= Updated; }
    }

    private static async Task ProveNotificationQueuedAsync(Composer composer)
    {
        // Mailbox FIFO barrier: the actor must process the earlier notification before replying to this harmless
        // streaming-rejected selection. No polling of a private queue and no timing assumptions.
        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct));
        Assert.Equal("Cannot switch model while streaming.", rejection.Message);
    }

    [Theory]
    [MemberData(nameof(OperationRepairNotificationInvalidations))]
    public async Task OperationRepair_ActiveNotificationAndQueuedChain_RecheckAccountBeforeEveryCachedInvocation(
        bool original, bool delete, bool queued)
    {
        var context = await ProviderMatrixAsync(original: original, primary: original ? "copilot/main" : "ollama-local/main");
        var client = await InstallOperationNotificationClientAsync(context, parkFirst: queued);
        await ObserveOperationAccountsAsync();
        Task<string?>? foreground = null;
        try
        {
            if (queued)
            {
                foreground = context.Composer.SendMessageWithEventsAsync("foreground-marker");
                await GuardAsync(foreground, "foreground stream admitted");
                await GuardAsync(client.FirstEntered.Task, "foreground model call entered before queueing");
                context.Composer.SendActiveNotification("queued-marker", "{{CHV1:E0|}}queued-marker");
                await ProveNotificationQueuedAsync(context.Composer);
            }
            await InvalidateOperationAccountAsync(context, delete);
            var counts = MatrixCounts(context);
            var before = OperationCheckpoint();
            await ObserveNotificationTerminalAsync(context.Composer, () =>
            {
                if (queued) client.ReleaseFirst();
                else context.Composer.SendActiveNotification("idle-marker", "{{CHV1:E0|}}idle-marker");
            });
            AssertMatrixNoProviderWork(context, counts);
            AssertOperationCredentialFree(before);
            var reason = delete ? ProviderDenyReason.UnknownAccount : ProviderDenyReason.DisabledAccount;
            Assert.Contains($"The selected model provider is not available to this account ({reason}).", context.Composer.StreamingContent);
            Assert.True(context.Composer.IsConnected);
            Assert.False(context.Composer.IsStreaming);
            Assert.DoesNotContain(client.Payloads, payload => payload.Contains(queued ? "queued-marker" : "idle-marker", StringComparison.Ordinal));
            if (queued)
            {
                Assert.Single(client.Payloads);
                Assert.Contains("foreground-marker", client.Payloads.Single());
            }
            else Assert.Empty(client.Payloads);
            Assert.Equal(delete ? ComposerRuntimeRefusal.UnknownAccount : ComposerRuntimeRefusal.DisabledAccount,
                (await Request(context.Manager, context.Principal)).Refusal);
        }
        finally
        {
            client.ReleaseFirst();
            if (foreground is not null) await GuardAsync(foreground, "owned foreground admission completion");
            // Join the actual actor/stream teardown even on assertion failure before fixture resources go away.
            await GuardAsync(context.Manager.ShutdownAsync(), "notification fixture joins actor and owned stream");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OperationRepair_EnabledActiveNotificationAndQueuedChain_StillExecuteWithFreshSecretFreeReads(bool original, bool queued)
    {
        var context = await ProviderMatrixAsync(original: original, primary: original ? "copilot/main" : "ollama-local/main");
        var client = await InstallOperationNotificationClientAsync(context, parkFirst: queued);
        await ObserveOperationAccountsAsync();
        Task<string?>? foreground = null;
        try
        {
            if (queued)
            {
                foreground = context.Composer.SendMessageWithEventsAsync("foreground-marker");
                await GuardAsync(foreground, "enabled foreground admitted");
                await GuardAsync(client.FirstEntered.Task, "enabled foreground model entered");
                context.Composer.SendActiveNotification("queued-marker", "{{CHV1:E0|}}queued-marker");
                await ProveNotificationQueuedAsync(context.Composer);
            }
            var counts = MatrixCounts(context);
            var before = OperationCheckpoint();
            await ObserveNotificationTerminalAsync(context.Composer, () =>
            {
                if (queued) client.ReleaseFirst();
                else context.Composer.SendActiveNotification("idle-marker", "{{CHV1:E0|}}idle-marker");
            });
            AssertOperationCredentialFree(before);
            Assert.Equal(counts.Requests + 1, context.Telemetry.Requests);
            Assert.Equal(counts.Clients, _clientCreations);
            Assert.Equal(counts.Probes, Volatile.Read(ref _tokenProbes));
            Assert.Equal(queued ? 2 : 1, client.Payloads.Count);
            Assert.Contains(queued ? "queued-marker" : "idle-marker", client.Payloads.Last());
            Assert.Equal("matrix-reply", context.Composer.StreamingContent);
        }
        finally
        {
            client.ReleaseFirst();
            if (foreground is not null) await GuardAsync(foreground, "enabled foreground admission completion");
            await GuardAsync(context.Manager.ShutdownAsync(), "enabled notification fixture joins actor and stream");
        }
    }

    [Theory]
    [InlineData(ProviderMatrixPath.CompactFull)]
    [InlineData(ProviderMatrixPath.CompactPartial)]
    public async Task OperationRepair_CompactionCancellationDuringFreshnessCheck_ReturnsFalseWithoutCallbacksOrModelUse(ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync();
        MatrixSeedCompaction(context.Service);
        var originalGate = InstanceField<ProviderGate>(context.Service, "_providerGate");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderGate parked = async (p, c, ct) =>
        {
            var decision = await originalGate(p, c, ct); // exercise the REAL credential-free directory read first
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return decision;
        };
        var gateField = typeof(ComposerAgentService).GetField("_providerGate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        gateField.SetValue(context.Service, parked); // test-only wrapper of an existing delegate; no production seam
        using var cancellation = new CancellationTokenSource();
        Task<bool>? operation = null;
        ExceptionDispatchInfo? primary = null;
        Exception? cleanupFailure = null;
        var callbacks = 0;
        void Compacting() => Interlocked.Increment(ref callbacks);
        context.Composer.OnCompactingStarted += Compacting;
        context.Composer.OnCompacted += Compacting;
        var counts = MatrixCounts(context);
        var before = OperationCheckpoint();
        try
        {
            operation = path == ProviderMatrixPath.CompactFull
                ? context.Composer.CompactSessionAsync(cancellation.Token)
                : context.Composer.CompactOldestPercentAsync(50, cancellation.Token);
            await GuardAsync(entered.Task, "compaction parked after real freshness read");
            await cancellation.CancelAsync();
            await GuardAsync(operation, "compaction cancellation settled");
            Assert.False(await operation);
            Assert.Equal(0, callbacks);
            AssertMatrixNoProviderWork(context, counts);
            AssertOperationCredentialFree(before);
            Assert.True(context.Composer.IsConnected);
        }
        catch (Exception error) { primary = ExceptionDispatchInfo.Capture(error); }
        finally
        {
            try
            {
                await CompleteCleanupAsync(new Func<Task>[]
                {
                    () => { release.TrySetResult(); return Task.CompletedTask; },
                    async () => await cancellation.CancelAsync(),
                    () => operation is null ? Task.CompletedTask : GuardAsync(operation, "owned compaction task terminal"),
                    () => { gateField.SetValue(context.Service, originalGate); return Task.CompletedTask; },
                    () =>
                    {
                        context.Composer.OnCompactingStarted -= Compacting;
                        context.Composer.OnCompacted -= Compacting;
                        return Task.CompletedTask;
                    },
                });
            }
            catch (Exception error) { cleanupFailure = error; }
        }
        if (primary is not null)
        {
            if (cleanupFailure is not null) primary.SourceException.Data["CleanupFailure"] = cleanupFailure;
            primary.Throw();
        }
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
}
