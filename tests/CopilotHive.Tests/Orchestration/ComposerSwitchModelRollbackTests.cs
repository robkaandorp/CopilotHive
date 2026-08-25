using System.Reflection;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Focused tests for the model-switch commit boundary of
/// <see cref="ComposerAgentService.SwitchModelAsync(ValidatedModelSelection, CancellationToken)"/>:
/// the four mutated scalars are reverted on any PRE-COMMIT failure (apply or client/agent
/// creation), the ORIGINAL failure is authoritative (never masked by cleanup/logging), and the
/// POST-COMMIT registry publish and final success log are independently best-effort.
/// </summary>
public sealed class ComposerSwitchModelRollbackTests
{
    // ── Seams ──

    /// <summary>
    /// Logger that throws when the formatted message contains <paramref name="messageFragment"/>.
    /// Used for log fragments that only occur on the code path under test.
    /// </summary>
    private sealed class FragmentThrowingLogger(string messageFragment, Exception failure) : ILogger
    {
        private int _throws;

        /// <summary>Number of times the fragment was matched (and thrown for).</summary>
        internal int Throws => Volatile.Read(ref _throws);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).Contains(messageFragment, StringComparison.Ordinal))
                return;

            Interlocked.Increment(ref _throws);
            throw failure;
        }
    }

    /// <summary>One fragment→exception rule for <see cref="ArmableFragmentThrowingLogger"/>.</summary>
    private sealed record ThrowSpec(string Fragment, Exception Failure);

    /// <summary>
    /// Logger that throws per-fragment exceptions — but ONLY while <see cref="Armed"/> is true, so
    /// the initial connect completes normally and the failure is forced on the later SWITCH. Each
    /// fragment throws its OWN instance so a test can tell exactly which log call failed, and each
    /// match is counted so a test can prove the log call was genuinely reached.
    /// </summary>
    private sealed class ArmableFragmentThrowingLogger(IReadOnlyList<ThrowSpec> throwSpecs) : ILogger
    {
        private volatile bool _armed;
        private readonly Dictionary<string, int> _throwCounts = [];
        private readonly Lock _lock = new();

        internal bool Armed
        {
            get => _armed;
            set => _armed = value;
        }

        /// <summary>Number of times the given fragment matched (and threw).</summary>
        internal int ThrowsFor(string fragment)
        {
            lock (_lock)
                return _throwCounts.TryGetValue(fragment, out var count) ? count : 0;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!_armed)
                return;

            var message = formatter(state, exception);
            foreach (var spec in throwSpecs)
            {
                if (!message.Contains(spec.Fragment, StringComparison.Ordinal))
                    continue;

                lock (_lock)
                    _throwCounts[spec.Fragment] = (_throwCounts.TryGetValue(spec.Fragment, out var c) ? c : 0) + 1;
                throw spec.Failure;
            }
        }
    }

    /// <summary>Logger that hands every formatted message to an observer (never throws).</summary>
    private sealed class MessageObservingLogger(Action<string> onMessage) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => onMessage(formatter(state, exception));
    }

    /// <summary>
    /// Chat-client factory that records every requested model and returns a caller-supplied client,
    /// so a test can prove exactly which clients were created (not merely that a field is set).
    /// </summary>
    private sealed class RecordingChatClientFactory(Func<string, IChatClient> create)
    {
        private readonly List<string> _requestedModels = [];
        private readonly Lock _lock = new();

        internal IReadOnlyList<string> RequestedModels
        {
            get { lock (_lock) return [.. _requestedModels]; }
        }

        internal Func<string, IChatClient> Delegate => modelId =>
        {
            lock (_lock)
                _requestedModels.Add(modelId);
            return create(modelId);
        };
    }

    /// <summary>
    /// Catalog seam counting enumerations. <c>ValidateAvailableModel</c> captures exactly ONE
    /// snapshot per invocation, so counting full enumerations counts validations.
    /// </summary>
    private sealed class CountingCatalog(IReadOnlyList<string> models) : IReadOnlyList<string>
    {
        private int _enumerations;

        internal int EnumerationCount => Volatile.Read(ref _enumerations);

        public string this[int index] => models[index];

        public int Count => models.Count;

        public IEnumerator<string> GetEnumerator()
        {
            Interlocked.Increment(ref _enumerations);
            return models.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ── Helpers ──

    private static ComposerAgentService CreateService(
        string stateDir,
        Func<string, IChatClient> chatClientFactory,
        ILogger logger,
        string model = "model-a",
        int maxContextTokens = 64_000,
        ReasoningEffort? configuredReasoningEffort = null,
        HiveConfigFile? hiveConfig = null,
        LlmSessionRegistry? sessionRegistry = null,
        IReadOnlyList<string>? startupAvailableModels = null)
        => new(
            model,
            maxContextTokens,
            50,
            configuredReasoningEffort,
            hiveConfig,
            "system prompt",
            [],
            null,
            stateDir,
            null,
            logger,
            chatClientFactory,
            sessionRegistry,
            startupAvailableModels ?? [model],
            null,
            null,
            false,
            []);

    /// <summary>
    /// Config catalog with a context window on <c>model-b</c> ONLY, so a switch a→b is guaranteed
    /// to mutate <c>_maxContextTokens</c> (making its revert observable).
    /// </summary>
    private static HiveConfigFile TwoModelConfig() => new()
    {
        Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a" },
                new ModelEntry { Name = "model-b", ContextWindow = 128_000 },
            ],
        },
    };

    private static T? GetField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        return (T?)field.GetValue(obj);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"composer-switch-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Asserts the four switch-mutated scalars hold the expected values.</summary>
    private static void AssertScalars(
        ComposerAgentService service,
        string? model,
        ReasoningEffort? reasoningEffort,
        int maxContextTokens)
    {
        Assert.Equal(model, service.Model);
        Assert.Equal(reasoningEffort, service.ReasoningEffort);
        Assert.Equal(reasoningEffort, GetField<ReasoningEffort?>(service, "_configuredReasoningEffort"));
        Assert.Equal(maxContextTokens, service.MaxContextTokens);
    }

    /// <summary>Writes a persisted session file that MUST NOT be read by a switch.</summary>
    private static async Task WriteSessionFileAsync(string stateDir, string message, CancellationToken ct)
    {
        var persisted = AgentSession.Create("composer");
        persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, message));
        await persisted.SaveAsync(Path.Combine(stateDir, "composer-session.json"), ct);
    }

    // ── 1. Successful switch commits everything ──

    /// <summary>
    /// A successful switch COMMITS all four scalars, creates the new client + agent, publishes the
    /// registry entry, writes the final success log, preserves the session INSTANCE, and never
    /// disk-loads.
    /// <para>
    /// Removal-proof: a session file containing a distinctive message is written between the
    /// connect and the switch. Any disk-load added to the switch path would replace the session
    /// instance and surface that message, failing the reference-equality and history assertions.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_Success_CommitsScalars_CreatesClientAndAgent_Publishes_LogsFinal_PreservesSession()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var messages = new List<string>();
            var logger = new MessageObservingLogger(m => { lock (messages) messages.Add(m); });
            var factory = new RecordingChatClientFactory(_ => new Mock<IChatClient>().Object);
            var registry = new LlmSessionRegistry();

            service = CreateService(
                stateDir,
                factory.Delegate,
                logger,
                configuredReasoningEffort: ReasoningEffort.Low,
                hiveConfig: TwoModelConfig(),
                sessionRegistry: registry);

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(service.IsConnected);
            Assert.False(service.SessionLoadedFromDisk);

            var sessionBefore = service.Session;
            var agentBefore = service.Agent;
            var clientBefore = service.ChatClient;

            // Planted AFTER the connect: a switch must NEVER read it.
            await WriteSessionFileAsync(stateDir, "must not be loaded by a switch", TestContext.Current.CancellationToken);

            var selection = service.ValidateAvailableModel("model-b", ReasoningEffort.High);
            await service.SwitchModelAsync(selection, TestContext.Current.CancellationToken);

            // Scalars COMMITTED (including the conditional context-window update).
            AssertScalars(service, "model-b", ReasoningEffort.High, 128_000);

            // A new client and agent really were created.
            Assert.Equal(["model-a", "model-b"], factory.RequestedModels);
            Assert.True(service.IsConnected);
            Assert.NotNull(service.Agent);
            Assert.NotSame(agentBefore, service.Agent);
            Assert.NotSame(clientBefore, service.ChatClient);

            // Registry published with the new model.
            var entry = Assert.Single(registry.GetAll(), s => s.SessionId == "composer");
            Assert.Equal("model-b", entry.Model);
            Assert.Equal(128_000, entry.MaxTokens);
            Assert.Equal(ReasoningEffort.High, entry.ReasoningEffort);

            // Final success log written.
            List<string> observed;
            lock (messages) observed = [.. messages];
            Assert.Contains(observed, m => m.Contains("Composer switched to model 'model-b'", StringComparison.Ordinal));

            // Session PRESERVED by reference — and NO disk-load happened.
            Assert.Same(sessionBefore, service.Session);
            Assert.Empty(service.Session.MessageHistory);
            Assert.False(service.SessionLoadedFromDisk);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 2. No double validation ──

    /// <summary>
    /// The snapshot overload performs ZERO validations of its own — the caller's single
    /// <c>ValidateAvailableModel</c> is the only one per selection operation. The control delta
    /// (exactly 1 for the caller's explicit validation) proves the counting seam is not vacuous.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_SnapshotOverload_PerformsNoSecondValidation()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var catalog = new CountingCatalog(["model-a", "model-b"]);
            service = CreateService(
                stateDir,
                _ => new Mock<IChatClient>().Object,
                NullLogger<ComposerAgentService>.Instance,
                startupAvailableModels: catalog);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var beforeValidation = catalog.EnumerationCount;
            var selection = service.ValidateAvailableModel("model-b", ReasoningEffort.Medium);

            // Control: the caller's explicit validation takes exactly ONE snapshot.
            var afterValidation = catalog.EnumerationCount;
            Assert.Equal(1, afterValidation - beforeValidation);

            await service.SwitchModelAsync(selection, TestContext.Current.CancellationToken);

            // The overload added ZERO further snapshots.
            Assert.Equal(afterValidation, catalog.EnumerationCount);
            Assert.Equal("model-b", service.Model);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 3. Client/agent creation failure reverts every scalar ──

    /// <summary>
    /// A PRE-COMMIT client/agent creation failure reverts ALL FOUR scalars, attempts disposal of
    /// the just-created client and agent (a THROWING <c>Dispose</c> is swallowed), leaves the
    /// Composer disconnected, preserves the session instance, and propagates the ORIGINAL creation
    /// exception.
    /// <para>
    /// Removal-proof: the failure is forced at the very END of agent creation (the agent's own
    /// creation log), so the client AND the agent genuinely exist at failure time — the disposal
    /// hook firing and the throwing <c>Dispose</c> being called prove the cleanup ran, and the
    /// pre-switch scalar values prove the revert ran.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_CreationThrows_RevertsAllScalars_DisposesCreated_PreservesSession_PropagatesOriginal()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var creationFailure = new InvalidOperationException("agent creation boom");
            var logger = new ArmableFragmentThrowingLogger(
                [new ThrowSpec("Composer CodingAgent created", creationFailure)]);

            var firstClient = new Mock<IChatClient>();
            var switchClient = new Mock<IChatClient>();
            switchClient.Setup(c => c.Dispose()).Throws(new InvalidOperationException("dispose boom"));

            var calls = 0;
            var factory = new RecordingChatClientFactory(
                _ => ++calls == 1 ? firstClient.Object : switchClient.Object);

            service = CreateService(
                stateDir,
                factory.Delegate,
                logger,
                configuredReasoningEffort: ReasoningEffort.Low,
                hiveConfig: TwoModelConfig());

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            AssertScalars(service, "model-a", ReasoningEffort.Low, 64_000);
            var sessionBefore = service.Session;
            var agentBefore = service.Agent;

            // Records WHICH agents were disposed, so the newly created agent's disposal can be
            // distinguished from the switch's ordinary teardown of the previous agent.
            var disposedAgents = new List<CodingAgent>();
            service.OnAgentDisposing = a => { lock (disposedAgents) disposedAgents.Add(a); };

            // Arm AFTER the connect: only the SWITCH's agent creation fails.
            logger.Armed = true;

            var selection = service.ValidateAvailableModel("model-b", ReasoningEffort.High);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SwitchModelAsync(selection, TestContext.Current.CancellationToken));

            logger.Armed = false;
            service.OnAgentDisposing = null;

            // The ORIGINAL creation exception is authoritative — the throwing Dispose is swallowed.
            Assert.Same(creationFailure, ex);
            Assert.Equal(1, logger.ThrowsFor("Composer CodingAgent created"));

            // ── ALL FOUR scalars reverted ──
            AssertScalars(service, "model-a", ReasoningEffort.Low, 64_000);

            // The failure really was POST-creation: the new client was created, its Dispose was
            // ATTEMPTED (and threw — swallowed), and the NEWLY created agent was disposed too.
            Assert.Equal(["model-a", "model-b"], factory.RequestedModels);
            switchClient.Verify(c => c.Dispose(), Times.AtLeastOnce);

            List<CodingAgent> disposed;
            lock (disposedAgents) disposed = [.. disposedAgents];

            // First the switch's teardown disposed the PREVIOUS agent, then the failure cleanup
            // disposed the agent the failed switch had just created (a distinct instance).
            Assert.Equal(2, disposed.Count);
            Assert.Same(agentBefore, disposed[0]);
            Assert.NotSame(agentBefore, disposed[1]);

            // Composer is DISCONNECTED and the session instance is preserved.
            Assert.False(service.IsConnected);
            Assert.Null(service.ChatClient);
            Assert.Null(service.Agent);
            Assert.Same(sessionBefore, service.Session);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 4. ApplyModelScalars failure reverts every scalar ──

    /// <summary>
    /// A failing <c>ApplyModelScalars</c> (its context-window log throws AFTER the model and
    /// reasoning fields were already assigned) reverts ALL FOUR scalars — the partial mutation is
    /// undone — preserves the session, and propagates the ORIGINAL apply exception.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_ApplyThrows_RevertsAllScalars_PreservesSession_PropagatesOriginal()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            // "Updating Composer context window" is logged ONLY from ApplyModelScalars, after
            // _model/_configuredReasoningEffort/_reasoningEffort were already overwritten.
            var applyFailure = new InvalidOperationException("apply boom");
            var logger = new FragmentThrowingLogger("Updating Composer context window", applyFailure);

            var factory = new RecordingChatClientFactory(_ => new Mock<IChatClient>().Object);

            service = CreateService(
                stateDir,
                factory.Delegate,
                logger,
                configuredReasoningEffort: ReasoningEffort.Low,
                hiveConfig: TwoModelConfig());

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            AssertScalars(service, "model-a", ReasoningEffort.Low, 64_000);
            var sessionBefore = service.Session;

            var selection = service.ValidateAvailableModel("model-b", ReasoningEffort.High);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SwitchModelAsync(selection, TestContext.Current.CancellationToken));

            Assert.Same(applyFailure, ex);
            Assert.Equal(1, logger.Throws);

            // ── ALL FOUR scalars reverted (the apply had already overwritten model + reasoning) ──
            AssertScalars(service, "model-a", ReasoningEffort.Low, 64_000);

            // The failure was PRE-creation: no client for the new model was ever requested.
            Assert.Equal(["model-a"], factory.RequestedModels);
            Assert.False(service.IsConnected);
            Assert.Same(sessionBefore, service.Session);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 5. Throwing cleanup logger cannot skip the rollback nor mask the original ──

    /// <summary>
    /// A THROWING logger inside the pre-commit cleanup neither skips the scalar rollback nor masks
    /// the original creation exception.
    /// <para>
    /// Removal-proof: the creation failure and the cleanup-warning failure are DISTINCT exception
    /// instances, and the just-created client's <c>Dispose</c> throws so the cleanup warning is
    /// genuinely reached (asserted by its throw count). If the rollback ran after the fallible
    /// cleanup, the scalars would still read the NEW model; if the cleanup-log throw were not
    /// swallowed, "cleanup log boom" would propagate and the <see cref="Assert.Same(object?, object?)"/>
    /// would fail.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_PreCommitCleanupLogThrows_RollbackStillRuns_OriginalExceptionPropagates()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var creationFailure = new InvalidOperationException("agent creation boom");
            var logger = new ArmableFragmentThrowingLogger(
            [
                new ThrowSpec("Composer CodingAgent created", creationFailure),
                new ThrowSpec("Failed to dispose Composer chat clients during cleanup",
                    new InvalidOperationException("cleanup log boom")),
            ]);

            var firstClient = new Mock<IChatClient>();
            var switchClient = new Mock<IChatClient>();
            switchClient.Setup(c => c.Dispose()).Throws(new InvalidOperationException("dispose boom"));

            var calls = 0;
            service = CreateService(
                stateDir,
                _ => ++calls == 1 ? firstClient.Object : switchClient.Object,
                logger,
                configuredReasoningEffort: ReasoningEffort.Low,
                hiveConfig: TwoModelConfig());

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var sessionBefore = service.Session;
            logger.Armed = true;

            var selection = service.ValidateAvailableModel("model-b", ReasoningEffort.High);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SwitchModelAsync(selection, TestContext.Current.CancellationToken));

            logger.Armed = false;

            // The cleanup warning was GENUINELY reached (the disposal failure forced it) and threw
            // its own DISTINCT exception…
            Assert.True(logger.ThrowsFor("Failed to dispose Composer chat clients during cleanup") > 0,
                "The cleanup warning must have been reached (disposal failure logged)");

            // …yet the ORIGINAL pre-commit exception is what propagates.
            Assert.Same(creationFailure, ex);

            // …and the rollback ran BEFORE the throwing cleanup log.
            AssertScalars(service, "model-a", ReasoningEffort.Low, 64_000);
            Assert.False(service.IsConnected);
            Assert.Same(sessionBefore, service.Session);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 6. Throwing FINAL success log is best-effort ──

    /// <summary>
    /// A THROWING final success log does NOT propagate as a switch failure, and the COMMITTED
    /// outcome is unchanged: scalars committed, connection live, registry published, session
    /// preserved. The throw counter proves the final log really was attempted (and really threw),
    /// so the test is not vacuous.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_FinalSuccessLogThrows_DoesNotPropagate_CommittedOutcomeUnchanged()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            // "Composer switched to model" is logged ONLY by the switch's final success log.
            var logger = new FragmentThrowingLogger(
                "Composer switched to model",
                new InvalidOperationException("final log boom"));

            var registry = new LlmSessionRegistry();
            service = CreateService(
                stateDir,
                _ => new Mock<IChatClient>().Object,
                logger,
                configuredReasoningEffort: ReasoningEffort.Low,
                hiveConfig: TwoModelConfig(),
                sessionRegistry: registry);

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var sessionBefore = service.Session;

            var selection = service.ValidateAvailableModel("model-b", ReasoningEffort.High);

            // No exception: the final log is best-effort.
            await service.SwitchModelAsync(selection, TestContext.Current.CancellationToken);

            // The final log really was attempted and really threw.
            Assert.Equal(1, logger.Throws);

            // Committed outcome UNCHANGED by the logging failure.
            AssertScalars(service, "model-b", ReasoningEffort.High, 128_000);
            Assert.True(service.IsConnected);
            Assert.NotNull(service.Agent);
            Assert.Same(sessionBefore, service.Session);

            var entry = Assert.Single(registry.GetAll(), s => s.SessionId == "composer");
            Assert.Equal("model-b", entry.Model);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 7. A failed pre-commit switch publishes nothing ──

    /// <summary>
    /// A PRE-COMMIT switch failure must NOT publish a registry entry for the new model — the only
    /// entry is the one the prior successful connect published.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_PreCommitFailure_DoesNotPublishNewRegistryEntry()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var registry = new LlmSessionRegistry();
            var creationFailure = new InvalidOperationException("creation boom");
            var calls = 0;

            service = CreateService(
                stateDir,
                _ => ++calls == 1 ? new Mock<IChatClient>().Object : throw creationFailure,
                NullLogger<ComposerAgentService>.Instance,
                configuredReasoningEffort: ReasoningEffort.Low,
                hiveConfig: TwoModelConfig(),
                sessionRegistry: registry);

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var published = Assert.Single(registry.GetAll(), s => s.SessionId == "composer");
            Assert.Equal("model-a", published.Model);

            var selection = service.ValidateAvailableModel("model-b", ReasoningEffort.High);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SwitchModelAsync(selection, TestContext.Current.CancellationToken));
            Assert.Same(creationFailure, ex);

            // NO entry for the failed model: the sole entry is still the connect's.
            var after = Assert.Single(registry.GetAll());
            Assert.Equal("composer", after.SessionId);
            Assert.Equal("model-a", after.Model);
            Assert.DoesNotContain(registry.GetAll(), s => s.Model == "model-b");

            // Scalars reverted despite the registry entry surviving.
            AssertScalars(service, "model-a", ReasoningEffort.Low, 64_000);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }
}
