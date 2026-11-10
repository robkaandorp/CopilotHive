using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using SharpCoder.Providers;

namespace CopilotHive.Tests.Orchestration;

public sealed partial class ComposerRuntimeManagerTests
{
    public enum ProviderMatrixPath { Send, SubAgent, Switch, Direct, DirectAsync, CompactFull, CompactPartial }

    public static IEnumerable<object[]> ProviderMatrixRoles =>
        Enum.GetValues<UserRole>().Select(role => new object[] { role });

    public static IEnumerable<object[]> ProviderMatrixRolePaths =>
        from role in Enum.GetValues<UserRole>()
        from path in Enum.GetValues<ProviderMatrixPath>()
        select new object[] { role, path };

    public static IEnumerable<object[]> ProviderMatrixClientRolePaths =>
        ProviderMatrixRolePaths.Where(row => (ProviderMatrixPath)row[1] is
            ProviderMatrixPath.SubAgent or ProviderMatrixPath.Switch or ProviderMatrixPath.Direct or ProviderMatrixPath.DirectAsync);

    public static IEnumerable<object[]> ProviderMatrixRoleCompactionPaths =>
        ProviderMatrixRolePaths.Where(row => (ProviderMatrixPath)row[1] is ProviderMatrixPath.CompactFull or ProviderMatrixPath.CompactPartial);

    public static IEnumerable<object[]> ProviderMatrixInvalidations =>
        from original in new[] { false, true }
        from delete in new[] { false, true }
        from path in Enum.GetValues<ProviderMatrixPath>()
        select new object[] { original, delete, path };

    public static IEnumerable<object[]> ProviderMatrixExplicitCompactionFreshness =>
        from original in new[] { false, true }
        from compaction in original ? new[] { "copilot/compact", "   " } : new[] { "ollama-local/compact" }
        from delete in new[] { false, true }
        from path in new[] { ProviderMatrixPath.CompactFull, ProviderMatrixPath.CompactPartial }
        select new object[] { original, delete, path, compaction };

    public static IEnumerable<object?[]> ProviderMatrixCompactions =>
        from role in Enum.GetValues<UserRole>()
        from selection in new (string Primary, string? Compaction)[]
        {
            ("copilot/main", null), // inherited effective compaction
            ("copilot/main", ""),
            ("copilot/main", "ollama-local/compact"),
            ("ollama-local/main", "copilot/compact"),
            ("ollama-local/main", "   "), // real parser defaults blank to Copilot
            ("ollama-local/main", "fallback-model"), // unprefixed provider defaults to Copilot
        }
        select new object?[] { role, selection.Primary, selection.Compaction };

    public static IEnumerable<object?[]> ProviderMatrixDeferredCompactionOperations =>
        from row in ProviderMatrixCompactions
        from path in new[] { ProviderMatrixPath.CompactFull, ProviderMatrixPath.CompactPartial }
        select row.Concat(new object?[] { path }).ToArray();

    private sealed class MatrixTelemetry
    {
        public int Requests;
        public int Connects;
        public ConcurrentQueue<string> CreatedModels { get; } = new();
        public ConcurrentQueue<string> InvokedModels { get; } = new();
        public ConcurrentQueue<string> DisposedModels { get; } = new();
    }

    private sealed class MatrixChatClient(string model, MatrixTelemetry telemetry) : IChatClient
    {
        public ChatClientMetadata Metadata => new("provider-matrix", null, model);
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() => telemetry.DisposedModels.Enqueue(model);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref telemetry.Requests);
            telemetry.InvokedModels.Enqueue(model);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "matrix-summary")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref telemetry.Requests);
            telemetry.InvokedModels.Enqueue(model);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "matrix-reply") { FinishReason = ChatFinishReason.Stop };
        }
    }

    private sealed class MatrixProvider(IServiceProvider inner, IBrainRepoManager repo) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(IBrainRepoManager) ? repo : inner.GetService(type);
    }

    private sealed record ProviderMatrixContext(ActingUserPrincipal Principal, ComposerRuntimeManager Manager,
        ComposerUserRuntime Runtime, ComposerFactory Factory, HiveConfigFile Config, MatrixTelemetry Telemetry,
        IServiceProvider Provider, Composer? LegacyOverride = null)
    {
        public Composer Composer => LegacyOverride ?? Runtime.Composer;
        public ComposerAgentService Service => Field<ComposerAgentService>(Composer, "_agentService");
    }

    private async Task<ProviderMatrixContext> ProviderMatrixAsync(bool original = false, UserRole role = UserRole.Member,
        string? primary = "ollama-local/main", string? compaction = null, bool auth = true)
    {
        var (first, second) = await TwoUsersAsync(original ? UserRole.Admin : role);
        var principal = original ? first : second;
        if (original && role != UserRole.Admin)
        {
            await _users.ChangeUserRoleAsync(first.UserId, role, Ct);
            principal = ActingUserPrincipal.ForActivatedAccount(first.UserId, first.GitHubId, role, true, true);
        }
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = new[] { "copilot/main", "copilot/compact", "ollama-local/main", "ollama-local/other", "ollama-local/compact" }
                    .Select(name => new ModelEntry { Name = name }).ToList(),
                CompactionModel = compaction,
            },
            Composer = new ComposerConfig { Model = primary },
        };
        var telemetry = new MatrixTelemetry();
        _providerResolver = model => ChatClientFactory.ParseProviderAndModel(model).Item1;
        _tokenAvailable = true;
        CountTokenProbes();
        _clientFactory = model =>
        {
            telemetry.CreatedModels.Enqueue(model);
            return new MatrixChatClient(model, telemetry);
        };
        _connectHook = async (composer, ct) =>
        {
            Interlocked.Increment(ref telemetry.Connects);
            await composer.ConnectAsync(ct);
        };
        var repo = new Mock<IBrainRepoManager>();
        repo.SetupGet(r => r.WorkDirectory).Returns(StateDir);
        var provider = new MatrixProvider(BuildProvider(auth, config), repo.Object);
        var factory = NewFactory(provider);
        var manager = NewManager(provider, factory: factory);
        if (!original) _factory.CredentialReads.Start(first.UserId); // includes real construction and every later path
        var created = await Request(manager, principal);
        Assert.True(created.IsGranted);
        Assert.Null(created.Failure);
        return new ProviderMatrixContext(principal, manager, created.Runtime!, factory, config, telemetry, provider);
    }

    private (int Clients, int Probes, int Requests, int Connects, int Queries, int Disposals) MatrixCounts(ProviderMatrixContext context) =>
        (_clientCreations, Volatile.Read(ref _tokenProbes), Volatile.Read(ref context.Telemetry.Requests),
            Volatile.Read(ref context.Telemetry.Connects), _factory.CredentialReads.Queries.Length, context.Telemetry.DisposedModels.Count);

    private void AssertMatrixNoProviderWork(ProviderMatrixContext context,
        (int Clients, int Probes, int Requests, int Connects, int Queries, int Disposals) before)
    {
        Assert.Equal(before.Clients, _clientCreations);
        Assert.Equal(before.Probes, Volatile.Read(ref _tokenProbes));
        Assert.True(before.Requests == Volatile.Read(ref context.Telemetry.Requests),
            $"Denied operation invoked a cached model: expected {before.Requests} request(s), actual {context.Telemetry.Requests}; "
            + $"models=[{string.Join(", ", context.Telemetry.InvokedModels)}]");
        Assert.Equal(before.Connects, Volatile.Read(ref context.Telemetry.Connects));
        Assert.Equal(before.Disposals, context.Telemetry.DisposedModels.Count);
    }

    private static IChatClient MatrixDirectClient(ComposerAgentService service, string model)
    {
        try
        {
            return (IChatClient)typeof(ComposerAgentService).GetMethod("CreateClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [model])!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static async Task<IChatClient> MatrixDirectAsyncClient(ComposerAgentService service, string model)
    {
        var task = (Task<IChatClient>)typeof(ComposerAgentService)
            .GetMethod("CreateClientAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [model, Ct])!;
        return await task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    private static void MatrixSeedCompaction(ComposerAgentService service)
    {
        service.Session.MessageHistory.Clear();
        service.Session.MessageHistory.Add(new ChatMessage(ChatRole.System, "matrix system"));
        for (var i = 0; i < 30; i++)
            service.Session.MessageHistory.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"matrix-history-{i} " + new string('x', 100)));
    }

    private static async Task MatrixSendAndDrainAsync(Composer composer)
    {
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Updated() { if (!composer.IsStreaming) terminal.TrySetResult(); }
        composer.OnStreamingUpdate += Updated;
        try
        {
            var send = composer.SendMessageWithEventsAsync("matrix-question"); // retain before any assertions
            await GuardAsync(send, "matrix send admission");
            await GuardAsync(terminal.Task, "matrix stream terminal callback");
            Assert.Contains("matrix-reply", composer.StreamingContent);
        }
        finally { composer.OnStreamingUpdate -= Updated; }
    }

    private static async Task MatrixRunPathAsync(ProviderMatrixContext context, ProviderMatrixPath path, string target)
    {
        switch (path)
        {
            case ProviderMatrixPath.Send:
                await MatrixSendAndDrainAsync(context.Composer);
                break;
            case ProviderMatrixPath.SubAgent:
                using (context.Service.AgentOptions.SubAgents!.ClientFactory!(target)) { }
                break;
            case ProviderMatrixPath.Switch:
                await context.Composer.SwitchModelAsync(target, ReasoningEffort.Medium, Ct);
                break;
            case ProviderMatrixPath.Direct:
                using (MatrixDirectClient(context.Service, target)) { }
                break;
            case ProviderMatrixPath.DirectAsync:
                using (await MatrixDirectAsyncClient(context.Service, target)) { }
                break;
            case ProviderMatrixPath.CompactFull:
                MatrixSeedCompaction(context.Service);
                Assert.True(await context.Composer.CompactSessionAsync(Ct));
                break;
            case ProviderMatrixPath.CompactPartial:
                MatrixSeedCompaction(context.Service);
                Assert.True(await context.Composer.CompactOldestPercentAsync(50, Ct));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(path));
        }
    }

    private void AssertMatrixAllowedModels(ProviderMatrixContext context, ProviderMatrixPath path, string target,
        (int Clients, int Probes, int Requests, int Connects, int Queries, int Disposals) before)
    {
        var explicitCompaction = context.Runtime.Configuration.CompactionModel;
        var hasCompaction = !string.IsNullOrEmpty(explicitCompaction);
        var expectedCreated = path == ProviderMatrixPath.Switch
            ? hasCompaction ? new[] { target, explicitCompaction! } : new[] { target }
            : path is ProviderMatrixPath.SubAgent or ProviderMatrixPath.Direct or ProviderMatrixPath.DirectAsync
                ? new[] { target } : Array.Empty<string>();
        Assert.Equal(expectedCreated, context.Telemetry.CreatedModels.Skip(before.Clients).ToArray());
        var expectedInvoked = path == ProviderMatrixPath.Send ? new[] { context.Service.Model! }
            : path is ProviderMatrixPath.CompactFull or ProviderMatrixPath.CompactPartial
                ? new[] { hasCompaction ? explicitCompaction! : context.Service.Model! } : Array.Empty<string>();
        Assert.Equal(expectedInvoked, context.Telemetry.InvokedModels.Skip(before.Requests).ToArray());
        Assert.Equal(before.Connects, context.Telemetry.Connects);
        Assert.Equal(before.Disposals + (path == ProviderMatrixPath.Switch ? hasCompaction ? 2 : 1
            : path is ProviderMatrixPath.SubAgent or ProviderMatrixPath.Direct or ProviderMatrixPath.DirectAsync ? 1 : 0), context.Telemetry.DisposedModels.Count);
    }

    [Theory]
    [MemberData(nameof(ProviderMatrixClientRolePaths))]
    public async Task Matrix_NonOriginalCopilot_NewClientPaths_DenyEveryRoleBeforeProviderWork(UserRole role, ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(role: role);
        var before = MatrixCounts(context);
        var client = context.Service.ChatClient;
        var agent = context.Service.Agent;
        var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => MatrixRunPathAsync(context, path, "copilot/main"));
        Assert.Equal(ProviderDenyReason.NonOriginalCopilot, denied.Reason);
        AssertMatrixNoProviderWork(context, before);
        Assert.Equal("ollama-local/main", context.Composer.CurrentModel);
        Assert.Same(client, context.Service.ChatClient);
        Assert.Same(agent, context.Service.Agent);
        Assert.True(context.Composer.IsConnected);
        Assert.True(_factory.CredentialReads.Queries.Length > before.Queries); // actual decision, not a copied gate
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [MemberData(nameof(ProviderMatrixRoles))]
    public async Task Matrix_NonOriginalCopilot_DeferredSend_SafelyRefusesEveryRoleWithoutDirectoryOrProviderUse(UserRole role)
    {
        var context = await ProviderMatrixAsync(role: role, primary: "copilot/main");
        var before = MatrixCounts(context);
        foreach (var available in new[] { false, true })
        {
            _tokenAvailable = available;
            var queriesBeforeSend = _factory.CredentialReads.Queries.Length;
            var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Composer.SendMessageWithEventsAsync("denied"));
            Assert.Equal("Composer not connected.", denied.Message);
            Assert.Equal(queriesBeforeSend, _factory.CredentialReads.Queries.Length); // safe pre-admission refusal, not gate activity
            Assert.Equal(ComposerRuntimeStatus.Deferred, (await Retry(context.Manager, context.Principal)).Status);
            AssertMatrixNoProviderWork(context, before);
        }
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [MemberData(nameof(ProviderMatrixCompactions))]
    public async Task Matrix_NonOriginalCompaction_InheritedWhitespaceAndFallback_DeniedForEveryRoleBeforeAnyClient(
        UserRole role, string primary, string? compaction)
    {
        var context = await ProviderMatrixAsync(role: role, primary: primary, compaction: compaction);
        Assert.False(context.Composer.IsConnected);
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Retry(context.Manager, context.Principal)).Status);
        Assert.Equal(0, _clientCreations);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(0, context.Telemetry.Requests);
        Assert.Equal(0, context.Telemetry.Connects);
        var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => context.Composer.ConnectAsync(Ct));
        Assert.Equal(ProviderDenyReason.NonOriginalCopilot, denied.Reason);
        Assert.Equal(0, _clientCreations);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(0, context.Telemetry.Requests);
        Assert.Equal(0, context.Telemetry.Connects);
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [MemberData(nameof(ProviderMatrixRolePaths))]
    public async Task Matrix_OriginalCopilot_NewPaths_AllowedForEveryCurrentRole(UserRole role, ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(original: true, role: role, primary: "copilot/main", compaction: "copilot/compact");
        var before = MatrixCounts(context);
        await MatrixRunPathAsync(context, path, "copilot/main");
        AssertMatrixAllowedModels(context, path, "copilot/main", before);
        Assert.True(context.Composer.IsConnected);
        Assert.Equal(before.Probes, Volatile.Read(ref _tokenProbes));
        Assert.Equal(before.Connects, context.Telemetry.Connects);
        Assert.Equal(before.Clients + (path == ProviderMatrixPath.Switch ? 2 : path is ProviderMatrixPath.SubAgent or ProviderMatrixPath.Direct or ProviderMatrixPath.DirectAsync ? 1 : 0), _clientCreations);
        Assert.Equal(before.Requests + (path is ProviderMatrixPath.Send or ProviderMatrixPath.CompactFull or ProviderMatrixPath.CompactPartial ? 1 : 0), context.Telemetry.Requests);
    }

    [Theory]
    [MemberData(nameof(ProviderMatrixRolePaths))]
    public async Task Matrix_NonOriginalOllama_EveryEligiblePath_IsUsableAndNeverReadsOriginalCredentials(UserRole role, ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(role: role);
        var before = MatrixCounts(context);
        await MatrixRunPathAsync(context, path, "ollama-local/other");
        AssertMatrixAllowedModels(context, path, "ollama-local/other", before);
        Assert.True(context.Composer.IsConnected);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(before.Clients + (path is ProviderMatrixPath.SubAgent or ProviderMatrixPath.Switch or ProviderMatrixPath.Direct or ProviderMatrixPath.DirectAsync ? 1 : 0), _clientCreations);
        Assert.Equal(before.Requests + (path is ProviderMatrixPath.Send or ProviderMatrixPath.CompactFull or ProviderMatrixPath.CompactPartial ? 1 : 0), context.Telemetry.Requests);
        if (path is not (ProviderMatrixPath.Send or ProviderMatrixPath.CompactFull or ProviderMatrixPath.CompactPartial))
            Assert.True(_factory.CredentialReads.Queries.Length > before.Queries);
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [MemberData(nameof(ProviderMatrixInvalidations))]
    public async Task Matrix_Freshness_DisabledOrDeletedAccount_DeniesNextOperationOrClient(
        bool original, bool delete, ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(original: original, primary: original ? "copilot/main" : "ollama-local/main");
        if (!original) AssertNoOriginalCredentialReads(); // construction is observed separately from setup mutation
        if (delete) await DeleteStoredAccountAsync(context.Principal.UserId);
        else await _users.SetUserEnabledAsync(context.Principal.UserId, false, Ct);
        var before = MatrixCounts(context);
        var materializationsBefore = _factory.CredentialReads.OriginalAccountMaterializations;
        var denied = await Record.ExceptionAsync(() => MatrixRunPathAsync(context, path, "ollama-local/other"));
        AssertMatrixNoProviderWork(context, before); // the cached-client send/compaction regression boundary
        var typed = Assert.IsType<ProviderNotEligibleException>(denied);
        Assert.Equal(delete ? ProviderDenyReason.UnknownAccount : ProviderDenyReason.DisabledAccount, typed.Reason);
        if (!original)
        {
            Assert.DoesNotContain(_factory.CredentialReads.Queries.Skip(before.Queries), SelectsCredentialColumns);
            Assert.Equal(materializationsBefore, _factory.CredentialReads.OriginalAccountMaterializations);
            Assert.True(_factory.CredentialReads.Queries.Length > before.Queries);
        }
    }

    [Theory]
    [MemberData(nameof(ProviderMatrixExplicitCompactionFreshness))]
    public async Task Matrix_Freshness_CachedExplicitOrWhitespaceCompaction_DeniesNextModelUse(
        bool original, bool delete, ProviderMatrixPath path, string compaction)
    {
        var context = await ProviderMatrixAsync(original: original, primary: original ? "copilot/main" : "ollama-local/main", compaction: compaction);
        Assert.NotNull(context.Service.AgentOptions.CompactionClient); // this cell uses the EXPLICIT cached client
        if (!original) AssertNoOriginalCredentialReads();
        if (delete) await DeleteStoredAccountAsync(context.Principal.UserId);
        else await _users.SetUserEnabledAsync(context.Principal.UserId, false, Ct);
        var before = MatrixCounts(context);
        var materializationsBefore = _factory.CredentialReads.OriginalAccountMaterializations;
        var error = await Record.ExceptionAsync(() => MatrixRunPathAsync(context, path, "ollama-local/other"));
        AssertMatrixNoProviderWork(context, before);
        Assert.Equal(delete ? ProviderDenyReason.UnknownAccount : ProviderDenyReason.DisabledAccount,
            Assert.IsType<ProviderNotEligibleException>(error).Reason);
        if (!original)
        {
            Assert.DoesNotContain(_factory.CredentialReads.Queries.Skip(before.Queries), SelectsCredentialColumns);
            Assert.Equal(materializationsBefore, _factory.CredentialReads.OriginalAccountMaterializations);
        }
    }

    [Theory]
    [InlineData(ProviderMatrixPath.SubAgent)]
    [InlineData(ProviderMatrixPath.Switch)]
    [InlineData(ProviderMatrixPath.Direct)]
    [InlineData(ProviderMatrixPath.DirectAsync)]
    public async Task Matrix_OriginalTokenArrival_NeverRecoversNonOriginalNewPathDenial(ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync();
        var before = MatrixCounts(context);
        foreach (var available in new[] { false, true })
        {
            _tokenAvailable = available;
            var queriesBefore = _factory.CredentialReads.Queries.Length;
            var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => MatrixRunPathAsync(context, path, "copilot/main"));
            Assert.Equal(ProviderDenyReason.NonOriginalCopilot, denied.Reason);
            AssertMatrixNoProviderWork(context, before);
            Assert.True(_factory.CredentialReads.Queries.Length > queriesBefore);
        }
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [InlineData(ProviderMatrixPath.Send)]
    [InlineData(ProviderMatrixPath.SubAgent)]
    [InlineData(ProviderMatrixPath.Switch)]
    [InlineData(ProviderMatrixPath.Direct)]
    [InlineData(ProviderMatrixPath.DirectAsync)]
    [InlineData(ProviderMatrixPath.CompactFull)]
    [InlineData(ProviderMatrixPath.CompactPartial)]
    public async Task Matrix_OpenMode_CopilotNewPaths_PreserveUngatedBehavior(ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(primary: "copilot/main", compaction: "copilot/compact", auth: false);
        var before = MatrixCounts(context);
        await MatrixRunPathAsync(context, path, "copilot/main");
        AssertMatrixAllowedModels(context, path, "copilot/main", before);
        Assert.True(context.Composer.IsConnected);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(before.Requests + (path is ProviderMatrixPath.Send or ProviderMatrixPath.CompactFull or ProviderMatrixPath.CompactPartial ? 1 : 0), context.Telemetry.Requests);
    }

    [Fact]
    public async Task MatrixRecovery_CopilotPrimaryEligibleCompaction_StaysDeferredWithoutConnectOrProbe()
    {
        var context = await ProviderMatrixAsync(primary: "copilot/main", compaction: "ollama-local/compact");
        var before = MatrixCounts(context);
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Retry(context.Manager, context.Principal)).Status);
        AssertMatrixNoProviderWork(context, before);
        Assert.Equal(0, _clientCreations);
        AssertNoOriginalCredentialReads();
    }

    [Fact]
    public async Task MatrixRecovery_EligiblePrimaryCopilotCompaction_SwitchCommitsNothingAndNeverConnects()
    {
        var context = await ProviderMatrixAsync(compaction: "copilot/compact");
        var before = MatrixCounts(context);
        var client = context.Service.ChatClient;
        var agent = context.Service.Agent;
        var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => context.Composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.High, Ct));
        Assert.Equal(ProviderDenyReason.NonOriginalCopilot, denied.Reason);
        Assert.Equal("ollama-local/main", context.Composer.CurrentModel);
        Assert.Same(client, context.Service.ChatClient);
        Assert.Same(agent, context.Service.Agent);
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Retry(context.Manager, context.Principal)).Status);
        AssertMatrixNoProviderWork(context, before);
        Assert.Equal(0, _clientCreations);
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("copilot/main")]
    public async Task MatrixFirstSelection_ActorConnection_ReconcilesShellOrDeferredStatusAndFacade(string? initial)
    {
        var context = await ProviderMatrixAsync(primary: initial);
        Assert.Equal(initial is null ? ComposerRuntimeStatus.DisconnectedShell : ComposerRuntimeStatus.Deferred,
            (await Request(context.Manager, context.Principal)).Status);
        await context.Composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct);
        Assert.True(context.Composer.IsConnected);
        Assert.Equal("ollama-local/other", context.Composer.GetStats()!.Model);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Request(context.Manager, context.Principal)).Status);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(context.Manager, context.Principal)).Status);
        AssertNoOriginalCredentialReads(); // gate operations only; legacy facade resolver is a separately filed concern
        var facade = new ComposerFacade(context.Composer,
            (HivePrincipalResolver)context.Provider.GetService(typeof(HivePrincipalResolver))!,
            (HivePolicy)context.Provider.GetService(typeof(HivePolicy))!, NullLogger<ComposerFacade>.Instance);
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, context.Principal.GitHubId)], "matrix"));
        var view = await facade.GetCurrentModelAsync(caller, Ct);
        Assert.True(view.Success);
        Assert.Equal("ollama-local/other", view.Value!.Model);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Request(context.Manager, context.Principal)).Status);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(context.Manager, context.Principal)).Status);
        Assert.Equal(1, _clientCreations);
        Assert.Equal(0, context.Telemetry.Connects); // actor path, never manager recovery
        Assert.Equal(0, context.Telemetry.Requests);
    }

    [Fact]
    public async Task MatrixFirstSelection_DeniedCopilotFromShell_DoesNotCommitOrPublishAClient()
    {
        var context = await ProviderMatrixAsync(primary: null);
        var before = MatrixCounts(context);
        var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => context.Composer.SwitchModelAsync("copilot/main", ReasoningEffort.Medium, Ct));
        Assert.Equal(ProviderDenyReason.NonOriginalCopilot, denied.Reason);
        Assert.Null(context.Composer.CurrentModel);
        Assert.False(context.Composer.IsConnected);
        AssertMatrixNoProviderWork(context, before);
        AssertNoOriginalCredentialReads();
    }

    [Fact]
    public async Task MatrixRecovery_FailedActorSelection_CommitsEligiblePrimaryButLookupDoesNotConnect_ExplicitRetryRecovers()
    {
        var context = await ProviderMatrixAsync(primary: "copilot/main");
        var before = MatrixCounts(context);
        _clientFactory = _ => throw new InvalidOperationException("matrix planned selection failure");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct));
        Assert.Equal("matrix planned selection failure", error.Message);
        Assert.Equal("ollama-local/other", context.Composer.CurrentModel);
        Assert.False(context.Composer.IsConnected);
        var clientsAfterSelection = _clientCreations;
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(context.Manager, context.Principal)).Status);
        var decision = await context.Factory.DecideConnectAsync(context.Runtime.Configuration, context.Principal.UserId, context.Composer.CurrentModel, Ct);
        Assert.False(decision.Defer); // pure probe grants eligibility, but cannot create or connect anything
        Assert.Equal(clientsAfterSelection, _clientCreations);
        Assert.Equal(before.Connects, context.Telemetry.Connects);
        _clientFactory = model => new MatrixChatClient(model, context.Telemetry);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(context.Manager, context.Principal)).Status);
        Assert.Equal(before.Connects + 1, context.Telemetry.Connects);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [InlineData("ollama-local/compact", "copilot/compact")]
    [InlineData(null, "copilot/compact")]
    public async Task MatrixReload_SwitchKeepsCapturedCompactionAndSubAgentDecisionsStayIdentityBound(string? captured, string reloaded)
    {
        var context = await ProviderMatrixAsync(compaction: captured);
        context.Config.SetCompactionModel(reloaded);
        var before = MatrixCounts(context);
        await context.Composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct);
        Assert.Equal(captured, context.Runtime.Configuration.CompactionModel);
        Assert.Equal(before.Clients + (captured is null ? 1 : 2), _clientCreations);
        Assert.Equal(captured is null, context.Service.AgentOptions.CompactionClient is null);
        Assert.True(_factory.CredentialReads.Queries.Length > before.Queries);
        var queriesBeforeSubAgent = _factory.CredentialReads.Queries.Length;
        var denied = Assert.Throws<ProviderNotEligibleException>(() => context.Service.AgentOptions.SubAgents!.ClientFactory!("copilot/main"));
        Assert.True(_factory.CredentialReads.Queries.Length > queriesBeforeSubAgent);
        Assert.Equal(ProviderDenyReason.NonOriginalCopilot, denied.Reason);
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [InlineData(ProviderMatrixPath.Send)]
    [InlineData(ProviderMatrixPath.SubAgent)]
    [InlineData(ProviderMatrixPath.Switch)]
    [InlineData(ProviderMatrixPath.Direct)]
    [InlineData(ProviderMatrixPath.DirectAsync)]
    [InlineData(ProviderMatrixPath.CompactFull)]
    [InlineData(ProviderMatrixPath.CompactPartial)]
    public async Task Matrix_LegacyFactory_CopilotNewPaths_KeepLegacyRuntimeBehavior(ProviderMatrixPath path)
    {
        var managed = await ProviderMatrixAsync(primary: "copilot/main", compaction: "copilot/compact");
        Assert.False(managed.Composer.IsConnected);
        await using var legacy = managed.Factory.CreateLegacy();
        await legacy.ConnectAsync(Ct);
        Assert.Null(Field<ComposerUserState?>(legacy, "_userState"));
        var context = managed with { LegacyOverride = legacy };
        var before = MatrixCounts(context);
        await MatrixRunPathAsync(context, path, "copilot/main");
        AssertMatrixAllowedModels(context, path, "copilot/main", before);
        Assert.True(legacy.IsConnected);
        Assert.Equal(before.Probes, Volatile.Read(ref _tokenProbes));
        Assert.Equal(before.Requests + (path is ProviderMatrixPath.Send or ProviderMatrixPath.CompactFull or ProviderMatrixPath.CompactPartial ? 1 : 0), context.Telemetry.Requests);
        Assert.Equal(before.Clients + (path == ProviderMatrixPath.Switch ? 2 : path is ProviderMatrixPath.SubAgent or ProviderMatrixPath.Direct or ProviderMatrixPath.DirectAsync ? 1 : 0), _clientCreations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fallback-model")]
    public async Task Matrix_DirectBlankOrUnprefixedModels_FailClosedBeforeDefaultCopilotFactory(string model)
    {
        var context = await ProviderMatrixAsync();
        var before = MatrixCounts(context);
        var sync = Assert.Throws<ProviderNotEligibleException>(() => MatrixDirectClient(context.Service, model));
        Assert.True(_factory.CredentialReads.Queries.Length > before.Queries);
        var queriesBeforeAsync = _factory.CredentialReads.Queries.Length;
        var asyncError = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => MatrixDirectAsyncClient(context.Service, model));
        Assert.True(_factory.CredentialReads.Queries.Length > queriesBeforeAsync);
        Assert.Equal(ProviderDenyReason.NonOriginalCopilot, sync.Reason);
        Assert.Equal(sync.Reason, asyncError.Reason);
        AssertMatrixNoProviderWork(context, before);
        AssertNoOriginalCredentialReads();
    }

    [Fact]
    public async Task MatrixReload_ClearingCopilotCompactionCannotRetrofitASwitchOrManagerRecovery()
    {
        var context = await ProviderMatrixAsync(compaction: "copilot/compact");
        context.Config.SetCompactionModel(null);
        var before = MatrixCounts(context);
        var error = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => context.Composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct));
        Assert.Equal(ProviderDenyReason.NonOriginalCopilot, error.Reason);
        Assert.Equal("copilot/compact", context.Runtime.Configuration.CompactionModel);
        Assert.Equal("ollama-local/main", context.Composer.CurrentModel);
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Retry(context.Manager, context.Principal)).Status);
        AssertMatrixNoProviderWork(context, before);
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [MemberData(nameof(ProviderMatrixDeferredCompactionOperations))]
    public async Task Matrix_DeferredCompaction_EveryRoleAndEffectiveSelection_SafelyRefusesEachApi(
        UserRole role, string primary, string? compaction, ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(role: role, primary: primary, compaction: compaction);
        var before = MatrixCounts(context);
        var queriesBefore = _factory.CredentialReads.Queries.Length;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (path == ProviderMatrixPath.CompactFull) await context.Composer.CompactSessionAsync(Ct);
            else await context.Composer.CompactOldestPercentAsync(50, Ct);
        });
        Assert.Equal("Composer not connected. Call ConnectAsync first.", error.Message);
        Assert.Equal(queriesBefore, _factory.CredentialReads.Queries.Length); // safe pre-admission refusal, not a gate read
        AssertMatrixNoProviderWork(context, before);
        AssertNoOriginalCredentialReads();
    }

    [Theory]
    [MemberData(nameof(ProviderMatrixRoleCompactionPaths))]
    public async Task Matrix_EligibleOllamaExplicitCompaction_UsesCapturedClientDespiteReload(UserRole role, ProviderMatrixPath path)
    {
        var context = await ProviderMatrixAsync(role: role, compaction: "ollama-local/compact");
        context.Config.SetCompactionModel("copilot/compact");
        var before = MatrixCounts(context);
        await MatrixRunPathAsync(context, path, "unused");
        AssertMatrixAllowedModels(context, path, "unused", before);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        AssertNoOriginalCredentialReads();
    }

    private ComposerAgentService MatrixRealDirectoryService(ProviderGate gate, Func<string, IChatClient> clients,
        string? compaction = null) => new("ollama-local/main", 64000, 50, null, null, "matrix system", [], null,
            StateDir, compaction, NullLogger<ComposerAgentService>.Instance, clients, null,
            ["ollama-local/main", "ollama-local/other"], null, null, false, [], providerGate: gate);

    private async Task MatrixDisableViaSqlAsync(int userId)
    {
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET enabled=0 WHERE id={userId}", Ct));
    }

    [Fact]
    public async Task MatrixPerClientFreshness_SwitchDisabledAfterSelectionBeforeReplacementPrimary_NoReplacementClient()
    {
        var (original, invited) = await TwoUsersAsync();
        var factory = NewFactory(BuildProvider());
        _providerResolver = CopilotAwareProvider;
        CountTokenProbes();
        var checks = 0;
        var clients = 0;
        var telemetry = new MatrixTelemetry();
        ProviderGate gate = async (p, c, ct) =>
        {
            var verdict = await factory.EvaluateEligibilityAsync(invited.UserId, p, c, ct);
            if (Interlocked.Increment(ref checks) == 3) await MatrixDisableViaSqlAsync(invited.UserId);
            return verdict;
        };
        _factory.CredentialReads.Start(original.UserId);
        await using var service = MatrixRealDirectoryService(gate, model =>
        {
            Interlocked.Increment(ref clients);
            return new MatrixChatClient(model, telemetry);
        });
        await service.ConnectAsync(Ct);
        Assert.Equal(1, clients);
        var error = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => service.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct));
        Assert.Equal(ProviderDenyReason.DisabledAccount, error.Reason);
        Assert.Equal(4, checks);
        Assert.Equal(1, clients);
        Assert.Equal("ollama-local/main", service.Model);
        Assert.Null(service.ChatClient);
        Assert.False(service.IsConnected);
        Assert.Equal(new[] { "ollama-local/main" }, telemetry.DisposedModels.ToArray());
        Assert.Equal(0, telemetry.Requests);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        AssertNoOriginalCredentialReads();
    }

    [Fact]
    public async Task MatrixPerClientFreshness_SwitchDisabledAfterReplacementPrimary_DeniesReplacementCompaction()
    {
        var (original, invited) = await TwoUsersAsync();
        var factory = NewFactory(BuildProvider());
        _providerResolver = CopilotAwareProvider;
        CountTokenProbes();
        var clients = 0;
        var telemetry = new MatrixTelemetry();
        _factory.CredentialReads.Start(original.UserId);
        await using var service = MatrixRealDirectoryService(
            (p, c, ct) => factory.EvaluateEligibilityAsync(invited.UserId, p, c, ct), model =>
            {
                if (Interlocked.Increment(ref clients) == 3) MatrixDisableViaSqlAsync(invited.UserId).GetAwaiter().GetResult();
                return new MatrixChatClient(model, telemetry);
            }, compaction: "ollama-local/compact");
        await service.ConnectAsync(Ct);
        Assert.Equal(2, clients);
        var error = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => service.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct));
        Assert.Equal(ProviderDenyReason.DisabledAccount, error.Reason);
        Assert.Equal(3, clients); // only the legitimate replacement primary, never the replacement compaction
        Assert.Equal("ollama-local/main", service.Model);
        Assert.Null(service.ChatClient);
        Assert.False(service.IsConnected);
        Assert.Equal(new[] { "ollama-local/main", "ollama-local/compact", "ollama-local/other" }, telemetry.DisposedModels.ToArray());
        Assert.Equal(0, telemetry.Requests);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        AssertNoOriginalCredentialReads();
    }

    [Fact]
    public async Task MatrixPerClientFreshness_DisabledAfterSelectionBeforePrimaryCreation_NoClientOrCredentialRead()
    {
        var (original, invited) = await TwoUsersAsync();
        var factory = NewFactory(BuildProvider());
        _providerResolver = CopilotAwareProvider;
        CountTokenProbes();
        var checks = 0;
        var clients = 0;
        var telemetry = new MatrixTelemetry();
        ProviderGate gate = async (p, c, ct) =>
        {
            var verdict = await factory.EvaluateEligibilityAsync(invited.UserId, p, c, ct);
            if (Interlocked.Increment(ref checks) == 1) await MatrixDisableViaSqlAsync(invited.UserId);
            return verdict; // selection saw enabled; actual client must re-read after this mutation
        };
        _factory.CredentialReads.Start(original.UserId);
        await using var service = MatrixRealDirectoryService(gate, model =>
        {
            Interlocked.Increment(ref clients);
            return new MatrixChatClient(model, telemetry);
        });
        var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => service.ConnectAsync(Ct));
        Assert.Equal(ProviderDenyReason.DisabledAccount, denied.Reason);
        Assert.Equal(2, checks);
        Assert.Equal(0, clients);
        Assert.Equal(0, telemetry.Requests);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.False(service.IsConnected);
        AssertNoOriginalCredentialReads();
    }

    [Fact]
    public async Task MatrixPerClientFreshness_DisabledAfterPrimaryBeforeCompactionCreation_DeniesCompaction()
    {
        var (original, invited) = await TwoUsersAsync();
        var factory = NewFactory(BuildProvider());
        _providerResolver = CopilotAwareProvider;
        CountTokenProbes();
        var clients = 0;
        var telemetry = new MatrixTelemetry();
        _factory.CredentialReads.Start(original.UserId);
        await using var service = MatrixRealDirectoryService(
            (p, c, ct) => factory.EvaluateEligibilityAsync(invited.UserId, p, c, ct), model =>
            {
                Interlocked.Increment(ref clients);
                MatrixDisableViaSqlAsync(invited.UserId).GetAwaiter().GetResult();
                return new MatrixChatClient(model, telemetry);
            }, compaction: "ollama-local/compact");
        var denied = await Assert.ThrowsAsync<ProviderNotEligibleException>(() => service.ConnectAsync(Ct));
        Assert.Equal(ProviderDenyReason.DisabledAccount, denied.Reason);
        Assert.Equal(1, clients); // primary was legitimately created BEFORE invalidation; no compaction client
        Assert.False(service.IsConnected);
        Assert.Null(service.ChatClient); // the legitimate primary was rolled back
        Assert.Equal(new[] { "ollama-local/main" }, telemetry.DisposedModels.ToArray());
        Assert.Equal(0, telemetry.Requests);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        AssertNoOriginalCredentialReads();
    }
}
