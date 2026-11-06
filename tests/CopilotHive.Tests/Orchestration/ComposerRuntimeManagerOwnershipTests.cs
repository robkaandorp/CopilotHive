using System.Runtime.ExceptionServices;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CopilotHive.Tests.Orchestration;

public sealed partial class ComposerRuntimeManagerTests
{
    // Installed in the ACTUAL provider handed to ComposerFactory, not merely a nearby coordinator.
    // Its counters are necessary because UserService/coordinator intentionally swallow hook faults.
    private sealed class LegacyIsolationSentinel : IAsyncDisposable
    {
        private readonly UserService _users;
        private readonly Action _tokenHandler;
        private int _connects;
        private int _probes;
        private int _signals;
        private int _transitions;
        private bool _armed;

        public LlmConnectionCoordinator Coordinator { get; }
        public TaskCompletionSource Faulted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Signals => Volatile.Read(ref _signals);

        public LegacyIsolationSentinel(UserService users)
        {
            _users = users;
            Coordinator = new LlmConnectionCoordinator(Model, null, oauthEnabled: true,
                _ =>
                {
                    Interlocked.Increment(ref _connects);
                    throw new InvalidOperationException("managed path touched legacy coordinator connect");
                },
                () =>
                {
                    if (_armed)
                    {
                        Interlocked.Increment(ref _probes);
                        throw new InvalidOperationException("managed path touched legacy coordinator probe");
                    }
                    return false;
                }, _ => "copilot");
            // PendingConnect startup has no asynchronous work. Verify, don't assume, that fact.
            var started = Coordinator.StartAsync();
            Assert.True(started.IsCompletedSuccessfully);
            Assert.Equal(ComposerState.PendingConnect, Coordinator.State);
            Coordinator.StateChanged += OnStateChanged;
            Coordinator.SubscribeTokenSignal(h => users.TokenAvailable += h, h => users.TokenAvailable -= h);
            _tokenHandler = () =>
            {
                Interlocked.Increment(ref _signals);
                throw new InvalidOperationException("managed path raised UserService.TokenAvailable");
            };
            users.TokenAvailable += _tokenHandler;
            _armed = true;
        }

        private void OnStateChanged(ComposerState state)
        {
            Interlocked.Increment(ref _transitions);
            if (state == ComposerState.Faulted) Faulted.TrySetResult();
        }

        public void AssertUntouched()
        {
            Assert.Equal(0, Volatile.Read(ref _connects));
            Assert.Equal(0, Volatile.Read(ref _probes));
            Assert.Equal(0, Volatile.Read(ref _signals));
            Assert.Equal(0, Volatile.Read(ref _transitions));
            Assert.Equal(ComposerState.PendingConnect, Coordinator.State);
        }

        public async ValueTask DisposeAsync()
        {
            _users.TokenAvailable -= _tokenHandler;
            Coordinator.StateChanged -= OnStateChanged;
            await GuardAsync(Coordinator.DisposeAsync().AsTask(), "legacy isolation sentinel disposal");
        }
    }

    [Fact]
    public async Task LegacyIsolationSentinel_PositiveControls_DetectActualCoordinatorAndTokenSignal()
    {
        await TwoUsersAsync();
        await using var sentinel = new LegacyIsolationSentinel(_users);
        sentinel.AssertUntouched();
        sentinel.Coordinator.OnTokenAvailable();
        await GuardAsync(sentinel.Faulted.Task, "positive-control legacy attempt faulted");
        var coordinatorViolation = Record.Exception(sentinel.AssertUntouched);
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(coordinatorViolation);

        // The real directory raises the actual event after a token commit. A throwing handler is
        // swallowed by production, so assert the observed counter as well as sentinel rejection.
        await _users.CreateOrUpdateUserAsync("100", "first", null, null, null,
            "positive-control-token", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        Assert.Equal(1, sentinel.Signals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealFactory_CompactionProviderControlsDeferredOutcome(bool copilotCompaction)
    {
        var (_, second) = await TwoUsersAsync();
        const string primary = "ollama-local/main";
        var compaction = copilotCompaction ? "copilot/compact" : "ollama-local/compact";
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = primary }, new ModelEntry { Name = compaction }],
                CompactionModel = compaction,
            },
            Composer = new ComposerConfig { Model = primary },
        };
        var providersObserved = new List<string>();
        _providerResolver = model =>
        {
            providersObserved.Add(model);
            return model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local";
        };
        _tokenAvailable = false;
        var manager = NewManager(BuildProvider(authEnabled: true, config: config));
        await using var gate = Park();
        gate.Release();
        var result = await Request(manager, second);

        Assert.Equal(copilotCompaction ? ComposerRuntimeStatus.Deferred : ComposerRuntimeStatus.Connected, result.Status);
        Assert.True(result.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.None, result.Refusal);
        Assert.Null(result.Failure);
        Assert.Equal(primary, result.Runtime!.Composer.StartupDefaultModel);
        Assert.Equal(!copilotCompaction, result.Runtime.Composer.IsConnected);
        Assert.Equal([primary, compaction], providersObserved);
        Assert.Equal(copilotCompaction ? 0 : 1, gate.Calls);
        Assert.Equal(copilotCompaction ? 0 : 2, _clientCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightConnection_WithoutRemoval_AdmitsCreateAndConnectWaiters(bool deferred)
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        ComposerUserRuntime? before = null;
        if (deferred)
        {
            _provider = "copilot";
            _tokenAvailable = false;
            before = (await Request(manager, second)).Runtime;
            _tokenAvailable = true;
        }
        await using var gate = Park();
        var initiator = deferred ? Retry(manager, second) : Request(manager, second);
        await GuardAsync(gate.Entered.Task, "normal shared operation entered connect");
        var createWaiter = Request(manager, second);
        var connectWaiter = Retry(manager, second);
        Assert.False(initiator.IsCompleted);
        Assert.False(createWaiter.IsCompleted);
        Assert.False(connectWaiter.IsCompleted);
        gate.Release();
        var results = await Bounded(Task.WhenAll(initiator, createWaiter, connectWaiter), "normally admitted waiters");
        Assert.All(results, result =>
        {
            Assert.Equal(ComposerRuntimeStatus.Connected, result.Status);
            Assert.True(result.IsGranted);
            Assert.Null(result.Failure);
            Assert.Equal(ComposerRuntimeRefusal.None, result.Refusal);
            Assert.Same(results[0].Runtime, result.Runtime);
        });
        if (deferred) Assert.Same(before, results[0].Runtime);
        Assert.Same(results[0].Runtime, (await Request(manager, second)).Runtime);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(1, _clientCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_InsideRealComposerConnect_CancelsOnlyCallerWaitAndKeepsCache(bool deferred)
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        ComposerUserRuntime? before = null;
        if (deferred)
        {
            _provider = "copilot";
            _tokenAvailable = false;
            before = (await Request(manager, second)).Runtime;
            _tokenAvailable = true;
        }
        using var callerCts = new CancellationTokenSource();
        using var releaseClient = new ManualResetEventSlim(false);
        var clientEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sharedToken = default;
        Composer? actualComposer = null;
        _connectHook = async (composer, token) =>
        {
            actualComposer = composer;
            sharedToken = token;
            using var registration = token.Register(() => sharedCancelled.TrySetResult());
            await composer.ConnectAsync(token); // forward to the REAL actor/agent path
        };
        _clientFactory = _ =>
        {
            clientEntered.TrySetResult(); // deeper than the manager delegate: real agent is connecting
            Assert.True(releaseClient.Wait(TimeSpan.FromSeconds(30)), "Timed out: release real Composer client construction");
            return new Mock<IChatClient>().Object;
        };
        Task<ComposerRuntimeResult>? cancelled = null;
        Task<ComposerRuntimeResult>? survivor = null;
        Task<ComposerRuntimeResult>? late = null;
        ExceptionDispatchInfo? primary = null;
        Exception? cleanupFailure = null;
        try
        {
            cancelled = deferred ? Retry(manager, second, callerCts.Token) : Request(manager, second, callerCts.Token);
            await GuardAsync(clientEntered.Task, "real Composer entered chat-client construction");
            survivor = Retry(manager, second);
            Assert.False(cancelled.IsCompleted);
            Assert.False(survivor.IsCompleted);
            Assert.NotEqual(callerCts.Token, sharedToken);
            await callerCts.CancelAsync();
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Bounded(cancelled, "cancelled real-connect caller"));
            Assert.Equal(callerCts.Token, cancellation.CancellationToken);
            Assert.False(sharedToken.IsCancellationRequested);
            Assert.False(sharedCancelled.Task.IsCompleted);
            Assert.False(survivor.IsCompleted);
            late = Request(manager, second);
            Assert.False(late.IsCompleted); // cache not evicted while shared connection still runs
            releaseClient.Set();
            var successful = await Bounded(Task.WhenAll(survivor, late), "real-connect surviving waiters");
            Assert.All(successful, result =>
            {
                Assert.Equal(ComposerRuntimeStatus.Connected, result.Status);
                Assert.True(result.Runtime!.Composer.IsConnected);
                Assert.Same(actualComposer, result.Runtime.Composer);
                Assert.Same(successful[0].Runtime, result.Runtime);
                Assert.Null(result.Failure);
            });
            var runtime = successful[0].Runtime!;
            if (deferred) Assert.Same(before, runtime);
            Assert.Same(runtime, (await Request(manager, second)).Runtime);
            Assert.Same(runtime, (await Retry(manager, second)).Runtime);
            await runtime.Composer.SaveSessionAsync(Ct);
            Assert.True(File.Exists(ComposerUserState.SessionFilePath(StateDir, runtime.State)));
            Assert.Equal(1, _clientCreations);
            Assert.Equal(["composer:2"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            releaseClient.Set();
            try
            {
                foreach (var task in new[] { cancelled, survivor, late }.OfType<Task<ComposerRuntimeResult>>())
                    await JoinRequestAsync(task);
                // No handle may have escaped if an assertion failed before survivors were started.
                await GuardAsync(Remove(manager, second.UserId), "real-connect failure-path removal");
            }
            catch (Exception ex) { cleanupFailure = ex; }
        }
        if (primary is not null)
        {
            if (cleanupFailure is not null) primary.SourceException.Data["CleanupFailure"] = cleanupFailure;
            primary.Throw();
        }
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredConnect_RechecksStaleAccountOnEveryJoinedWait_WithoutPoisoningCache(bool invalidateRole)
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        _provider = "copilot";
        _tokenAvailable = false;
        var deferred = (await Request(manager, second)).Runtime!;
        _tokenAvailable = true;
        await using var gate = Park();
        var connector = Retry(manager, second);
        await GuardAsync(gate.Entered.Task, "deferred connection parked before account mutation");
        var connectWaiter = Retry(manager, second);
        var createWaiter = Request(manager, second);
        Assert.False(connectWaiter.IsCompleted);
        Assert.False(createWaiter.IsCompleted);
        try
        {
            if (invalidateRole) await SetStoredRoleAsync(second, "retired-role");
            else await _users.SetUserEnabledAsync(second.UserId, false, Ct);
            Assert.True(second.Enabled);
            Assert.Equal(UserRole.Member, second.Role);
            gate.Release();
            foreach (var call in new[] { connector, connectWaiter, createWaiter })
            {
                if (invalidateRole) await AssertInvalidRoleAsync(call);
                else
                {
                    var refused = await Bounded(call, "disabled deferred-connection waiter refused");
                    Assert.Equal(ComposerRuntimeStatus.Refused, refused.Status);
                    Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, refused.Refusal);
                    Assert.False(refused.IsGranted);
                    Assert.Null(refused.Runtime);
                    Assert.Null(refused.Failure);
                }
            }
        }
        finally
        {
            gate.Release();
            foreach (var call in new[] { connector, connectWaiter, createWaiter }) await JoinRequestAsync(call);
            if (invalidateRole) await SetStoredRoleAsync(second, "member");
            else
            {
                var fresh = await _users.GetActivatedUserByIdAsync(second.UserId, CancellationToken.None);
                if (fresh is { Enabled: false }) await _users.SetUserEnabledAsync(second.UserId, true, CancellationToken.None);
            }
        }
        var repaired = await Retry(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, repaired.Status);
        Assert.Same(deferred, repaired.Runtime);
        Assert.Same(deferred, (await Request(manager, second)).Runtime);
        Assert.Same(deferred.Attachments, repaired.Runtime!.Attachments);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(1, _clientCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectFailure_CleansMatchingPendingAttachments_PreservesHistoryAndOtherUser(bool deferred)
    {
        var (first, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var survivor = (await Request(manager, first)).Runtime!;
        using var otherContent = new MemoryStream([9, 8, 7, 6]);
        var otherAttachment = await survivor.Attachments.SaveAsync("unrelated.png", otherContent, Ct);
        Assert.True(otherAttachment.Success, otherAttachment.Error);
        var otherPath = Path.Combine(survivor.Attachments.AttachmentsRootPath, otherAttachment.Attachment!.SavedRelativePath);
        var otherBefore = await File.ReadAllBytesAsync(otherPath, Ct);
        var sessionPath = Path.Combine(StateDir, "composer", "users", "2", "session.json");
        await WriteSessionAsync(sessionPath, "failure-preserved-session-marker");
        var sessionBefore = await File.ReadAllBytesAsync(sessionPath, Ct);
        ComposerUserRuntime? deferredRuntime = null;
        if (deferred)
        {
            _provider = "copilot";
            _tokenAvailable = false;
            deferredRuntime = (await Request(manager, second)).Runtime;
            _tokenAvailable = true;
        }
        Composer? failedComposer = null;
        ComposerAttachmentService? matchingAttachments = null;
        ComposerAttachment? pending = null;
        string? pendingPath = null;
        var failedClient = new Mock<IChatClient>();
        _clientFactory = _ => failedClient.Object;
        var sentinel = new InvalidOperationException("fault after connection allocated its real resources");
        var agentDisposals = 0;
        _connectHook = async (composer, token) =>
        {
            failedComposer = composer;
            matchingAttachments = Field<ComposerAttachmentService>(composer, "_attachmentService");
            if (deferredRuntime is not null) Assert.Same(deferredRuntime.Attachments, matchingAttachments);
            Assert.Equal(Path.GetFullPath(Path.Combine(StateDir, "composer", "users", "2", "attachments")), matchingAttachments.AttachmentsRootPath);
            using var content = new MemoryStream([1, 4, 1, 4]);
            var saved = await matchingAttachments.SaveAsync("pending.png", content, token);
            Assert.True(saved.Success, saved.Error);
            pending = saved.Attachment!;
            pendingPath = Path.Combine(matchingAttachments.AttachmentsRootPath, pending.SavedRelativePath);
            Assert.True(File.Exists(pendingPath));
            await composer.ConnectAsync(token);
            Assert.Equal(["failure-preserved-session-marker"], composer.GetChatHistory().Select(message => message.Content));
            Field<ComposerAgentService>(composer, "_agentService").OnAgentDisposing = _ =>
            {
                Interlocked.Increment(ref agentDisposals);
                throw new IOException("deliberate secondary agent-cleanup fault");
            };
            throw sentinel;
        };

        var result = await Bounded(deferred ? Retry(manager, second) : Request(manager, second), "fault completes after resource cleanup");

        Assert.Equal(ComposerRuntimeStatus.Failed, result.Status);
        Assert.Same(sentinel, result.Failure);
        Assert.False(result.IsGranted);
        Assert.Null(result.Runtime);
        Assert.Equal(ComposerRuntimeRefusal.None, result.Refusal);
        Assert.NotNull(failedComposer);
        Assert.True(ActorCompleted(failedComposer));
        failedClient.Verify(client => client.Dispose(), Times.Once);
        Assert.Equal(1, Volatile.Read(ref agentDisposals));
        Assert.NotNull(matchingAttachments);
        Assert.NotNull(pending);
        Assert.False(File.Exists(pendingPath));
        var missing = matchingAttachments.MarkAsSent(pending);
        Assert.False(missing.Success);
        Assert.Equal("unknown attachment", missing.Error); // authoritative tracked record is gone too
        Assert.Equal(sessionBefore, await File.ReadAllBytesAsync(sessionPath, Ct));
        Assert.Equal(otherBefore, await File.ReadAllBytesAsync(otherPath, Ct));
        Assert.Same(survivor, (await Request(manager, first)).Runtime);
        Assert.Same(survivor.Attachments, (await Request(manager, first)).Runtime!.Attachments);
        Assert.True(survivor.Composer.IsConnected);
        Assert.Equal(["composer:1"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal);

        _connectHook = null;
        _clientFactory = _ => new Mock<IChatClient>().Object;
        var recreated = await Request(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, recreated.Status);
        Assert.NotSame(failedComposer, recreated.Runtime!.Composer);
        Assert.NotSame(matchingAttachments, recreated.Runtime.Attachments);
        Assert.Equal(["failure-preserved-session-marker"], recreated.Runtime.Composer.GetChatHistory().Select(message => message.Content));
        Assert.Equal(sessionBefore, await File.ReadAllBytesAsync(sessionPath, Ct));
        Assert.Equal(otherBefore, await File.ReadAllBytesAsync(otherPath, Ct));
        Assert.Equal(["composer:1", "composer:2"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalMigrationFailure_ReturnsTypedFailure_PreservesBothHistoriesAndAllowsFreshRetry(bool conflictingDestination)
    {
        var (first, _) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var legacyPath = Path.Combine(StateDir, "composer-session.json");
        var userPath = Path.Combine(StateDir, "composer", "users", "1", "session.json");
        if (conflictingDestination)
        {
            await WriteSessionAsync(legacyPath, "conflicting-legacy-history-marker");
            await WriteSessionAsync(userPath, "existing-user-history-marker");
        }
        else await File.WriteAllTextAsync(legacyPath, "{corrupt-legacy-history-marker", Ct);
        var legacyBefore = await File.ReadAllBytesAsync(legacyPath, Ct);
        var userBefore = conflictingDestination ? await File.ReadAllBytesAsync(userPath, Ct) : null;
        var failed = await Request(manager, first);

        Assert.Equal(ComposerRuntimeStatus.Failed, failed.Status);
        var failure = Assert.IsType<ComposerMigrationException>(failed.Failure);
        Assert.Null(failed.Runtime);
        Assert.False(failed.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.None, failed.Refusal);
        Assert.Equal(conflictingDestination
            ? $"Cannot claim the legacy Composer session for user 1: a different per-user session already exists at '{userPath}'. Neither file was changed; resolve the conflict manually."
            : $"The legacy Composer session '{legacyPath}' is corrupt; it was not migrated.", failure.Message);
        Assert.Equal(legacyBefore, await File.ReadAllBytesAsync(legacyPath, Ct));
        if (conflictingDestination) Assert.Equal(userBefore, await File.ReadAllBytesAsync(userPath, Ct));
        else Assert.False(File.Exists(userPath));
        Assert.False(File.Exists(Path.Combine(StateDir, "composer", "users", "1", "legacy-migration.json")));
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll());
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, first)).Refusal);

        // Resolve the source conflict explicitly; cleanup must not have manufactured this recovery.
        if (conflictingDestination) await File.WriteAllBytesAsync(legacyPath, userBefore!, Ct);
        else await WriteSessionAsync(legacyPath, "repaired-legacy-history-marker");
        var fresh = await Request(manager, first);
        Assert.Equal(ComposerRuntimeStatus.Connected, fresh.Status);
        Assert.Equal([conflictingDestination ? "existing-user-history-marker" : "repaired-legacy-history-marker"],
            fresh.Runtime!.Composer.GetChatHistory().Select(message => message.Content));
        Assert.Equal(2, _clientCreations);
    }

    [Fact]
    public async Task ConnectGate_WhenAssertionBodyThrows_ReleasesAndJoinsTheStartedConnection()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        Task<ComposerRuntimeResult>? creator = null;
        var sentinel = new InvalidOperationException("deliberate assertion-body abort during connection");
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var gate = Park();
            creator = Request(manager, second);
            await GuardAsync(gate.Entered.Task, "cleanup probe entered real manager operation");
            throw sentinel;
        });
        Assert.Same(sentinel, caught);
        Assert.NotNull(creator);
        var result = await Bounded(creator, "connection settles after assertion-body abort");
        Assert.Equal(ComposerRuntimeStatus.Connected, result.Status);
        Assert.True(result.Runtime!.Composer.IsConnected);
        Assert.Same(result.Runtime, (await Request(manager, second)).Runtime);
        Assert.Equal(1, _clientCreations);
    }
}
