using CopilotHive.Configuration;
using CopilotHive.Orchestration;
using CopilotHive.Git;
using CopilotHive.Security;
using CopilotHive.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// ROUND-2 enforcement: every chat client <see cref="ComposerAgentService"/> creates — primary, compaction and every
/// sub-agent model client — passes the identity-bound provider gate for ITS OWN model, BEFORE the chat-client factory
/// (and so any token probe / network call) runs. Tests use counting fake factories, never timing.
/// </summary>
public sealed class ComposerProviderClientGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-provider-gate-{Guid.NewGuid():N}");
    private readonly List<string> _created = [];
    private readonly List<ComposerAgentService> _services = [];

    public ComposerProviderClientGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var s in _services) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        TestHelpers.ForceDeleteDirectory(_dir);
    }

    private static string ProviderOf(string model) => model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local";

    private static readonly ProviderIdentity NonOriginal = ProviderIdentity.ForAccount(true, true, isOriginal: false);
    private static readonly ProviderIdentity Original = ProviderIdentity.ForAccount(true, true, isOriginal: true);

    /// <summary>A gate over a MUTABLE identity so tests can change standing between the selection-level and the per-client check.</summary>
    private sealed class MutableGate(ProviderIdentity? initial, Func<string, string>? providerOf = null)
    {
        public volatile ProviderIdentity? Identity = initial;
        public int Evaluations;

        public ProviderGate Gate => (primary, compaction, _) =>
        {
            Interlocked.Increment(ref Evaluations);
            return Task.FromResult(ProviderEligibility.Evaluate(true, Identity, primary, compaction, providerOf ?? ProviderOf, null));
        };
    }

    private ComposerAgentService Create(
        ProviderGate? gate, string model = "ollama-local/main", string? compaction = null, bool subAgents = false,
        string[]? models = null)
    {
        var catalog = models ?? ["ollama-local/main", "ollama-local/other", "copilot/main"];
        var repo = new Mock<IBrainRepoManager>();
        repo.SetupGet(r => r.WorkDirectory).Returns(_dir);
        var service = new ComposerAgentService(
            model, 64000, 50, null, null, "system prompt", new List<AITool>(), subAgents ? repo.Object : null, _dir,
            compaction, NullLogger<ComposerAgentService>.Instance,
            id => { lock (_created) _created.Add(id); return new Mock<IChatClient>().Object; },
            null, catalog, null, null, subAgents,
            subAgents ? catalog.Select(n => new ModelEntry { Name = n }).ToList() : [],
            providerGate: gate);
        _services.Add(service);
        return service;
    }

    private string[] Created() { lock (_created) return _created.ToArray(); }

    [Fact]
    public async Task SubAgentClientFactory_NonOriginal_DeniesCopilotAtCreation_AndAllowsOllama()
    {
        var gate = new MutableGate(NonOriginal);
        var service = Create(gate.Gate, subAgents: true);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        var before = Created().Length;
        var subAgents = service.AgentOptions.SubAgents!;

        var denied = Assert.Throws<ProviderNotEligibleException>(() => subAgents.ClientFactory!("copilot/main"));

        Assert.Equal(ProviderDenyReason.NonOriginalCopilot, denied.Reason);
        Assert.Equal(before, Created().Length); // denied BEFORE the chat-client factory
        subAgents.ClientFactory!("ollama-local/other");
        Assert.Equal(["ollama-local/other"], Created().Skip(before).ToArray());
    }

    [Fact]
    public async Task SubAgentClientFactory_Original_IsAllowedCopilot_RegardlessOfRole()
    {
        var gate = new MutableGate(Original);
        var service = Create(gate.Gate, model: "copilot/main", subAgents: true);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        service.AgentOptions.SubAgents!.ClientFactory!("copilot/main");

        Assert.Equal(["copilot/main", "copilot/main"], Created());
    }

    [Fact]
    public async Task PerClientFreshness_AccountDisabledAfterSelection_DeniesTheNextSubAgentClient_WithoutCreation()
    {
        var gate = new MutableGate(NonOriginal);
        var service = Create(gate.Gate, subAgents: true);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        var subAgents = service.AgentOptions.SubAgents!;
        var before = Created().Length;
        var evaluationsBefore = Volatile.Read(ref gate.Evaluations);

        // The account is disabled AFTER the runtime was connected: the very next client creation re-reads it.
        gate.Identity = ProviderIdentity.ForAccount(enabled: false, roleDefined: true, isOriginal: false);
        var denied = Assert.Throws<ProviderNotEligibleException>(() => subAgents.ClientFactory!("ollama-local/other"));

        Assert.Equal(ProviderDenyReason.DisabledAccount, denied.Reason);
        Assert.Equal(before, Created().Length);
        Assert.True(Volatile.Read(ref gate.Evaluations) > evaluationsBefore); // the gate was consulted for THIS client

        // Deleted account (unknown identity) is denied the same way.
        gate.Identity = ProviderIdentity.Unknown;
        Assert.Equal(ProviderDenyReason.UnknownAccount,
            Assert.Throws<ProviderNotEligibleException>(() => subAgents.ClientFactory!("ollama-local/other")).Reason);
        Assert.Equal(before, Created().Length);
    }

    [Fact]
    public async Task PerClientGate_StaleInFlightConnect_DeniedAtClientCreation_CreatesNoClient()
    {
        // The selection-level gate passes, then the account is disabled before the PRIMARY client is created.
        var identity = new MutableGate(NonOriginal);
        var calls = 0;
        ProviderGate gate = (p, c, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2) identity.Identity = ProviderIdentity.Unknown;
            return identity.Gate(p, c, ct);
        };
        var service = Create(gate);

        var ex = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => service.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ProviderDenyReason.UnknownAccount, ex.Reason);
        Assert.Empty(Created());
        Assert.False(service.IsConnected);
    }

    [Theory]
    [InlineData("copilot/compact")]
    public async Task Connect_NonOriginal_ExplicitCopilotCompaction_FailsClosed_BeforeAnyClient(string compaction)
    {
        var service = Create(new MutableGate(NonOriginal).Gate, compaction: compaction);

        await Assert.ThrowsAsync<ProviderNotEligibleException>(() => service.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Empty(Created());
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task Connect_NonOriginal_WhitespaceCompaction_IsClassifiedAsGiven_FailsClosedWhenUnclassifiable()
    {
        // Whitespace is NOT "unset": it is handed verbatim to client creation, so it is classified as given. A resolver
        // that cannot classify it fails CLOSED for a non-original identity (no client of either kind is created).
        var gate = new MutableGate(NonOriginal, m => m.Trim().Length == 0 ? throw new FormatException("blank") : ProviderOf(m));
        var service = Create(gate.Gate, compaction: "   ");

        var ex = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => service.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ProviderDenyReason.UnresolvableProvider, ex.Reason);
        Assert.Empty(Created());
    }

    [Fact]
    public async Task Connect_NonOriginal_NullCompaction_InheritsThePrimary_OllamaConnects_CopilotDenied()
    {
        var ollama = Create(new MutableGate(NonOriginal).Gate, compaction: null);
        await ollama.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.True(ollama.IsConnected);
        Assert.Equal(["ollama-local/main"], Created()); // no separate compaction client: it inherits the primary

        var copilot = Create(new MutableGate(NonOriginal).Gate, model: "copilot/main", compaction: null);
        await Assert.ThrowsAsync<ProviderNotEligibleException>(() => copilot.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["ollama-local/main"], Created());
    }

    [Fact]
    public async Task Switch_Denied_LeavesModelConnectionAndClientsCompletelyUntouched()
    {
        var gate = new MutableGate(NonOriginal);
        var service = Create(gate.Gate);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        var agentBefore = service.Agent;
        var clientBefore = service.ChatClient;
        var createdBefore = Created();

        var ex = await Assert.ThrowsAsync<ProviderNotEligibleException>(() =>
            service.SwitchModelAsync("copilot/main", ReasoningEffort.Medium, TestContext.Current.CancellationToken));

        Assert.Equal(ProviderDenyReason.NonOriginalCopilot, ex.Reason);
        Assert.Equal("ollama-local/main", service.Model);          // nothing committed
        Assert.True(service.IsConnected);                            // no teardown-before-denial
        Assert.Same(agentBefore, service.Agent);
        Assert.Same(clientBefore, service.ChatClient);
        Assert.Equal(createdBefore, Created());                      // no client created
    }

    [Fact]
    public async Task Switch_ToEligibleOllama_StillWorks_AndCreatesPrimaryClientThroughTheGate()
    {
        var gate = new MutableGate(NonOriginal);
        var service = Create(gate.Gate);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        await service.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        Assert.Equal("ollama-local/other", service.Model);
        Assert.Equal(["ollama-local/main", "ollama-local/other"], Created());
    }

    [Fact]
    public async Task FirstSelection_Denied_CommitsNothing_AndEligibleSelectionConnects()
    {
        var gate = new MutableGate(NonOriginal);
        var service = Create(gate.Gate, model: " "); // model-less shell
        Assert.Null(service.Model);
        var published = 0;

        await Assert.ThrowsAsync<ProviderNotEligibleException>(() => service.ConnectFirstSelectionAsync(
            "copilot/main", ReasoningEffort.Medium, () => published++, TestContext.Current.CancellationToken));
        Assert.Null(service.Model);
        Assert.Equal(0, published);
        Assert.Empty(Created());
        Assert.False(service.IsConnected);

        await service.ConnectFirstSelectionAsync(
            "ollama-local/other", ReasoningEffort.Medium, () => published++, TestContext.Current.CancellationToken);
        Assert.Equal(1, published);
        Assert.True(service.IsConnected);
        Assert.Equal(["ollama-local/other"], Created());
    }

    [Fact]
    public async Task OpenMode_And_LegacyService_HaveNoGate_AndAreUnchanged()
    {
        var service = Create(gate: null, model: "copilot/main", compaction: "copilot/compact", subAgents: true);

        await service.ConnectAsync(TestContext.Current.CancellationToken);
        service.AgentOptions.SubAgents!.ClientFactory!("copilot/main");

        Assert.True(service.IsConnected);
        Assert.Equal(["copilot/main", "copilot/compact", "copilot/main"], Created());
    }

    [Fact]
    public async Task OperationGate_UngatedService_AddsNoEvaluationAndNoDenial()
    {
        var service = Create(gate: null, model: "copilot/main", compaction: "copilot/compact");
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        await service.EnforceOperationGateAsync(TestContext.Current.CancellationToken); // legacy / open mode: no-op

        Assert.Equal(["copilot/main", "copilot/compact"], Created());
    }

    [Fact]
    public async Task OperationGate_ConsultsTheGateWithTheActualPrimaryAndExplicitCompaction_AndCreatesNoClient()
    {
        var seen = new List<(string Primary, string? Compaction)>();
        var identity = new MutableGate(NonOriginal);
        ProviderGate gate = (p, c, ct) => { lock (seen) seen.Add((p, c)); return identity.Gate(p, c, ct); };
        var service = Create(gate, compaction: "ollama-local/compact");
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        var created = Created().Length;
        lock (seen) seen.Clear();

        await service.EnforceOperationGateAsync(TestContext.Current.CancellationToken);
        Assert.Equal([("ollama-local/main", (string?)"ollama-local/compact")], seen);
        Assert.Equal(created, Created().Length);

        identity.Identity = ProviderIdentity.ForAccount(enabled: false, roleDefined: true, isOriginal: false);
        var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(
            () => service.EnforceOperationGateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProviderDenyReason.DisabledAccount, denied.Reason);
        Assert.Equal(created, Created().Length);
        Assert.True(service.IsConnected); // the denial tears nothing down
    }

    [Fact]
    public void ChatClientFactory_IsReachedOnlyThroughTheGatedCreationMethods()
    {
        var source = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration", "ComposerAgentService.cs"))
            .ReplaceLineEndings("\n");

        Assert.Equal(1, Count(source, "(_chatClientFactory ?? ChatClientFactory.Create)("));
        Assert.Equal(1, Count(source, "CreateRawClient(modelId);\n    }\n\n    /// <summary>\n    /// Synchronous"));
        // No code path may call the raw creator outside the two gated methods.
        Assert.Equal(2, Count(source, "return CreateRawClient(modelId);"));
        Assert.Equal(0, Count(source, "= CreateClient(model)"));
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; (i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0; i += needle.Length) n++;
        return n;
    }
}

public sealed partial class ComposerRuntimeManagerTests
{
    private async Task<(ComposerUserRuntime Runtime, ComposerRuntimeManager Manager, ActingUserPrincipal Invited)> ConnectedOllamaNonOriginalAsync(
        string primary = "ollama-local/main")
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = true;
        CountTokenProbes();
        var manager = NewManager(BuildProvider(true, new HiveConfigFile
        {
            // The catalog also lists a Copilot model, so a denied switch is denied by the GATE, not by catalog validation.
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = primary }, new ModelEntry { Name = "ollama-local/other" }, new ModelEntry { Name = "copilot/main" }] },
            Composer = new ComposerConfig { Model = primary },
        }));
        var created = await Request(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Connected, created.Status);
        return (created.Runtime!, manager, invited);
    }

    [Fact]
    public async Task NonOriginal_ConnectedOllama_PrimarySwitchToCopilot_IsDenied_NothingCommitted_NoProbeOrClient()
    {
        var (runtime, _, _) = await ConnectedOllamaNonOriginalAsync();
        var clientsBefore = _clientCreations;

        await Assert.ThrowsAsync<ProviderNotEligibleException>(() =>
            runtime.Composer.SwitchModelAsync("copilot/main", Microsoft.Extensions.AI.ReasoningEffort.Medium, Ct));

        Assert.True(runtime.Composer.IsConnected);
        Assert.Equal("ollama-local/main", runtime.Composer.CurrentModel);
        Assert.Equal(clientsBefore, _clientCreations);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonOriginal_DisabledOrDeletedAfterConstruction_DeniesTheNextClientCreation_WithoutProbe(bool delete)
    {
        var (runtime, _, invited) = await ConnectedOllamaNonOriginalAsync();
        var clientsBefore = _clientCreations;
        if (delete)
        {
            await using var db = _factory.CreateDbContext();
            Assert.Equal(1, await db.Users.Where(u => u.Id == invited.UserId).ExecuteDeleteAsync(Ct));
        }
        else await _users.SetUserEnabledAsync(invited.UserId, false, Ct);

        // An ELIGIBLE (Ollama) selection is still denied: the freshly read account is no longer eligible at all.
        var ex = await Assert.ThrowsAsync<ProviderNotEligibleException>(() =>
            runtime.Composer.SwitchModelAsync("ollama-local/other", Microsoft.Extensions.AI.ReasoningEffort.Medium, Ct));

        Assert.Equal(delete ? ProviderDenyReason.UnknownAccount : ProviderDenyReason.DisabledAccount, ex.Reason);
        Assert.Equal("ollama-local/main", runtime.Composer.CurrentModel);
        Assert.Equal(clientsBefore, _clientCreations);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
    }

    [Fact]
    public async Task NonOriginal_DeferredRuntime_EligibleActorFirstSelectionConnects_ButManagerProbingNeverBypassesAnExplicitCopilotCompaction()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = true;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("copilot/main", "copilot/compact"));
        await using var gate = Park();
        gate.Release();
        var deferred = await Request(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        var composer = deferred.Runtime!.Composer;

        // Actor first-selection to an eligible primary is STILL blocked by the explicit Copilot compaction model.
        await Assert.ThrowsAsync<ProviderNotEligibleException>(() =>
            composer.SwitchModelAsync("ollama-local/other", Microsoft.Extensions.AI.ReasoningEffort.Medium, Ct));
        Assert.Equal("copilot/main", composer.CurrentModel);
        Assert.False(composer.IsConnected);

        // Manager probing: still Deferred, never a connect, even with the original token available.
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Retry(manager, invited)).Status);
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, invited)).Status);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, _clientCreations);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
    }
}

public sealed partial class ComposerRuntimeManagerTests
{
    [Theory]
    [InlineData(ProviderMatrixPath.Send)]
    [InlineData(ProviderMatrixPath.CompactFull)]
    [InlineData(ProviderMatrixPath.CompactPartial)]
    public async Task OperationDenial_ForADisabledAccount_LeavesTheRuntimeIntact_AndTheOperationWorksAgainOnceReEnabled(ProviderMatrixPath path)
    {
        // Two admins exist (TwoUsersAsync(Admin)), so the invited account may be disabled and re-enabled.
        var context = await ProviderMatrixAsync(role: UserRole.Admin);
        var composer = context.Composer;
        var agent = context.Service.Agent;
        var chatClient = context.Service.ChatClient;
        await _users.SetUserEnabledAsync(context.Principal.UserId, false, Ct);
        var before = MatrixCounts(context);

        var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => MatrixRunPathAsync(context, path, "ollama-local/other"));

        Assert.Equal(ProviderDenyReason.DisabledAccount, denied.Reason);
        AssertMatrixNoProviderWork(context, before);
        // The runtime is intact: no teardown, no disposal, still connected, still streaming-idle.
        Assert.True(composer.IsConnected);
        Assert.False(composer.IsStreaming);
        Assert.Same(agent, context.Service.Agent);
        Assert.Same(chatClient, context.Service.ChatClient);
        Assert.Equal("ollama-local/main", composer.CurrentModel);
        Assert.False(composer.IsCompacting);

        await _users.SetUserEnabledAsync(context.Principal.UserId, true, Ct);
        await MatrixRunPathAsync(context, path, "ollama-local/other"); // viable successful outcome again
        Assert.Equal(before.Requests + 1, context.Telemetry.Requests);
        Assert.True(composer.IsConnected);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
    }

    [Fact]
    public async Task NotConnectedSend_StaysASafeRefusal_WithNoDirectoryRead_AfterTheOperationGateWasAdded()
    {
        var context = await ProviderMatrixAsync(primary: "copilot/main"); // non-original Copilot => deferred, not connected
        var queries = _factory.CredentialReads.Queries.Length;
        var before = MatrixCounts(context);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Composer.SendMessageWithEventsAsync("x"));
        var compactRefused = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Composer.CompactSessionAsync(Ct));

        Assert.Equal("Composer not connected.", refused.Message);
        Assert.IsNotType<ProviderNotEligibleException>(refused);
        Assert.IsNotType<ProviderNotEligibleException>(compactRefused);
        Assert.Equal(queries, _factory.CredentialReads.Queries.Length);
        AssertMatrixNoProviderWork(context, before);
    }
}
