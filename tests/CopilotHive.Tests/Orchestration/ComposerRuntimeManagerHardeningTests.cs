using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// CLEANUP ERROR CONTAINMENT (<c>ComposerRuntimeManager.LogCleanupWarning</c>) and MANAGER-OWNED DISPOSAL
/// (<see cref="ComposerUserRuntime"/> is a read-only handle with no disposal entry point).
/// <para>
/// AC3: a THROWING logger must never mask the original connect/migration failure nor skip any remaining cleanup
/// step (composer disposal, actual teardown join, subscriber cleanup, attachment-clearing attempt, unregister, cache
/// eviction). Every vector arms a REAL secondary cleanup fault so the contained log site is genuinely reached, then
/// proves the steps after it still ran; a positive control proves the warning evidence is unchanged when the logger
/// works.
/// </para>
/// <para>
/// AC4: the runtime's public surface is pinned by reflection (no <c>DisposeAsync</c>, no
/// <see cref="IAsyncDisposable"/>), and deterministic manager-only disposal/recreation tests prove there is exactly
/// one disposal path and no overlap.
/// </para>
/// All waits are bounded and fail loudly; milestones are production-caused; no test sleeps.
/// </summary>
public sealed partial class ComposerRuntimeManagerTests
{
    // ── Throwing logger fixture ───────────────────────────────────────────────

    /// <summary>
    /// An <see cref="ILogger{TCategoryName}"/> that records every entry and then THROWS from <c>Log</c>. This is the
    /// production hazard: cleanup logging runs inside catch blocks, so a faulting sink would escape that catch and
    /// skip the remaining cleanup unless production contains it.
    /// </summary>
    private sealed class ThrowingLogger<TCategoryName> : ILogger<TCategoryName>
    {
        private readonly ConcurrentQueue<LifecycleLogEntry> _entries = new();
        private int _logged;

        /// <summary>Entries recorded BEFORE the throw — production genuinely reached the log call.</summary>
        public LifecycleLogEntry[] Entries => _entries.ToArray();

        /// <summary>How many times production attempted to log.</summary>
        public int LoggedCalls => Volatile.Read(ref _logged);

        /// <summary>The fault every log attempt raises.</summary>
        public Exception Fault { get; } = new InvalidOperationException("deliberate throwing-logger fault");

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> fields ? fields.ToArray() : [];
            _entries.Enqueue(new(logLevel, formatter(state, exception), exception, properties));
            Interlocked.Increment(ref _logged);
            throw Fault;
        }
    }

    /// <summary>
    /// Makes the runtime's own <see cref="ComposerAttachmentService"/> fail its clearing step: disposing the private
    /// operation gate makes the next <c>WaitAsync</c> throw <see cref="ObjectDisposedException"/> out of
    /// <c>ClearAllAsync</c>, which is a genuine fault OF that cleanup step (not of its internals).
    /// </summary>
    private static void BreakAttachmentClearing(ComposerAttachmentService attachments) =>
        InstanceField<SemaphoreSlim>(attachments, "_gate").Dispose();

    /// <summary>Faults the Composer's teardown-completion task exactly like a throwing actor disposal would.</summary>
    private static void FaultTeardownCompletion(Composer composer, Exception fault) =>
        Assert.True(Field<TaskCompletionSource>(composer, "_teardownCompletion").TrySetException(fault));

    /// <summary>Arms the agent-disposal hook so <see cref="Composer.DisposeAsync"/> genuinely throws.</summary>
    private static void FaultAgentDisposal(Composer composer, Exception fault) =>
        Field<ComposerAgentService>(composer, "_agentService").OnAgentDisposing = _ => throw fault;

    // ── AC3: a throwing logger cannot mask the ORIGINAL failure ──────────────

    /// <summary>
    /// THE FLAGSHIP AC3 VECTOR: a GENUINE connect failure plus a secondary composer-disposal failure, with a manager
    /// logger that throws from every call. The settled outcome must still carry the ORIGINAL connect exception, and
    /// every remaining cleanup step must still run — the actual teardown join, subscriber cleanup (bus unsubscribe),
    /// attachment clearing (genuine failure ⇒ partial attachments cleared), registry unregister and cache eviction —
    /// so a same-user recreation builds a FRESH runtime.
    /// </summary>
    [Fact]
    public async Task ThrowingLogger_GenuineConnectFailure_KeepsOriginalFailureAuthoritativeAndCompletesEveryCleanupStep()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var sentinel = new InvalidOperationException("deliberate connect fault under a throwing logger");
        var secondaryDisposalFault = new IOException("deliberate secondary composer-disposal fault");

        var teardownJoins = new ConcurrentQueue<Task>();
        manager.OnJoiningTeardown = (userId, task) =>
        {
            Assert.Equal(second.UserId, userId);
            teardownJoins.Enqueue(task);
        };
        var unsubscribes = 0;
        _bus.OnUnsubscribe = () => Interlocked.Increment(ref unsubscribes);

        Composer? failedComposer = null;
        ComposerAttachmentService? failedAttachments = null;
        ComposerAttachment? pending = null;
        string? pendingPath = null;
        _connectHook = async (composer, token) =>
        {
            failedComposer = composer;
            failedAttachments = Field<ComposerAttachmentService>(composer, "_attachmentService");
            using var content = new MemoryStream([3, 1, 4, 1]);
            var saved = await failedAttachments.SaveAsync("contained-pending.png", content, token);
            Assert.True(saved.Success, saved.Error);
            pending = saved.Attachment!;
            pendingPath = Path.Combine(failedAttachments.AttachmentsRootPath, pending.SavedRelativePath);
            Assert.True(File.Exists(pendingPath));
            // Connect for real first, so an agent EXISTS and its disposal hook can genuinely fault:
            // otherwise the armed secondary cleanup failure below would be unreachable and this vector inert.
            await composer.ConnectAsync(token);
            Assert.True(composer.IsConnected);
            // Arm a REAL secondary cleanup failure so the contained log site is actually reached.
            FaultAgentDisposal(composer, secondaryDisposalFault);
            throw sentinel;
        };

        ComposerRuntimeResult result;
        try
        {
            result = await Bounded(Request(manager, second), "throwing-logger fatal failure settles after cleanup");
        }
        finally
        {
            _bus.OnUnsubscribe = null;
            _connectHook = null;
        }

        // ── The ORIGINAL failure is authoritative — never a logger or cleanup fault. ──
        Assert.Equal(ComposerRuntimeStatus.Failed, result.Status);
        Assert.Same(sentinel, result.Failure);
        Assert.Null(result.Runtime);
        Assert.False(result.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.None, result.Refusal);

        // ── The contained log site was genuinely reached, then swallowed. ──
        Assert.True(logger.LoggedCalls > 0,
            "The throwing logger was never reached — the containment vector is inert and proves nothing.");
        var attempted = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal($"Disposing the partial Composer of user {second.UserId} failed", attempted.Message);
        // A single agent-disposal fault surfaces unwrapped (an aggregate only appears when a client ALSO fails).
        Assert.Same(secondaryDisposalFault, attempted.Exception);

        // ── Every step AFTER the contained log ran anyway. ──
        Assert.NotNull(failedComposer);
        Assert.True(ActorCompleted(failedComposer));                 // real composer/actor disposal happened
        Assert.True(failedComposer.TeardownCompletion.IsCompleted);  // the ACTUAL teardown completion was reached
        var joined = Assert.Single(teardownJoins);
        Assert.Same(failedComposer.TeardownCompletion, joined);      // the manager joined the exact task
        Assert.Equal(1, Volatile.Read(ref unsubscribes));            // subscriber detached from the bus
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.False(File.Exists(pendingPath));                      // genuine failure ⇒ partial attachments cleared
        Assert.False(failedAttachments!.MarkAsSent(pending!).Success);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal); // cache evicted

        // ── A recreation builds a FRESH runtime: the entry was evicted, not resurrected. ──
        _clientFactory = _ => new Mock<IChatClient>().Object;
        var recreated = await Request(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, recreated.Status);
        Assert.NotSame(failedComposer, recreated.Runtime!.Composer);
        Assert.NotSame(failedAttachments, recreated.Runtime.Attachments);
    }

    /// <summary>
    /// MIGRATION failure under a throwing logger (the <c>FailAsync</c> path with NO composer yet): the original
    /// <see cref="ComposerMigrationException"/> stays authoritative, the legacy bytes are untouched, the subscriber is
    /// detached, and the entry is evicted so a repaired source still builds a fresh runtime.
    /// </summary>
    [Fact]
    public async Task ThrowingLogger_MigrationFailure_KeepsTheOriginalMigrationExceptionAndEvictsTheEntry()
    {
        var (first, _) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var legacyPath = Path.Combine(StateDir, "composer-session.json");
        await File.WriteAllTextAsync(legacyPath, "{corrupt-legacy-under-throwing-logger", Ct);
        var before = await File.ReadAllBytesAsync(legacyPath, Ct);

        var failed = await Bounded(Request(manager, first), "throwing-logger migration failure settles");

        Assert.Equal(ComposerRuntimeStatus.Failed, failed.Status);
        var migration = Assert.IsType<ComposerMigrationException>(failed.Failure);
        Assert.Equal($"The legacy Composer session '{legacyPath}' is corrupt; it was not migrated.", migration.Message);
        Assert.Null(failed.Runtime);
        Assert.Equal(before, await File.ReadAllBytesAsync(legacyPath, Ct));
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, first)).Refusal);

        // Recovery: a repaired source still builds a fresh runtime (no poisoned entry survived).
        await WriteSessionAsync(legacyPath, "repaired-under-throwing-logger-marker");
        var recovered = await Request(manager, first);
        Assert.Equal(ComposerRuntimeStatus.Connected, recovered.Status);
        Assert.Equal(["repaired-under-throwing-logger-marker"],
            recovered.Runtime!.Composer.GetChatHistory().Select(m => m.Content));
    }

    /// <summary>
    /// The LIFECYCLE path: a throwing cancellation callback is contained and warned about, and — with the logger ALSO
    /// throwing — removal/shutdown must still retain ownership, release the runtime, join the actual teardown,
    /// unregister the registry row and evict the entry.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingLogger_AndThrowingCancellationCallback_StillCompletesTeardownAndEvictsTheEntry(bool shutdown)
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var callbackFault = new InvalidOperationException("deliberate cancellation-callback fault under a throwing logger");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownJoins = 0;
        manager.OnJoiningTeardown = (_, _) => Interlocked.Increment(ref teardownJoins);
        Composer? composer = null;
        _connectHook = async (value, token) =>
        {
            composer = value;
            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                throw callbackFault;
            });
            entered.TrySetResult();
            await GuardAsync(release.Task, "release lifecycle-cancelled connect under a throwing logger");
            token.ThrowIfCancellationRequested();
            await value.ConnectAsync(token);
        };

        var creator = Request(manager, second);
        Task? lifecycle = null;
        try
        {
            await GuardAsync(entered.Task, "shared connect in flight before the lifecycle cancellation");
            lifecycle = shutdown ? manager.ShutdownAsync() : Remove(manager, second.UserId);
            await GuardAsync(callbackEntered.Task, "lifecycle dispatched the deliberately throwing callback");
            release.TrySetResult();
            await GuardAsync(lifecycle, "lifecycle completes teardown under a throwing logger");

            var creatorResult = await Bounded(creator, "lifecycle-cancelled creator result");
            Assert.Equal(ComposerRuntimeStatus.Removed, creatorResult.Status);
            Assert.Null(creatorResult.Runtime);
            Assert.Null(creatorResult.Failure); // the callback fault never became the outcome

            Assert.NotNull(composer);
            Assert.True(ActorCompleted(composer));
            Assert.True(composer.TeardownCompletion.IsCompleted);
            Assert.True(Volatile.Read(ref teardownJoins) > 0);
            Assert.Equal(0, _bus.SubscriberCount);
            Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));

            // The contained callback warning was reached and swallowed.
            Assert.True(logger.LoggedCalls > 0);
            var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Equal($"A cancellation callback of the shared operation of user {second.UserId} threw; teardown continues",
                warning.Message);

            var after = await Bounded(Retry(manager, second), "admission after throwing-logger teardown");
            Assert.Equal(ComposerRuntimeStatus.Refused, after.Status);
            Assert.Equal(shutdown ? ComposerRuntimeRefusal.ShuttingDown : ComposerRuntimeRefusal.NoRuntime, after.Refusal);
            Assert.Null(after.Runtime);
        }
        finally
        {
            release.TrySetResult();
            if (lifecycle is not null) await GuardAsync(lifecycle, "throwing-logger lifecycle cleanup");
            await JoinRequestAsync(creator);
            await GuardAsync(manager.ShutdownAsync(), "throwing-logger manager cleanup");
        }
    }

    /// <summary>
    /// The SUBSCRIBER-cleanup catch site: the bus unsubscribe itself throws (a genuine subscriber-disposal failure)
    /// while the manager logger also throws. Attachments still get cleared, the registry row is still dropped and the
    /// cache entry is still evicted — the ORIGINAL connect fault stays authoritative.
    /// </summary>
    [Fact]
    public async Task ThrowingLogger_AndFailingSubscriberCleanup_StillClearsAttachmentsUnregistersAndEvicts()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var sentinel = new InvalidOperationException("deliberate connect fault with a failing subscriber cleanup");
        var unsubscribeFault = new InvalidOperationException("deliberate bus-unsubscribe fault");
        var unsubscribes = 0;
        _bus.OnUnsubscribe = () =>
        {
            Interlocked.Increment(ref unsubscribes);
            throw unsubscribeFault;
        };

        Composer? failedComposer = null;
        ComposerAttachmentService? failedAttachments = null;
        string? pendingPath = null;
        _connectHook = async (composer, token) =>
        {
            failedComposer = composer;
            failedAttachments = Field<ComposerAttachmentService>(composer, "_attachmentService");
            using var content = new MemoryStream([5, 5, 5, 5]);
            var saved = await failedAttachments.SaveAsync("subscriber-fault-pending.png", content, token);
            Assert.True(saved.Success, saved.Error);
            pendingPath = Path.Combine(failedAttachments.AttachmentsRootPath, saved.Attachment!.SavedRelativePath);
            throw sentinel;
        };

        ComposerRuntimeResult result;
        try
        {
            result = await Bounded(Request(manager, second), "subscriber-cleanup failure settles");
        }
        finally
        {
            _bus.OnUnsubscribe = null;
            _connectHook = null;
        }

        Assert.Equal(ComposerRuntimeStatus.Failed, result.Status);
        Assert.Same(sentinel, result.Failure);
        Assert.True(Volatile.Read(ref unsubscribes) > 0, "The subscriber-cleanup step never ran.");
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal($"Disposing the partial subscriber of user {second.UserId} failed", warning.Message);
        Assert.Same(unsubscribeFault, warning.Exception);

        // Steps AFTER the contained subscriber log still ran.
        Assert.NotNull(failedComposer);
        Assert.True(ActorCompleted(failedComposer));
        Assert.False(File.Exists(pendingPath));                       // attachment clearing still ran
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal);
    }

    /// <summary>
    /// The ATTACHMENT-CLEARING catch site: <c>ClearAllAsync</c> itself throws while the logger also throws. Unregister
    /// and cache eviction still run, the runtime was still disposed and joined, and the ORIGINAL fault stays
    /// authoritative — the clearing attempt failing never rewrites the settled outcome.
    /// </summary>
    [Fact]
    public async Task ThrowingLogger_AndFailingAttachmentClear_StillDisposesJoinsUnregistersAndEvicts()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var sentinel = new InvalidOperationException("deliberate connect fault with a failing attachment clear");
        var teardownJoins = 0;
        manager.OnJoiningTeardown = (_, _) => Interlocked.Increment(ref teardownJoins);
        Composer? failedComposer = null;
        var cleared = 0;

        _connectHook = async (composer, token) =>
        {
            failedComposer = composer;
            var attachments = Field<ComposerAttachmentService>(composer, "_attachmentService");
            using var content = new MemoryStream([6, 6, 6, 6]);
            var saved = await attachments.SaveAsync("failing-clear.png", content, token);
            Assert.True(saved.Success, saved.Error);
            // Break the clearing step itself, so ClearAllAsync genuinely throws during cleanup.
            BreakAttachmentClearing(attachments);
            Interlocked.Exchange(ref cleared, 1);
            throw sentinel;
        };

        var failed = await Bounded(Request(manager, second), "failing-attachment-clear failure settles");

        Assert.Equal(1, Volatile.Read(ref cleared)); // the vector was armed
        Assert.Equal(ComposerRuntimeStatus.Failed, failed.Status);
        Assert.Same(sentinel, failed.Failure);
        Assert.Null(failed.Runtime);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal($"Clearing the partial attachments of user {second.UserId} failed", warning.Message);
        Assert.IsType<ObjectDisposedException>(warning.Exception);

        // Steps AFTER the contained clearing fault still ran.
        Assert.NotNull(failedComposer);
        Assert.True(ActorCompleted(failedComposer));
        Assert.True(Volatile.Read(ref teardownJoins) > 0);                              // teardown join (pre-clear) ran
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal); // evicted
    }

    /// <summary>
    /// The TEARDOWN-JOIN catch site: the Composer's <see cref="Composer.TeardownCompletion"/> faults (exactly what a
    /// throwing actor disposal produces) while the logger also throws. The runtime is still released, the subscriber
    /// detached, the registry row dropped and the entry evicted — the fault is logged, never thrown.
    /// </summary>
    [Fact]
    public async Task ThrowingLogger_AndFaultedTeardownCompletion_StillUnregistersAndEvicts()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var teardownFault = new InvalidOperationException("deliberate faulted teardown completion");
        var teardownJoins = new ConcurrentQueue<Task>();
        manager.OnJoiningTeardown = (_, task) => teardownJoins.Enqueue(task);
        Composer? runtimeComposer = null;

        var connected = await Request(manager, second);
        runtimeComposer = connected.Runtime!.Composer;
        FaultTeardownCompletion(runtimeComposer, teardownFault);

        Assert.True(await Bounded(Remove(manager, second.UserId), "throwing-logger removal with faulted teardown"));

        Assert.True(ActorCompleted(runtimeComposer)); // disposal itself still ran
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal($"Teardown of the Composer of user {second.UserId} faulted", warning.Message);
        Assert.Contains(teardownJoins, task => ReferenceEquals(task, runtimeComposer.TeardownCompletion));
        Assert.False(runtimeComposer.TeardownCompletion.IsCompletedSuccessfully); // the vector really faulted it

        // Steps AFTER the contained join warning still ran.
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal); // evicted
    }

    /// <summary>
    /// POSITIVE CONTROL: with a WORKING logger the contained warnings still appear with their exact original text,
    /// exception identity and payload — containment changed robustness only, never the evidence.
    /// </summary>
    [Fact]
    public async Task WorkingLogger_StillEmitsTheOriginalCleanupWarningTextAndPayload()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new LifecycleWarningLogger();
        var manager = NewManager(sp, logger);
        var callbackFault = new InvalidOperationException("deliberate callback fault for the positive control");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownJoins = 0;
        manager.OnJoiningTeardown = (_, _) => Interlocked.Increment(ref teardownJoins);
        Composer? composer = null;
        _connectHook = async (value, token) =>
        {
            composer = value;
            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                throw callbackFault;
            });
            entered.TrySetResult();
            await GuardAsync(release.Task, "release positive-control connect");
            token.ThrowIfCancellationRequested();
            await value.ConnectAsync(token);
        };

        var creator = Request(manager, second);
        Task? removal = null;
        try
        {
            await GuardAsync(entered.Task, "positive-control connect in flight");
            removal = Remove(manager, second.UserId);
            await GuardAsync(callbackEntered.Task, "positive control dispatched the throwing callback");
            release.TrySetResult();
            await GuardAsync(removal, "positive-control removal completes");
            await JoinRequestAsync(creator);
        }
        finally
        {
            release.TrySetResult();
            if (removal is not null) await GuardAsync(removal, "positive-control cleanup");
            await GuardAsync(manager.ShutdownAsync(), "positive-control manager cleanup");
        }

        // The warning evidence is byte-for-byte what it was before containment.
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal($"A cancellation callback of the shared operation of user {second.UserId} threw; teardown continues",
            warning.Message);
        var aggregate = Assert.IsType<AggregateException>(warning.Exception);
        Assert.Same(callbackFault, Assert.Single(aggregate.InnerExceptions));
        var userId = Assert.Single(warning.Properties, property => property.Key == "UserId");
        Assert.Equal(second.UserId, Assert.IsType<int>(userId.Value));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.True(Volatile.Read(ref teardownJoins) > 0);
        Assert.NotNull(composer);
        Assert.True(ActorCompleted(composer));
        Assert.Equal(0, _bus.SubscriberCount);
    }

    // ── AC4: manager-owned disposal ──────────────────────────────────────────

    /// <summary>
    /// PUBLIC-SURFACE REGRESSION: <see cref="ComposerUserRuntime"/> is a read-only handle — it implements neither
    /// <see cref="IAsyncDisposable"/> nor <see cref="IDisposable"/> and exposes no disposal-shaped member, so a handle
    /// holder cannot race manager removal. The manager keeps exactly one (private) disposal sequence.
    /// </summary>
    [Fact]
    public void ComposerUserRuntime_ExposesNoPublicDisposalEntryPoint()
    {
        var type = typeof(ComposerUserRuntime);
        const System.Reflection.BindingFlags Public = System.Reflection.BindingFlags.Public;
        const System.Reflection.BindingFlags Instance = System.Reflection.BindingFlags.Instance;

        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(type),
            "ComposerUserRuntime must not implement IAsyncDisposable: disposal is manager-owned.");
        Assert.False(typeof(IDisposable).IsAssignableFrom(type),
            "ComposerUserRuntime must not implement IDisposable: disposal is manager-owned.");

        // No public member may be disposal-shaped, and no public method may even return an awaitable: the handle
        // carries data only, so there is no second path to drive teardown through.
        foreach (var member in type.GetMethods(Public | Instance))
        {
            if (member.IsSpecialName) continue; // property getters / event accessors
            Assert.DoesNotContain(member.Name,
                (string[])["DisposeAsync", "Dispose", "Close", "CloseAsync", "Shutdown", "ShutdownAsync", "StopAsync"]);
        }
        // Properties/events must not be disposal-shaped either.
        foreach (var property in type.GetProperties(Public | Instance))
            Assert.DoesNotContain(property.Name, (string[])["DisposeAsync", "Dispose", "Close", "CloseAsync"]);
        foreach (var method in type.GetMethods(Public | Instance))
        {
            if (method.IsSpecialName) continue;
            var returnsAwaitable = method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask)
                || method.ReturnType == typeof(Task<>)
                || (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>));
            Assert.False(returnsAwaitable,
                $"ComposerUserRuntime must expose no awaitable member (a disposal path in disguise): '{method.Name}'.");
        }

        // The read-only surface itself is unchanged — removal must not have shrunk the handle.
        foreach (var name in new[] { "State", "Composer", "Attachments", "Subscriber" })
            Assert.NotNull(type.GetProperty(name, Public | Instance));

        // The manager owns exactly ONE disposal sequence, and it is private.
        var disposeRuntimeAsync = typeof(ComposerRuntimeManager).GetMethod(
            "DisposeRuntimeAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(disposeRuntimeAsync);
        Assert.True(disposeRuntimeAsync.IsPrivate);
        Assert.Equal(typeof(Task), disposeRuntimeAsync.ReturnType);

        // Source pin: the handle really did lose its public disposal implementation.
        var managerSource = File.ReadAllText(Path.Combine(
            TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration", "ComposerRuntimeManager.cs"))
            .ReplaceLineEndings("\n");
        Assert.DoesNotContain("public sealed class ComposerUserRuntime : IAsyncDisposable", managerSource);
        Assert.DoesNotContain("public async ValueTask DisposeAsync()\n    {\n        try { await Composer.DisposeAsync(); }",
            managerSource);
        Assert.Contains("public sealed class ComposerUserRuntime\n", managerSource);
    }

    /// <summary>
    /// Deterministic manager-only disposal: each runtime is disposed EXACTLY once — by the manager only — and the
    /// handle never performs disposal, so no second path exists. Recreation after removal builds a fresh runtime that
    /// is likewise disposed once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagerOwnedDisposal_DisposesEachRuntimeExactlyOnce_AndCreatesNoSecondPath(bool shutdown)
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var teardownJoins = 0;
        manager.OnJoiningTeardown = (_, _) => Interlocked.Increment(ref teardownJoins);
        var client = new CountingClient();
        _clientFactory = _ => client;

        var first = (await Request(manager, second)).Runtime!;
        var firstComposer = first.Composer;

        Task lifecycle = shutdown ? manager.ShutdownAsync() : Remove(manager, second.UserId);
        await GuardAsync(lifecycle, "manager-only disposal completes");

        Assert.True(ActorCompleted(firstComposer));
        Assert.True(firstComposer.TeardownCompletion.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref teardownJoins)); // ONE join of ONE teardown: no second disposal path
        Assert.Equal(1, client.DisposeCount);              // the agent's chat client was disposed exactly once
        Assert.Equal(0, _bus.SubscriberCount);

        if (shutdown)
        {
            Assert.Equal(ComposerRuntimeRefusal.ShuttingDown, (await Request(manager, second)).Refusal);
            Assert.Equal(1, client.DisposeCount);
            return;
        }

        // Recreation AFTER the actual teardown: a brand-new runtime, disposed once more by the manager.
        var recreatedClient = new CountingClient();
        _clientFactory = _ => recreatedClient;
        var recreated = (await Request(manager, second)).Runtime!;
        Assert.NotSame(first, recreated);
        Assert.NotSame(firstComposer, recreated.Composer);
        Assert.True(await Remove(manager, second.UserId));
        Assert.True(ActorCompleted(recreated.Composer));
        Assert.Equal(2, Volatile.Read(ref teardownJoins)); // one per runtime — still no duplicate
        Assert.Equal(1, recreatedClient.DisposeCount);
        Assert.Equal(0, _bus.SubscriberCount);
    }

    /// <summary>
    /// No same-user recreation may start before the previous runtime's ACTUAL teardown completed: the recreation waits
    /// on the entry teardown (Removing contract unchanged) while the manager alone drives the disposal.
    /// </summary>
    [Fact]
    public async Task ManagerOwnedDisposal_RecreationWaitsForTheActualTeardown_AndNoDoubleDisposalOccurs()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        using var stuck = new StuckStream();
        var clock = new ControlledTimeProvider();
        var recorder = new JoinRecorder();
        manager.OnJoiningTeardown = recorder.Record;
        Composer? old = null;
        _connectHook = async (composer, ct) =>
        {
            if (old is null) { old = composer; InstallActorClock(composer, clock); }
            await composer.ConnectAsync(ct);
        };
        _clientFactory = _ => Volatile.Read(ref _clientCreations) == 1 ? stuck.Client : new Mock<IChatClient>().Object;

        var runtime = (await Request(manager, second)).Runtime!;
        Assert.Same(old, runtime.Composer);
        await runtime.Composer.SendMessageWithEventsAsync("force real deferred actor teardown");
        await GuardAsync(stuck.Started.Task, "stuck stream started");

        var removal = Remove(manager, second.UserId);
        await DriveActorDisposeTimeoutAsync(clock);
        await GuardAsync(recorder.When(task => ReferenceEquals(task, runtime.Composer.TeardownCompletion)),
            "manager joined the exact Composer.TeardownCompletion during removal");
        // Load-bearing pre-check: the actor is genuinely still running, so a recreation cannot have started.
        Assert.False(ActorCompleted(runtime.Composer));
        Assert.False(removal.IsCompleted);

        // A recreation attempt during the old teardown must WAIT (Removing contract) — never overlap a second runtime.
        var waiting = Request(manager, second);
        Assert.False(waiting.IsCompleted);

        stuck.Release();
        await GuardAsync(removal, "removal completes after the real teardown");
        await GuardAsync(waiting, "recreation completes only after the previous actual teardown");
        var recreated = await waiting;
        Assert.True(recreated.IsGranted);
        Assert.NotSame(runtime, recreated.Runtime);
        Assert.NotSame(runtime.Composer, recreated.Runtime!.Composer);
        Assert.True(ActorCompleted(runtime.Composer)); // the old runtime was genuinely torn down first
        Assert.Equal(1, _bus.SubscriberCount);         // exactly the new runtime's subscriber remains
        Assert.True(await Remove(manager, second.UserId));
        Assert.True(ActorCompleted(recreated.Runtime.Composer));
        Assert.Equal(0, _bus.SubscriberCount);
    }

    // ── AC3: the contained sites the phase-2 suite left UNPROBED ──────────────

    /// <summary>
    /// THE <c>DisposeRuntimeAsync</c> SITES (removal/shutdown path), as opposed to the <c>FailAsync</c> sites the
    /// phase-2 suite already covered: the runtime's COMPOSER disposal and its SUBSCRIBER disposal each get their OWN
    /// vector with a REAL secondary fault, while the manager logger also throws.
    /// </summary>
    /// <remarks>
    /// Every row proves the SAME three things at ITS OWN site: (1) the LIFECYCLE's own outcome stays authoritative
    /// and the logger/cleanup fault never becomes it; (2) the step AFTER the contained log site still ran; and
    /// (3) the entry was still evicted and the registry row still dropped, so no partially-cleaned runtime
    /// survived. The row also asserts the warning was genuinely attempted with the exact text of ITS OWN site, so
    /// a vector that never reached the site cannot pass silently.
    /// <para>
    /// The THIRD <c>DisposeRuntimeAsync</c> site (attachment clearing) is deliberately NOT covered here: its
    /// <c>clearAttachments</c> branch is UNREACHABLE — the only call site passes <c>false</c> — so no runtime
    /// behaviour can reach it. Its dead-branch nature is pinned by
    /// <see cref="DisposeRuntimeAsync_AttachmentClearingBranch_IsUnreachableAndThereforeUntestable"/> instead.
    /// </para>
    /// </remarks>
    /// <param name="site">Which contained cleanup site this row arms.</param>
    [Theory]
    [InlineData(CleanupSite.ComposerDisposal)]
    [InlineData(CleanupSite.SubscriberDisposal)]
    public async Task ThrowingLogger_AtEachDisposeRuntimeCleanupSite_KeepsTheLifecycleOutcomeAndStillEvicts(CleanupSite site)
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var teardownJoins = new ConcurrentQueue<Task>();
        var steps = new List<string>();
        var stepLock = new object();
        manager.OnJoiningTeardown = (_, task) =>
        {
            lock (stepLock) steps.Add("teardown-join");
            teardownJoins.Enqueue(task);
        };

        // The runtime is CONNECTED first, so it owns a real subscriber, a real attachment service and a real
        // chat client — every disposal step below is genuinely reachable (a never-connected runtime has none).
        var runtime = (await Request(manager, second)).Runtime!;
        var composer = runtime.Composer;
        var attachments = runtime.Attachments;

        // An attachment the user actually uploaded: the clearing policy is observable on real bytes.
        using var content = new MemoryStream([7, 7, 7, 7]);
        var saved = await attachments.SaveAsync("dispose-site-pending.png", content, Ct);
        Assert.True(saved.Success, saved.Error);
        var pendingPath = Path.Combine(attachments.AttachmentsRootPath, saved.Attachment!.SavedRelativePath);
        Assert.True(File.Exists(pendingPath));

        var unsubscribes = 0;

        // Arm a REAL secondary fault at THIS row's site, so the contained log site is genuinely reached.
        Exception armed;
        switch (site)
        {
            case CleanupSite.ComposerDisposal:
                // The agent-disposal hook throws INSIDE Composer.DisposeAsync ⇒ the composer's disposal fails.
                armed = new IOException("deliberate composer-disposal fault");
                Field<ComposerAgentService>(composer, "_agentService").OnAgentDisposing = _ =>
                {
                    lock (stepLock) steps.Add("composer-disposal");
                    throw armed;
                };
                _bus.OnUnsubscribe = () =>
                {
                    lock (stepLock) steps.Add("subscriber");
                    Interlocked.Increment(ref unsubscribes);
                };
                break;
            case CleanupSite.SubscriberDisposal:
                // The bus unsubscribe throws ⇒ the subscriber's disposal fails.
                armed = new InvalidOperationException("deliberate subscriber-disposal fault");
                // The composer disposal is RECORDED (but does not fail) so the order is fully observable.
                Field<ComposerAgentService>(composer, "_agentService").OnAgentDisposing = _ =>
                {
                    lock (stepLock) steps.Add("composer-disposal");
                };
                _bus.OnUnsubscribe = () =>
                {
                    lock (stepLock) steps.Add("subscriber");
                    Interlocked.Increment(ref unsubscribes);
                    throw armed;
                };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(site));
        }

        try
        {
            Assert.True(await Bounded(Remove(manager, second.UserId), "throwing-logger removal with a real cleanup fault"));
        }
        finally
        {
            _bus.OnUnsubscribe = null;
        }

        // ── The contained site was genuinely reached, with ITS OWN text, and swallowed. ──
        Assert.True(logger.LoggedCalls > 0,
            "The throwing logger was never reached — the containment vector is inert and proves nothing.");
        var expectedMessage = site switch
        {
            CleanupSite.ComposerDisposal => $"Disposing the Composer of user {second.UserId} failed",
            CleanupSite.SubscriberDisposal => $"Disposing the subscriber of user {second.UserId} failed",
            _ => throw new ArgumentOutOfRangeException(nameof(site)),
        };
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(expectedMessage, warning.Message);
        Assert.Same(armed, warning.Exception);

        // ── The steps AFTER the contained site still ran. ──
        Assert.True(ActorCompleted(composer));                        // the actor really was torn down
        Assert.True(composer.TeardownCompletion.IsCompleted);         // the ACTUAL teardown completion was reached
        Assert.Contains(teardownJoins, task => ReferenceEquals(task, composer.TeardownCompletion));
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId)); // row dropped
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal);            // entry evicted

        // ── The ORDER of the removal-path cleanup steps, as production performed them: the composer disposal
        // FIRST, then the subscriber cleanup, and only THEN the ACTUAL teardown join. (The removal path —
        // DisposeRuntimeAsync — deliberately disposes the subscriber before joining the teardown, unlike the
        // failure path.) A reordering (or a step that skipped ahead) is observable here, not hidden behind an
        // end state that merely looks right. ──
        lock (stepLock)
        {
            Assert.Equal(["composer-disposal", "subscriber", "teardown-join"], steps);
        }

        if (site == CleanupSite.SubscriberDisposal)
        {
            // The subscriber disposal itself failed, so the bus unsubscribe WAS attempted (and failed).
            Assert.True(Volatile.Read(ref unsubscribes) > 0, "The subscriber-cleanup step never ran.");
        }
        else
        {
            // The subscriber WAS detached on the path where its own disposal did not fail.
            Assert.Equal(0, _bus.SubscriberCount);
            Assert.True(Volatile.Read(ref unsubscribes) > 0);
        }

        // The LIFECYCLE never clears attachments (clearAttachments is false on this path), so the user's bytes
        // survive: the attachment policy is unchanged by the containment.
        Assert.True(File.Exists(pendingPath));
    }

    /// <summary>
    /// The third <c>DisposeRuntimeAsync</c> catch site — attachment clearing — sits behind a
    /// <c>clearAttachments</c> flag whose ONLY call site passes <c>false</c>. The branch is therefore DEAD CODE: no
    /// runtime behaviour can reach it, so no behaviour test could ever cover it.
    /// </summary>
    /// <remarks>
    /// This pins that fact explicitly instead of leaving a silently-uncovered catch site. It is a STRUCTURAL
    /// assertion about the CURRENT source, not a behavioural one; if a future change ever passes
    /// <c>clearAttachments: true</c>, the pin fails and the site must then be given a real vector.
    /// </remarks>
    [Fact]
    public void DisposeRuntimeAsync_AttachmentClearingBranch_IsUnreachableAndThereforeUntestable()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration", "ComposerRuntimeManager.cs"))
            .ReplaceLineEndings("\n");

        // The ONLY call site passes clearAttachments: false ⇒ the `if (clearAttachments)` branch is dead.
        Assert.Equal(1, CountOccurrences(source, "DisposeRuntimeAsync(runtime, clearAttachments: false)"));
        Assert.DoesNotContain("clearAttachments: true", source);
        Assert.Contains("if (clearAttachments)", source);
        // The site is nonetheless CONTAINED (it goes through the safe-log helper like every other one).
        Assert.Contains("LogCleanupWarning(ex, \"Clearing the attachments of user {UserId} failed\", userId)", source);
    }

    /// <summary>
    /// The <c>TearDownAsync</c> OUTER catch site: when the entry teardown itself throws, the warning is contained
    /// and the entry is STILL evicted (the <c>finally</c> block that drops it must run).
    /// </summary>
    /// <remarks>
    /// The teardown's own body cannot throw in normal production (every step is contained), so this vector drives
    /// the failure through the ONE seam the teardown awaits: the shared operation. Its settlement is awaited
    /// directly, so a fault there escapes the teardown body into its outer catch.
    /// <para>
    /// OWNERSHIP: injecting that fault deliberately hands the manager an invalid state — replacing
    /// <c>Entry.Operation</c> destroys the manager's ONLY runtime ownership record, so the teardown throws BEFORE
    /// <see cref="ComposerRuntimeManager"/>'s disposal sequence and its <c>finally</c> then evicts the entry. The
    /// saved runtime is therefore orphaned: never disposed, never joined, its subscriber still attached and its
    /// registry row still published. Nothing else owns it (DI does not own these manually constructed pieces, and
    /// fixture teardown only observes requests and shuts down CACHED entries), so this test must own the cleanup it
    /// caused: a failure-safe <c>finally</c> disposes the Composer, boundedly joins its EXACT
    /// <see cref="Composer.TeardownCompletion"/>, disposes the subscriber and unregisters the registry row — every
    /// step attempted even when an earlier one faults, with the primary failure rethrown only afterwards.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ThrowingLogger_WhenTheEntryTeardownItselfFaults_StillEvictsTheEntryAndContainsTheWarning()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var teardownFault = new InvalidOperationException("deliberate entry-teardown fault");
        var runtime = (await Request(manager, second)).Runtime!;
        var registry = sp.GetRequiredService<LlmSessionRegistry>();
        var registryId = ComposerUserState.RegistryId(runtime.State);
        Assert.Contains(registryId, registry.GetAll().Select(row => row.SessionId));

        // Fault the shared operation the teardown awaits: TearDownAsync's `await entry.Operation` throws, which
        // is exactly what its OUTER catch is for.
        FaultEntryOperation(manager, second.UserId, teardownFault);

        ExceptionDispatchInfo? primary = null;
        Exception? cleanupFailure = null;
        var cleanupOrder = new List<string>();
        try
        {
            Assert.True(await Bounded(Remove(manager, second.UserId), "removal whose teardown faults"));

            // The outer catch was reached and contained.
            Assert.True(logger.LoggedCalls > 0,
                "The throwing logger was never reached — the containment vector is inert and proves nothing.");
            var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Equal($"Tearing down the runtime of user {second.UserId} failed", warning.Message);
            Assert.Same(teardownFault, warning.Exception);

            // The teardown's finally block still ran: the entry left the cache.
            Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal);

            // ... and the injected fault really did orphan the runtime (this is the leak being owned below):
            // its registry row survived the removal because the disposal sequence never ran, and its actor is
            // still live. These are the preconditions that make the cleanup below necessary, asserted HERE so a
            // vanished cleanup cannot silently satisfy them.
            Assert.Contains(registryId, registry.GetAll().Select(row => row.SessionId));
            Assert.False(ActorCompleted(runtime.Composer));
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            try
            {
                // Retain the cleanup's own failures, then let the verification steps below report what they observe:
                // a removed cleanup must FAIL this test (load-bearing), not vanish behind an aggregate.
                try
                {
                    await CompleteCleanupAsync(
                        OrphanOwnershipCleanupSteps(runtime, registry, registryId, cleanupOrder));
                }
                catch (Exception ex) { cleanupFailure = ex; }
                // Independent post-conditions: observable whether or not the cleanup above ran.
                Assert.True(ActorCompleted(runtime.Composer),
                    "The orphaned runtime's actor was never torn down — its ownership cleanup did not run.");
                Assert.True(runtime.Composer.TeardownCompletion.IsCompleted,
                    "The orphaned runtime's ACTUAL teardown was never joined — its ownership cleanup did not run.");
                Assert.Equal(0, _bus.SubscriberCount);
                Assert.DoesNotContain(registryId, registry.GetAll().Select(row => row.SessionId));
                // The ORDER of the cleanup steps is load-bearing: the registry row must be unregistered AFTER the
                // actual teardown join, because a still-running actor re-publishes its row from its shutdown path.
                // Unregistering earlier leaves a stale row behind — see
                // OrphanOwnershipCleanup_UnregistersTheRegistryRowOnlyAfterTheActualTeardown.
                Assert.Equal(["composer-disposal", "teardown-join", "subscriber", "unregister"], cleanupOrder);
            }
            catch (Exception ex) { cleanupFailure ??= ex; }
        }
        if (primary is not null)
        {
            if (cleanupFailure is not null) primary.SourceException.Data["CleanupFailure"] = cleanupFailure;
            primary.Throw();
        }
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    /// <summary>
    /// The failure-safe cleanup of a runtime this test deliberately orphaned (it replaced the manager's only
    /// ownership record): dispose the Composer, boundedly join its EXACT <see cref="Composer.TeardownCompletion"/>,
    /// dispose the subscriber and unregister the registry row. Every step is attempted in production's own order —
    /// <see cref="CompleteCleanupAsync"/> runs them all and aggregates failures, so one fault never skips the rest.
    /// </summary>
    /// <remarks>
    /// The ORDER is load-bearing and is pinned by <see cref="OrphanOwnershipCleanup_UnregistersTheRegistryRowOnlyAfterTheActualTeardown"/>.
    /// Unregistering EARLY is a real leak: a still-running actor re-publishes its <c>composer:N</c> row from its
    /// shutdown path, so an unregister issued before the teardown join is undone by the very actor the join waits
    /// for.
    /// </remarks>
    private IEnumerable<Func<Task>> OrphanOwnershipCleanupSteps(
        ComposerUserRuntime runtime, LlmSessionRegistry registry, string registryId) =>
        OrphanOwnershipCleanupSteps(runtime, registry, registryId, null);

    /// <summary>
    /// The failure-safe cleanup of a runtime this test deliberately orphaned (it replaced the manager's only
    /// ownership record): dispose the Composer, boundedly join its EXACT <see cref="Composer.TeardownCompletion"/>,
    /// dispose the subscriber and unregister the registry row. Every step is attempted in production's own order —
    /// <see cref="CompleteCleanupAsync"/> runs them all and aggregates failures, so one fault never skips the rest.
    /// </summary>
    /// <param name="runtime">The orphaned runtime.</param>
    /// <param name="registry">The session registry that still holds the runtime's row.</param>
    /// <param name="registryId">The runtime's registry id.</param>
    /// <param name="order">
    /// When supplied, the executed step names are recorded here so the ORDER of the cleanup is OBSERVABLE and can
    /// be asserted — a reordering that would reintroduce the registry-row leak must not pass silently.
    /// </param>
    private IEnumerable<Func<Task>> OrphanOwnershipCleanupSteps(
        ComposerUserRuntime runtime, LlmSessionRegistry registry, string registryId, List<string>? order)
    {
        yield return () => { order?.Add("composer-disposal"); return runtime.Composer.DisposeAsync().AsTask(); };
        yield return () =>
        {
            order?.Add("teardown-join");
            return GuardAsync(runtime.Composer.TeardownCompletion, "orphaned runtime's ACTUAL teardown completion");
        };
        yield return () => { order?.Add("subscriber"); runtime.Subscriber?.Dispose(); return Task.CompletedTask; };
        yield return () => { order?.Add("unregister"); registry.Unregister(registryId); return Task.CompletedTask; };
    }

    /// <summary>
    /// ORDERING PIN for the orphan cleanup: the registry row must be unregistered AFTER the actual teardown join,
    /// never before. Unregistering early is a genuine leak — the still-running actor re-publishes its
    /// <c>composer:N</c> row from its shutdown path, so the unregister is undone by the very actor the join waits
    /// for, and a stale registry row survives the cleanup.
    /// </summary>
    /// <remarks>
    /// This vector reproduces the hazard on the REAL production path (a runtime whose actor is genuinely torn down
    /// by the cleanup) and proves that unregistering before the teardown leaves the row behind, while unregistering
    /// after it removes the row. Without this, the ordering of the cleanup's steps is unpinned and a reordering
    /// would pass the suite silently.
    /// </remarks>
    [Fact]
    public async Task OrphanOwnershipCleanup_UnregistersTheRegistryRowOnlyAfterTheActualTeardown()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var registry = sp.GetRequiredService<LlmSessionRegistry>();
        var manager = NewManager(sp);
        var runtime = (await Request(manager, second)).Runtime!;
        var registryId = ComposerUserState.RegistryId(runtime.State);
        Assert.Contains(registryId, registry.GetAll().Select(row => row.SessionId));

        // ── The EARLY-unregister variant: unregister BEFORE the teardown, then let the actor really go. ──
        // The actor is torn down for real, and its shutdown path RE-PUBLISHES its row.
        registry.Unregister(registryId);
        Assert.DoesNotContain(registryId, registry.GetAll().Select(row => row.SessionId));
        await runtime.Composer.DisposeAsync().AsTask();
        await GuardAsync(runtime.Composer.TeardownCompletion, "orphaned runtime's ACTUAL teardown completion");

        // The row SURVIVED: the actor re-published it while the teardown the unregister preceded was running. This
        // is the leak the ordering rule exists to prevent — and it is exactly what moving the cleanup's unregister
        // step to the front would cause.
        Assert.Contains(registryId, registry.GetAll().Select(row => row.SessionId));

        // ── The CORRECT order: unregister AFTER the teardown join removes the row for good. ──
        registry.Unregister(registryId);
        Assert.DoesNotContain(registryId, registry.GetAll().Select(row => row.SessionId));

        // The cleanup's own final assertions therefore hold: the row is gone and the actor is really down.
        Assert.True(ActorCompleted(runtime.Composer));
        Assert.True(runtime.Composer.TeardownCompletion.IsCompleted);
    }

    /// <summary>
    /// The REMAINING cleanup steps run IN ORDER even when the logger throws at the FIRST one: composer disposal,
    /// then the actual teardown join, then the subscriber cleanup, then the attachment policy, and only THEN the
    /// registry unregister and the cache eviction.
    /// </summary>
    /// <remarks>
    /// The phase-2 vectors each proved that the steps AFTER a contained site still ran, but none pinned their
    /// ORDER. This vector records the exact production order from a single run, so a reordering (or a step that
    /// silently skipped ahead) is observable rather than invisible behind "the end state looked right".
    /// </remarks>
    [Fact]
    public async Task ThrowingLogger_AllRemainingCleanupSteps_RunInOrder_EvenWhenTheFirstOneThrows()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var sentinel = new InvalidOperationException("deliberate connect fault for the ordered-cleanup vector");
        var steps = new List<string>();
        var stepLock = new object();

        var teardownJoins = new ConcurrentQueue<Task>();
        manager.OnJoiningTeardown = (_, task) =>
        {
            lock (stepLock) steps.Add("teardown-join");
            teardownJoins.Enqueue(task);
        };
        var unsubscribes = 0;
        _bus.OnUnsubscribe = () => { lock (stepLock) steps.Add("subscriber"); Interlocked.Increment(ref unsubscribes); };
        Composer? failedComposer = null;

        _connectHook = async (composer, token) =>
        {
            failedComposer = composer;
            var attachments = Field<ComposerAttachmentService>(composer, "_attachmentService");
            using var content = new MemoryStream([4, 2, 4, 2]);
            var saved = await attachments.SaveAsync("ordered-pending.png", content, token);
            Assert.True(saved.Success, saved.Error);
            // Connect for real FIRST, so an agent EXISTS and its disposal hook can genuinely fault — otherwise
            // the armed secondary cleanup failure below would be unreachable and this vector inert.
            await composer.ConnectAsync(token);
            Assert.True(composer.IsConnected);
            // Record the composer-disposal attempt from INSIDE it, so the order is observed at the real call site.
            Field<ComposerAgentService>(composer, "_agentService").OnAgentDisposing = _ =>
            {
                lock (stepLock) steps.Add("composer-disposal");
                throw new IOException("deliberate composer-disposal fault inside the ordered vector");
            };
            throw sentinel;
        };

        var failed = await Bounded(Request(manager, second), "ordered-cleanup failure settles");

        // The ORIGINAL failure is authoritative.
        Assert.Equal(ComposerRuntimeStatus.Failed, failed.Status);
        Assert.Same(sentinel, failed.Failure);
        Assert.Null(failed.Runtime);

        // The contained site was reached (the composer disposal failed inside the manager's cleanup).
        Assert.True(logger.LoggedCalls > 0, "The throwing logger was never reached.");
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal($"Disposing the partial Composer of user {second.UserId} failed", warning.Message);

        // The ORDER of the steps that ran, as production performed them: the composer disposal, then the ACTUAL
        // teardown join, then the subscriber cleanup.
        lock (stepLock)
        {
            Assert.Equal(["composer-disposal", "teardown-join", "subscriber"], steps);
        }

        // The teardown join came after the composer disposal and before the subscriber cleanup: the manager joined
        // the EXACT teardown task, and the registry row was dropped only afterwards.
        Assert.NotNull(failedComposer);
        Assert.Contains(teardownJoins, task => ReferenceEquals(task, failedComposer.TeardownCompletion));
        Assert.True(failedComposer.TeardownCompletion.IsCompleted);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, (await Retry(manager, second)).Refusal);
    }

    /// <summary>
    /// AC4: disposal is driven ONLY by the manager's three lifecycle paths — removal, failure and shutdown — and
    /// NOTHING else disposes the Composer or its subscriber. A plain <c>GetOrCreateAsync</c> never disposes, and
    /// the handle itself cannot.
    /// </summary>
    /// <remarks>
    /// The disposal counters are observed on the runtime's OWN chat client (the agent's client is disposed with
    /// the agent) and on the bus subscriber count. A second disposal path — anywhere — would show up as a client
    /// disposed twice or a subscriber detached twice.
    /// </remarks>
    [Fact]
    public async Task ManagerOwnedDisposal_OnlyTheLifecyclePathsDispose_TheHandleItselfNeverDoes()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var client = new CountingClient();
        _clientFactory = _ => client;

        // ── A plain create/connect disposes NOTHING. ──
        var runtime = (await Request(manager, second)).Runtime!;
        Assert.True(runtime.Composer.IsConnected);
        Assert.Equal(0, client.DisposeCount);
        Assert.Equal(1, _bus.SubscriberCount);

        // Repeated cache hits change nothing either.
        Assert.Same(runtime, (await Request(manager, second)).Runtime);
        Assert.Same(runtime, (await Retry(manager, second)).Runtime);
        Assert.Equal(0, client.DisposeCount);
        Assert.Equal(1, _bus.SubscriberCount);

        // The handle exposes no disposal entry point at all, so a holder cannot drive teardown.
        var handle = runtime;
        var disposeMembers = handle.GetType().GetMethods(System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();
        Assert.DoesNotContain("DisposeAsync", disposeMembers);
        Assert.DoesNotContain("Dispose", disposeMembers);
        // The handle's RUNTIME type implements neither interface (the compile-time check is vacuous here: the
        // static type no longer implements them at all, which is why the runtime check is the meaningful one).
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(handle.GetType()));
        Assert.False(typeof(IDisposable).IsAssignableFrom(handle.GetType()));

        // ── REMOVAL disposes: exactly once. ──
        Assert.True(await Bounded(Remove(manager, second.UserId), "removal disposes the runtime"));
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(0, _bus.SubscriberCount);

        // ── A RECREATED runtime is disposed by SHUTDOWN: exactly once, on its own client. ──
        var secondClient = new CountingClient();
        _clientFactory = _ => secondClient;
        var recreated = (await Request(manager, second)).Runtime!;
        Assert.NotSame(runtime, recreated);
        Assert.Equal(0, secondClient.DisposeCount);
        await GuardAsync(manager.ShutdownAsync(), "shutdown disposes the recreated runtime");
        Assert.Equal(1, secondClient.DisposeCount);
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.True(ActorCompleted(recreated.Composer));
    }

    /// <summary>
    /// ATTACHMENT-CLEARING POLICY under a throwing logger: a GENUINE failure clears the user's pending
    /// attachments, while a LIFECYCLE cancellation (removal/shutdown) does NOT — the containment changes the
    /// robustness of the reporting, never the policy.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingLogger_AttachmentPolicy_IsUnchanged_GenuineFailureClearsAndLifecyclePreserves(bool shutdown)
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var logger = new ThrowingLogger<ComposerRuntimeManager>();
        var manager = NewManager(sp, logger);
        var sentinel = new InvalidOperationException("deliberate connect fault for the attachment-policy control");
        ComposerAttachmentService? failedAttachments = null;
        string? pendingPath = null;

        _connectHook = async (composer, token) =>
        {
            failedAttachments = Field<ComposerAttachmentService>(composer, "_attachmentService");
            using var content = new MemoryStream([8, 8, 8, 8]);
            var saved = await failedAttachments.SaveAsync("policy-pending.png", content, token);
            Assert.True(saved.Success, saved.Error);
            pendingPath = Path.Combine(failedAttachments.AttachmentsRootPath, saved.Attachment!.SavedRelativePath);
            Assert.True(File.Exists(pendingPath));
            throw sentinel;
        };

        var failed = await Bounded(Request(manager, second), "attachment-policy failure settles");
        Assert.Equal(ComposerRuntimeStatus.Failed, failed.Status);
        Assert.Same(sentinel, failed.Failure);
        Assert.NotNull(failedAttachments);
        Assert.NotNull(pendingPath);

        // GENUINE FAILURE ⇒ the partial attachments ARE cleared (the policy is unchanged by containment).
        Assert.False(File.Exists(pendingPath));

        // ── The LIFECYCLE half: a deferred runtime's uploaded bytes SURVIVE removal/shutdown. ──
        _connectHook = null;
        var lifecycleLogger = new ThrowingLogger<ComposerRuntimeManager>();
        var lifecycleManager = NewManager(sp, lifecycleLogger);
        var lifecycleRuntime = (await Request(lifecycleManager, second)).Runtime!;
        using var lifecycleContent = new MemoryStream([9, 9, 9, 9]);
        var lifecycleSaved = await lifecycleRuntime.Attachments.SaveAsync("lifecycle-pending.png", lifecycleContent, Ct);
        Assert.True(lifecycleSaved.Success, lifecycleSaved.Error);
        var lifecyclePath = Path.Combine(lifecycleRuntime.Attachments.AttachmentsRootPath,
            lifecycleSaved.Attachment!.SavedRelativePath);
        Assert.True(File.Exists(lifecyclePath));

        Task lifecycle = shutdown ? lifecycleManager.ShutdownAsync() : Remove(lifecycleManager, second.UserId);
        await GuardAsync(lifecycle, "lifecycle completes under a throwing logger");

        // LIFECYCLE CANCELLATION ⇒ the user's content is PRESERVED (never cleared).
        Assert.True(File.Exists(lifecyclePath),
            "A lifecycle cancellation must never clear the user's attachments.");
        Assert.True(lifecycleRuntime.Attachments.MarkAsSent(lifecycleSaved.Attachment).Success);
        Assert.True(ActorCompleted(lifecycleRuntime.Composer));
    }

    /// <summary>
    /// STRUCTURAL PIN on the containment itself: EVERY cleanup/teardown warning in the manager goes through the ONE
    /// contained helper, and the helper itself can never throw.
    /// </summary>
    /// <remarks>
    /// This is the inventory that makes the per-site vectors complete: it proves there is no cleanup log site
    /// left that bypasses the containment, so no per-site vector can be silently defeated by a tenth, uncovered
    /// call site appearing in the future.
    /// </remarks>
    [Fact]
    public void EveryCleanupWarningInTheManager_GoesThroughTheContainedSafeLogHelper()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration", "ComposerRuntimeManager.cs"))
            .ReplaceLineEndings("\n");

        // The manager writes warnings through EXACTLY ONE sink call, and that call is inside the contained helper.
        Assert.Equal(1, CountOccurrences(source, "_logger.LogWarning("));
        Assert.Equal(10, CountOccurrences(source, "LogCleanupWarning(")); // 9 call sites + the helper's own declaration
        // No other logging API is reachable from the manager's cleanup paths.
        Assert.DoesNotContain("_logger.LogError", source);
        Assert.DoesNotContain("_logger.LogInformation", source);
        Assert.DoesNotContain("_logger.LogDebug", source);
        Assert.DoesNotContain("_logger.LogCritical", source);

        // The helper's own body contains the log call in a try/catch that swallows everything.
        var helperStart = source.IndexOf("private void LogCleanupWarning(", StringComparison.Ordinal);
        Assert.True(helperStart > 0, "The contained safe-log helper is missing from the manager.");
        var helperBody = source[helperStart..];
        Assert.Contains("try { _logger.LogWarning(exception, message, userId); }", helperBody);
        Assert.Contains("catch", helperBody);
        // The catch that swallows the logger fault must come BEFORE the helper's closing brace.
        var catchIndex = helperBody.IndexOf("catch", StringComparison.Ordinal);
        var helperEnd = helperBody.IndexOf("\n    }\n", StringComparison.Ordinal);
        Assert.True(catchIndex > 0 && catchIndex < helperEnd,
            "The logger fault must be swallowed INSIDE the helper, not by its caller.");
    }

    /// <summary>Which contained cleanup site a vector arms.</summary>
    public enum CleanupSite { ComposerDisposal, SubscriberDisposal }

    /// <summary>
    /// Faults the live shared operation of a user's cache entry, so a test can make the entry teardown's own
    /// <c>await entry.Operation</c> throw — the ONLY way into <c>TearDownAsync</c>'s outer catch.
    /// </summary>
    /// <remarks>
    /// The operation's element type (<c>Settled</c>) is private to the manager, so the task is handled through its
    /// non-generic <see cref="Task"/> base: the faulting behaviour is what matters here, not the payload.
    /// </remarks>
    private static void FaultEntryOperation(ComposerRuntimeManager manager, int userId, Exception fault)
    {
        var entriesField = typeof(ComposerRuntimeManager).GetField("_entries",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)entriesField.GetValue(manager)!;
        Assert.True(entries.Contains(userId), "The user has no cache entry to fault.");
        var entry = entries[userId]!;

        // The entry owns its operation as `Task<Settled>`, and `Settled` is private to the manager. The faulted
        // task is therefore built through the generic `Task.FromException<T>` whose type argument is read off the
        // property — the entry is then handed exactly the task its teardown awaits.
        var operationProperty = entry.GetType().GetProperty("Operation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!;
        var operation = (Task)operationProperty.GetValue(entry)!;
        Assert.True(operation.IsCompleted, "The shared operation must have settled before it can be replaced.");

        var fromException = typeof(Task).GetMethods()
            .Single(m => m.Name == nameof(Task.FromException)
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(Exception))
            .MakeGenericMethod(operationProperty.PropertyType.GetGenericArguments()[0]);
        var faulted = (Task)fromException.Invoke(null, [fault])!;
        operationProperty.SetValue(entry, faulted);
    }

    /// <summary>An <see cref="IChatClient"/> that counts its own disposal, so a double disposal is observable.</summary>
    private sealed class CountingClient : IChatClient
    {
        private int _disposals;
        public int DisposeCount => Volatile.Read(ref _disposals);

        public void Dispose() => Interlocked.Increment(ref _disposals);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

}

