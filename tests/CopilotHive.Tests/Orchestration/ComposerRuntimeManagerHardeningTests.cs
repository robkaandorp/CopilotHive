using System.Collections.Concurrent;
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
