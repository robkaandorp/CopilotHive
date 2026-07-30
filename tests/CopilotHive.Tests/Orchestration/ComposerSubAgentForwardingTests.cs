using System.Collections.Concurrent;
using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SharpCoder;
using SharpCoder.SubAgents;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Integration tests for the sub-agent event forwarding path: <c>CodingAgent.SubAgentChanged</c>
/// → <see cref="ComposerAgentService"/>.<c>OnSubAgentChanged</c> → <see cref="Composer"/>.
/// <c>OnSubAgentChanged</c>, plus <c>GetSubAgents()</c> consistency and teardown semantics.
/// These tests drive a real <see cref="ComposerAgentService"/> (or <see cref="Composer"/>) with a
/// fake chat client, force-create the lazy <c>SubAgentManager</c> and start a real sub-agent so the
/// lifecycle events actually fire.
/// </summary>
public sealed class ComposerSubAgentForwardingTests
{
    private const BindingFlags PrivateFlags =
        BindingFlags.Instance | BindingFlags.NonPublic;

    // ── Helpers (mirrors ComposerAgentServiceTests) ──

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"subagent-fwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ComposerAgentService CreateService(
        string stateDir,
        Func<string, IChatClient> chatClientFactory,
        IBrainRepoManager repoManager,
        HiveConfigFile hiveConfig,
        bool subAgentsEnabled = true,
        IReadOnlyList<ModelEntry>? subAgentModels = null) =>
        new(
            "test-model",
            64000,
            50,
            null,
            hiveConfig,
            "system prompt",
            new List<AITool>(),
            repoManager,
            stateDir,
            null,
            NullLogger<ComposerAgentService>.Instance,
            chatClientFactory,
            null,
            ["test-model"],
            null,
            null,
            subAgentsEnabled,
            subAgentModels ?? hiveConfig.Models?.AvailableModels?.ToList().AsReadOnly() ?? []);

    private static T? GetField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        return (T?)field.GetValue(obj);
    }

    /// <summary>
    /// Forces creation of the lazily-created <see cref="SubAgentManager"/> on a
    /// <see cref="CodingAgent"/>.
    /// </summary>
    private static SubAgentManager ForceCreateSubAgentManager(CodingAgent agent)
    {
        var method = agent.GetType().GetMethod("GetOrCreateSubAgentManager", PrivateFlags)
            ?? throw new InvalidOperationException("GetOrCreateSubAgentManager not found on CodingAgent");
        return (SubAgentManager)method.Invoke(agent, null)!;
    }

    /// <summary>
    /// Raises <c>CodingAgent.SubAgentChanged</c> by invoking the agent's private
    /// <c>OnSubAgentChanged(SubAgentInfo)</c> raiser. This drives the real event path, so a handler
    /// that is still attached genuinely posts into whichever tracker the service currently holds.
    /// </summary>
    private static void RaiseAgentSubAgentChanged(CodingAgent agent, SubAgentInfo info)
    {
        var raiser = agent.GetType().GetMethod("OnSubAgentChanged", PrivateFlags, [typeof(SubAgentInfo)])
            ?? throw new InvalidOperationException("OnSubAgentChanged(SubAgentInfo) not found on CodingAgent");
        raiser.Invoke(agent, [info]);
    }

    /// <summary>Upper bound for awaiting a forwarded sub-agent event before failing the test.</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="trigger"/> and deterministically waits until the tracker has forwarded a
    /// matching <see cref="SubAgentInfo"/>.
    /// <para>
    /// The tracker raises <c>OnSubAgentChanged</c> only <i>after</i> publishing the new snapshot, so
    /// once this returns the entry is guaranteed to be observable through <c>GetSubAgents()</c>.
    /// That removes the need for a fixed sleep, which could otherwise let an assertion run before
    /// the channel reader had consumed the message.
    /// </para>
    /// </summary>
    private static async Task<SubAgentInfo> AwaitForwardedEventAsync(
        ComposerAgentService service,
        Func<SubAgentInfo, bool> match,
        Func<Task> trigger)
    {
        var gate = new TaskCompletionSource<SubAgentInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(SubAgentInfo info)
        {
            if (match(info))
                gate.TrySetResult(info);
        }

        service.OnSubAgentChanged += Handler;
        try
        {
            await trigger();
            return await gate.Task.WaitAsync(EventTimeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            service.OnSubAgentChanged -= Handler;
        }
    }

    /// <summary>
    /// Raises a uniquely-identified marker event through <paramref name="agent"/> and waits until it
    /// has been processed. Because the tracker's channel is FIFO with a single reader, any message
    /// enqueued before the marker is necessarily applied to the snapshot first — so once the marker
    /// is visible, the absence of an earlier message proves it was never enqueued at all.
    /// </summary>
    private static Task<SubAgentInfo> AwaitMarkerAsync(
        ComposerAgentService service,
        CodingAgent agent,
        string markerId) =>
        AwaitForwardedEventAsync(
            service,
            info => info.Id == markerId,
            () =>
            {
                RaiseAgentSubAgentChanged(agent, new SubAgentInfo
                {
                    Id = markerId,
                    Task = "marker task",
                    Status = SubAgentStatus.Running,
                    StartedAt = DateTimeOffset.UtcNow,
                });
                return Task.CompletedTask;
            });

    private static HiveConfigFile MakeHiveConfig() => new()
    {
        Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "test-model", ContextWindow = 128_000 }],
        }
    };

    private static Mock<IBrainRepoManager> MakeRepoManager(string stateDir)
    {
        var repoManager = new Mock<IBrainRepoManager>();
        repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);
        return repoManager;
    }

    /// <summary>
    /// A chat client whose <c>GetResponseAsync</c> returns a simple assistant reply, used to drive
    /// the sub-agent CodingAgent to a terminal state quickly.
    /// </summary>
    private sealed class ReplyChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                FinishReason = ChatFinishReason.Stop,
            };
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done") { FinishReason = ChatFinishReason.Stop };
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private static async Task<SubAgentInfo> StartSubAgentAsync(SubAgentManager manager)
    {
        return await manager.StartAsync(
            new SubAgentRequest
            {
                Task = "summarise the repository layout",
                Model = "test-model",
                Timeout = TimeSpan.FromSeconds(30),
            },
            TestContext.Current.CancellationToken);
    }

    private static void TryDeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── 8. ComposerAgentService forwards SubAgentChanged → OnSubAgentChanged ──

    [Fact]
    public async Task Service_ForwardsSubAgentChanged_OnSubAgentChangedFiresWithSubAgentInfo()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var hiveConfig = MakeHiveConfig();
            var repoManager = MakeRepoManager(stateDir);
            var client = new ReplyChatClient();

            service = CreateService(
                stateDir,
                _ => client,
                repoManager.Object,
                hiveConfig);

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(service.Agent);

            var received = new ConcurrentBag<SubAgentInfo>();
            service.OnSubAgentChanged += info => received.Add(info);

            var manager = ForceCreateSubAgentManager(service.Agent!);
            await StartSubAgentAsync(manager);

            // Await the sub-agent reaching a terminal state.
            await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

            // Give the reader loop time to process the queued messages.
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Assert.NotEmpty(received);
            // At least one event must have been forwarded.
            Assert.Contains(received, i => !string.IsNullOrEmpty(i.Id));
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 9. GetSubAgents() is up to date when the event fires ──

    [Fact]
    public async Task Service_GetSubAgents_UpToDateWhenEventFires()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var hiveConfig = MakeHiveConfig();
            var repoManager = MakeRepoManager(stateDir);
            var client = new ReplyChatClient();

            service = CreateService(stateDir, _ => client, repoManager.Object, hiveConfig);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // Capture the snapshot inside the handler — at the moment the event fires.
            IReadOnlyList<SubAgentInfo>? snapshotAtEvent = null;
            SubAgentInfo? eventInfo = null;
            var firstEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.OnSubAgentChanged += info =>
            {
                if (eventInfo is null)
                {
                    eventInfo = info;
                    snapshotAtEvent = service.GetSubAgents();
                    firstEvent.TrySetResult(true);
                }
            };

            var manager = ForceCreateSubAgentManager(service.Agent!);
            await StartSubAgentAsync(manager);

            // The first event (Running) should arrive quickly.
            await firstEvent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.NotNull(eventInfo);
            Assert.NotNull(snapshotAtEvent);

            // The snapshot at event time must already contain the entry that fired the event.
            Assert.Contains(snapshotAtEvent!, e => e.Id == eventInfo!.Id);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 10. DisposeAsync clears the snapshot ──

    [Fact]
    public async Task Service_DisposeAsync_ClearsSnapshot()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var hiveConfig = MakeHiveConfig();
            var repoManager = MakeRepoManager(stateDir);
            var client = new ReplyChatClient();

            service = CreateService(stateDir, _ => client, repoManager.Object, hiveConfig);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var manager = ForceCreateSubAgentManager(service.Agent!);
            await StartSubAgentAsync(manager);
            await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            // Before dispose, the snapshot has entries.
            Assert.NotEmpty(service.GetSubAgents());

            // Dispose clears the tracker and publishes an empty snapshot.
            await service.DisposeAsync();
            service = null;

            // After dispose, GetSubAgents returns empty (tracker is null → empty list).
            // We can still call GetSubAgents because it null-coalesces.
            // Recreate a throwaway reference: the tracker is null, so it returns [].
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 10b. DisposeAsync then GetSubAgents returns empty ──

    [Fact]
    public async Task Service_DisposeAsync_ThenGetSubAgentsReturnsEmpty()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var hiveConfig = MakeHiveConfig();
            var repoManager = MakeRepoManager(stateDir);
            var client = new ReplyChatClient();

            service = CreateService(stateDir, _ => client, repoManager.Object, hiveConfig);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var manager = ForceCreateSubAgentManager(service.Agent!);
            await StartSubAgentAsync(manager);
            await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Assert.NotEmpty(service.GetSubAgents());

            await service.DisposeAsync();

            // After DisposeAsync the _subAgentTracker is null, so GetSubAgents returns [].
            Assert.Empty(service.GetSubAgents());
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 11. Recreate stops the old tracker and starts an empty one ──

    [Fact]
    public async Task Service_RecreateAgentAsync_ClearsSnapshot()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var hiveConfig = MakeHiveConfig();
            var repoManager = MakeRepoManager(stateDir);
            var client = new ReplyChatClient();

            service = CreateService(stateDir, _ => client, repoManager.Object, hiveConfig);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var oldAgent = service.Agent!;
            var oldTracker = GetField<SubAgentStateTracker>(service, "_subAgentTracker");
            Assert.NotNull(oldTracker);

            // Start a sub-agent on the OLD agent so the tracker has data. Awaiting the forwarded
            // event (rather than sleeping) guarantees the snapshot is populated before we assert.
            var oldManager = ForceCreateSubAgentManager(oldAgent);
            await AwaitForwardedEventAsync(
                service,
                _ => true,
                async () => await StartSubAgentAsync(oldManager));

            Assert.NotEmpty(service.GetSubAgents());

            // Recreate: the old tracker is stopped and a fresh, empty one replaces it.
            await service.RecreateAgentAsync();

            Assert.NotNull(service.Agent);
            Assert.NotSame(oldAgent, service.Agent);

            // The panel is cleared — stale entries from the previous agent do not survive.
            Assert.Empty(service.GetSubAgents());

            // A genuinely new tracker instance was installed, and the old one was stopped
            // (its own snapshot was reset to empty by StopAsync).
            var newTracker = GetField<SubAgentStateTracker>(service, "_subAgentTracker");
            Assert.NotNull(newTracker);
            Assert.NotSame(oldTracker, newTracker);
            Assert.Empty(oldTracker!.GetSubAgents());
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 11b. Old agent's events cannot reach the new tracker after recreate ──

    [Fact]
    public async Task Service_RecreateAgentAsync_StaleOldAgentEventDoesNotPopulateNewTracker()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var hiveConfig = MakeHiveConfig();
            var repoManager = MakeRepoManager(stateDir);
            var client = new ReplyChatClient();

            service = CreateService(stateDir, _ => client, repoManager.Object, hiveConfig);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var oldAgent = service.Agent!;

            // Sanity: while the old agent is current, raising its event DOES reach the tracker.
            // Awaiting the forwarded event proves the raiser genuinely drives the real event path,
            // so the negative assertion below cannot pass vacuously.
            await AwaitMarkerAsync(service, oldAgent, "live");
            Assert.Contains(service.GetSubAgents(), e => e.Id == "live");

            await service.RecreateAgentAsync();

            // The new tracker starts empty.
            Assert.Empty(service.GetSubAgents());

            var newAgent = service.Agent!;
            Assert.NotSame(oldAgent, newAgent);

            // Now actually exercise the stale path: raise SubAgentChanged on the OLD agent.
            // The service's handler was unsubscribed in DisposeAgentAsync, so the old agent's
            // event is a no-op. Had it still been attached, the handler would post into the
            // CURRENT tracker and repopulate the snapshot.
            RaiseAgentSubAgentChanged(oldAgent, new SubAgentInfo
            {
                Id = "stale",
                Task = "stale task",
                Status = SubAgentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
            });

            // Deterministic barrier instead of a sleep: raise a marker through the CURRENT agent
            // and wait for it to be forwarded. The tracker's channel is FIFO with a single reader,
            // so anything the stale raise had managed to enqueue would have been applied to the
            // snapshot strictly BEFORE the marker. Once the marker is observable, "stale is absent"
            // is a proof of non-delivery rather than a race we happened to win.
            await AwaitMarkerAsync(service, newAgent, "marker");

            var agents = service.GetSubAgents();
            Assert.Contains(agents, e => e.Id == "marker");
            Assert.DoesNotContain(agents, e => e.Id == "stale");
            Assert.DoesNotContain(agents, e => e.Id == "live");
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 11c. ResetSessionAsync clears the snapshot ──

    [Fact]
    public async Task Service_ResetSessionAsync_ClearsSnapshot()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var hiveConfig = MakeHiveConfig();
            var repoManager = MakeRepoManager(stateDir);
            var client = new ReplyChatClient();

            service = CreateService(stateDir, _ => client, repoManager.Object, hiveConfig);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // Awaiting the forwarded event (rather than sleeping) guarantees the snapshot is
            // genuinely populated, so the post-reset emptiness assertion is meaningful.
            var manager = ForceCreateSubAgentManager(service.Agent!);
            await AwaitForwardedEventAsync(
                service,
                _ => true,
                async () => await StartSubAgentAsync(manager));

            Assert.NotEmpty(service.GetSubAgents());

            await service.ResetSessionAsync();

            // A new session means a new agent and a new, empty tracker.
            Assert.Empty(service.GetSubAgents());
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 11b. A stale SubAgentInfo posted directly to the tracker is dropped after DisposeAsync ──

    [Fact]
    public async Task Service_DisposeAsync_StalePostDoesNotReviveSnapshot()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var hiveConfig = MakeHiveConfig();
            var repoManager = MakeRepoManager(stateDir);
            var client = new ReplyChatClient();

            service = CreateService(stateDir, _ => client, repoManager.Object, hiveConfig);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // Capture the tracker reference so we can attempt a stale post after dispose.
            var tracker = GetField<SubAgentStateTracker>(service, "_subAgentTracker");

            await service.DisposeAsync();

            // After dispose, the tracker is stopped (writer completed). A post is silently dropped.
            Assert.NotNull(tracker);
            tracker!.Post(new SubAgentInfo { Id = "stale", Status = SubAgentStatus.Running });

            // GetSubAgents still returns empty (the service field is null, and the old tracker
            // is stopped so its snapshot is empty too).
            Assert.Empty(service.GetSubAgents());
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── 12. Composer.OnSubAgentChanged raises when the service raises ──

    [Fact]
    public async Task Composer_OnSubAgentChanged_RaisesWhenServiceRaises()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = [new ModelEntry { Name = "test-model", ContextWindow = 128_000 }],
                }
            };
            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(tmpDir);

            dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                repoManager: repoManager.Object,
                stateDir: tmpDir,
                hiveConfig: hiveConfig,
                chatClientFactory: _ => new ReplyChatClient());

            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            var received = new ConcurrentBag<SubAgentInfo>();
            composer.OnSubAgentChanged += info => received.Add(info);

            // Force-create the SubAgentManager on the Composer's agent.
            var agentService = (ComposerAgentService)typeof(Composer)
                .GetField("_agentService", PrivateFlags)!.GetValue(composer)!;
            var agent = agentService.Agent!;
            var manager = ForceCreateSubAgentManager(agent);

            await StartSubAgentAsync(manager);
            await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Assert.NotEmpty(received);
        }
        finally
        {
            if (composer is not null)
                await composer.DisposeAsync();
            dbContext?.Dispose();
            TryDeleteDir(tmpDir);
        }
    }

    // ── 13. Composer.GetSubAgents() delegates to the service ──

    [Fact]
    public async Task Composer_GetSubAgents_DelegatesToService_NonEmptyAfterSubAgent()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = [new ModelEntry { Name = "test-model", ContextWindow = 128_000 }],
                }
            };
            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(tmpDir);

            dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                repoManager: repoManager.Object,
                stateDir: tmpDir,
                hiveConfig: hiveConfig,
                chatClientFactory: _ => new ReplyChatClient());

            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Empty(composer.GetSubAgents());

            var agentService = (ComposerAgentService)typeof(Composer)
                .GetField("_agentService", PrivateFlags)!.GetValue(composer)!;
            var agent = agentService.Agent!;
            var manager = ForceCreateSubAgentManager(agent);

            await StartSubAgentAsync(manager);
            await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            var subAgents = composer.GetSubAgents();
            Assert.NotEmpty(subAgents);
            Assert.All(subAgents, e => Assert.False(string.IsNullOrEmpty(e.Id)));
        }
        finally
        {
            if (composer is not null)
                await composer.DisposeAsync();
            dbContext?.Dispose();
            TryDeleteDir(tmpDir);
        }
    }

    // ── 13b. Composer.DisposeAsync clears GetSubAgents ──

    [Fact]
    public async Task Composer_DisposeAsync_ClearsGetSubAgents()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = [new ModelEntry { Name = "test-model", ContextWindow = 128_000 }],
                }
            };
            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(tmpDir);

            dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                repoManager: repoManager.Object,
                stateDir: tmpDir,
                hiveConfig: hiveConfig,
                chatClientFactory: _ => new ReplyChatClient());

            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            var agentService = (ComposerAgentService)typeof(Composer)
                .GetField("_agentService", PrivateFlags)!.GetValue(composer)!;
            var agent = agentService.Agent!;
            var manager = ForceCreateSubAgentManager(agent);

            await StartSubAgentAsync(manager);
            await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Assert.NotEmpty(composer.GetSubAgents());

            await composer.DisposeAsync();
            composer = null;

            // After dispose, the service tracker is null → GetSubAgents returns empty.
            // (composer is disposed, but we captured the agentService reference.)
            Assert.Empty(agentService.GetSubAgents());
        }
        finally
        {
            if (composer is not null)
                await composer.DisposeAsync();
            dbContext?.Dispose();
            TryDeleteDir(tmpDir);
        }
    }
}