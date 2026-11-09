using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;
using SharpCoder.SubAgents;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// FINDING 6 — A LIVE CHILD INVOCATION THROUGH THE REAL HOOK.
/// <para>
/// A sub-agent is started by a real composed Composer <see cref="CodingAgent"/>, and its client
/// ACTUALLY PERFORMS the read: the child's tool call travels through the real SharpCoder 0.23.0
/// <see cref="AgentOptions.AuthorizeTool"/> callback, and the file content the child read reaches the
/// model. A child that merely returned plain text would not satisfy this.
/// </para>
/// <para>
/// THE READ FIXTURE LIVES IN THE RESOLVED AGENT WORKSPACE: the agent's work directory is
/// <c>BrainRepoManager.WorkDirectory</c> (<c>{basePath}/repos</c>), which is what
/// <c>ComposerAgentService</c> hands SharpCoder as <c>AgentOptions.WorkDirectory</c>. The fixture is
/// therefore written THERE, not into an arbitrary directory — a fixture outside the workspace would
/// make the read fail for an ordinary file reason and the test would prove nothing.
/// </para>
/// <para>
/// THE DENIAL MUST BE AN EXPLICIT AUTHORIZATION REFUSAL: the negative case asserts the host-authored
/// refusal text (which names the tool and the reason), never merely "the output is not the file
/// content" — an ordinary file error would satisfy the latter but not the former.
/// </para>
/// </summary>
public sealed class ComposerLiveChildInvocationTests : IAsyncLifetime
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The distinctive marker the child's read must surface. A plain-text child cannot fake it.</summary>
    private const string DistinctivePayload = "COMPOSER-CHILD-READ-PAYLOAD-8f3a2c";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-livechild-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerLiveChildInvocationTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(StateDir);
        _factory = new MemoryDbFactory();
        _users = new UserService(_factory, NullLogger<UserService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StateDir => Path.Combine(_dir, "state");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var composer in _composers)
        {
            try { await composer.DisposeAsync(); }
            catch (Exception) { /* cleanup must not mask a test failure */ }
        }

        foreach (var provider in _providers)
        {
            try { await provider.DisposeAsync(); }
            catch (Exception) { /* cleanup must not mask a test failure */ }
        }

        try { _factory.Dispose(); }
        catch (Exception) { /* cleanup must not mask a test failure */ }

        TestHelpers.ForceDeleteDirectory(_dir);
    }

    private sealed class MemoryDbFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        public MemoryDbFactory()
        {
            var name = $"livechild-{Guid.NewGuid():N}";
            _keeper = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
            _keeper.Open();
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_keeper).Options;
            using var ctx = new CopilotHiveDbContext(_options);
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);

        public void Dispose() => _keeper.Dispose();
    }

    private async Task<(int OriginalId, int SecondId)> SeedAccountsAsync(UserRole secondRole)
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "original", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", secondRole, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        return (original.Id, second.Id);
    }

    private ActingUserPrincipal Resolve(int userId)
    {
        var resolver = new HivePrincipalResolver(
            _users, HiveAuthMode.AuthenticationEnabled, NullLogger<HivePrincipalResolver>.Instance);
        var resolution = resolver.ResolveByUserIdAsync(userId, Ct).GetAwaiter().GetResult();
        Assert.True(resolution.IsResolved, $"Account {userId} did not resolve: {resolution.RefusalReason}.");
        return resolution.Principal!;
    }

    private ComposerUserState StateOf(int userId) =>
        ComposerUserState.ResolveAsync(_users, userId, Ct).GetAwaiter().GetResult();

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_users);
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddSingleton<IGoalStore>(new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// Writes the read fixture into the RESOLVED AGENT WORKSPACE — the directory the agent's file
    /// tools are confined to. It is derived from the SAME <see cref="BrainRepoManager"/> the Composer is
    /// built with, so it cannot drift from the agent's real workspace.
    /// </summary>
    private async Task<string> WriteReadFixtureInTheAgentWorkspaceAsync(BrainRepoManager repoManager)
    {
        var workDirectory = repoManager.WorkDirectory;
        Assert.True(Directory.Exists(workDirectory),
            $"The resolved agent workspace '{workDirectory}' must exist before the fixture is written.");

        var filePath = Path.Combine(workDirectory, "child-read-fixture.txt");
        await File.WriteAllTextAsync(filePath, DistinctivePayload, Ct);
        return filePath;
    }

    /// <summary>
    /// Builds an identity-bound Composer whose agent streams over <paramref name="client"/> and whose
    /// sub-agent runs on <paramref name="childClient"/>, over a REAL <see cref="BrainRepoManager"/> so
    /// the built-in read tools are registered and confined to the real workspace.
    /// </summary>
    private Composer CreateComposer(
        ServiceProvider provider, int userId, IChatClient client, IChatClient childClient,
        out BrainRepoManager repoManager)
    {
        repoManager = new BrainRepoManager(_dir, NullLogger<BrainRepoManager>.Instance);

        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            repoManager: repoManager,
            stateDir: StateDir,
            serviceProvider: provider,
            hiveConfig: new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels =
                    [
                        new ModelEntry { Name = "test-model" },
                        new ModelEntry { Name = SubAgentModel },
                    ],
                },
                Composer = new ComposerConfig { Model = "test-model" },
            },
            chatClientFactory: model => string.Equals(model, SubAgentModel, StringComparison.Ordinal)
                ? childClient
                : client,
            userState: StateOf(userId),
            principal: Resolve(userId));
        _composers.Add(composer);
        return composer;
    }

    private const string SubAgentModel = "sub-model";

    private static ComposerAgentService AgentServiceOf(Composer composer) =>
        (ComposerAgentService)typeof(Composer).GetField("_agentService", PrivateFlags)!.GetValue(composer)!;

    /// <summary>
    /// The ALLOWED case: a Member-class user's child really reads the fixture through the real hook.
    /// <para>
    /// The child's client dispatches a REAL <c>read_file</c> tool call, so the read runs inside
    /// SharpCoder's own guard: the hook decides, and only then does the file tool produce its content.
    /// The DISTINCTIVE payload proves the child really performed the read — a plain-text child could
    /// not produce it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ALiveChildRead_ThroughTheRealHook_SucceedsForAMemberClassUser()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();

        var invocation = await RunLiveChildReadAsync(provider, secondId);

        // The CHILD really read the file: its content reached the model through the child's own tool
        // result. A child that merely returned text could not produce this payload.
        var childResults = invocation.ChildResults;
        Assert.Single(childResults);
        Assert.Contains(DistinctivePayload, childResults[0], StringComparison.Ordinal);

        // The child's read was NOT refused by the authorization boundary.
        Assert.DoesNotContain("role does not permit", childResults[0], StringComparison.Ordinal);
        Assert.DoesNotContain("was not executed", childResults[0], StringComparison.Ordinal);

        // The parent's delegation was allowed too.
        var parentResults = invocation.ParentResults;
        Assert.NotEmpty(parentResults);
        Assert.All(parentResults, result =>
            Assert.DoesNotContain("role does not permit", result, StringComparison.Ordinal));
    }

    /// <summary>
    /// The DENIED case: a FRESH denial — the child has already STARTED (so its delegation was
    /// legitimately allowed), and the account is disabled while the child is running, so the child's
    /// OWN read is refused by the authorization boundary on its next (and only) fresh lookup.
    /// <para>
    /// The refusal is asserted by its CONTENT (the host's "does not permit" reason naming the tool),
    /// never by a negative-only check on the payload — an ordinary file error would leave the payload
    /// absent too, but it would not carry the authorization reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ALiveChildRead_ThroughTheRealHook_IsRefusedForAnAccountThatCannotHoldTheCapability()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();

        // The shared rendezvous disables the account AFTER the real child starts. It then lets
        // the parent's await_sub_agents be refused and joins the parent BEFORE releasing the child.
        // Parent completion is deliberately not treated as evidence of child result delivery.
        var invocation = await RunLiveChildReadAsync(provider, secondId, disableAccountBeforeRead: true);
        var childResults = invocation.ChildResults;
        Assert.Single(childResults);

        // THE REFUSAL IS EXPLICIT: the host's authorization boundary refused the call, and the text
        // names the tool. This is what distinguishes an authorization refusal from an ordinary file
        // error — an ordinary error would say "Error: ..." and would NOT say the host denied it.
        Assert.Contains("read_file", childResults[0], StringComparison.Ordinal);
        Assert.Contains("host denied", childResults[0], StringComparison.Ordinal);
        Assert.Contains("was not executed", childResults[0], StringComparison.Ordinal);

        // …and the payload never reached the model: the read did not happen.
        Assert.DoesNotContain(DistinctivePayload, childResults[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Error:", childResults[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CORRELATION ID AND THE ACTING IDENTITY TRAVEL WITH THE CHILD'S CALL: the child's read is
    /// authorized under the PARENT runtime's correlation id and its acting account, so the child can
    /// never be authorized as a different identity than the one that started it.
    /// </summary>
    [Fact]
    public async Task TheChildsCall_IsAuthorizedUnderTheParentsCorrelationIdAndActingIdentity()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();

        // ONE runtime: the live child, gate and agent options come from the SAME Composer.
        var invocation = await RunLiveChildReadAsync(provider, secondId);
        var composer = invocation.Composer;
        var service = AgentServiceOf(composer);

        var gate = (ComposerToolAuthorizationGate)typeof(Composer)
            .GetField("_toolAuthorizationGate", PrivateFlags)!.GetValue(composer)!;

        Assert.Equal($"composer-user:{secondId}", gate.CorrelationId);
        Assert.Equal(secondId, gate.UserId);

        // The shared helper already connected and executed this very service; do not reconnect
        // here and accidentally assert against options from a replacement parent agent.
        var options = service.AgentOptions;
        Assert.NotNull(options.AuthorizeTool);
        Assert.Equal($"composer-user:{secondId}", options.ToolAuthorizationCorrelationId);

        // The callback the runtime handed SharpCoder is the gate's own callback — the SAME instance
        // the child's inherited pair is copied from. (A method group creates a NEW delegate on each
        // access, so the comparison is on the TARGET and the METHOD, not on the delegate identity.)
        Assert.Same(gate, options.AuthorizeTool!.Target);
        Assert.Equal(nameof(ComposerToolAuthorizationGate.AuthorizeToolAsync), options.AuthorizeTool.Method.Name);

        // Not just parent options: this is the context SharpCoder's CHILD guard actually constructed
        // for the child's model-issued read, and the callback that guard originally captured.
        var observed = invocation.Authorization;
        Assert.Equal("read_file", observed.Context.ToolName);
        Assert.Equal("child-read-fixture.txt", Assert.IsType<string>(observed.Context.Arguments["filePath"]));
        Assert.Equal(options.ToolAuthorizationCorrelationId, observed.Context.CorrelationId);
        Assert.Same(gate, observed.Original.Target);
        Assert.Equal(secondId, Assert.IsType<ComposerToolAuthorizationGate>(observed.Original.Target).UserId);
        Assert.Same(options.AuthorizeTool.Target, observed.Original.Target);
        Assert.Equal(options.AuthorizeTool.Method, observed.Original.Method);
        Assert.Contains(DistinctivePayload, Assert.Single(invocation.ChildResults), StringComparison.Ordinal);
    }

    private static readonly TimeSpan MilestoneTimeout = TimeSpan.FromSeconds(30);

    private sealed record ObservedChildAuthorization(
        ToolAuthorizationContext Context,
        Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>> Original);

    private sealed record LiveChildReadResult(
        Composer Composer,
        IReadOnlyList<string> ParentResults,
        IReadOnlyList<string> ChildResults,
        ObservedChildAuthorization Authorization);

    /// <summary>
    /// Test-only, capture-and-forward observation of the REAL guarded read_file from the child's
    /// first ChatOptions.Tools request. It neither constructs a context nor answers authorization:
    /// the original callback receives the exact context/token and its task is returned unchanged.
    /// This intentionally depends on SharpCoder 0.23.0's private instance-readonly _authorize field;
    /// a changed guard shape or a runtime disallowing that reflection must fail, not fall back to a mock.
    /// </summary>
    private sealed class ChildAuthorizationProbe
    {
        private readonly List<ObservedChildAuthorization> _observations = [];
        private AIFunction? _guard;
        private FieldInfo? _authorizeField;
        private Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>>? _original;

        public void Install(ChatOptions? options)
        {
            var tools = options?.Tools
                ?? throw new InvalidOperationException("The child's first real request has no ChatOptions.Tools.");
            var guard = Assert.Single(tools.OfType<AIFunction>(), tool => tool.Name == "read_file");
            var field = guard.GetType().GetField("_authorize", PrivateFlags)
                ?? throw new InvalidOperationException("The child's read_file has no SharpCoder _authorize guard seam.");
            var original = Assert.IsType<Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>>>(
                field.GetValue(guard));
            // Inspect the ORIGINAL target, never the observer's closure (and never infer identity
            // by parsing a correlation string).
            Assert.IsType<ComposerToolAuthorizationGate>(original.Target);

            Task<ToolAuthorizationDecision> CaptureAndForward(ToolAuthorizationContext context, CancellationToken token)
            {
                lock (_observations)
                {
                    _observations.Add(new ObservedChildAuthorization(context, original));
                }
                return original(context, token);
            }

            field.SetValue(guard,
                (Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>>)CaptureAndForward);
            _guard = guard;
            _authorizeField = field;
            _original = original;
        }

        public ObservedChildAuthorization[] Snapshot()
        {
            lock (_observations) { return _observations.ToArray(); }
        }

        public void Restore()
        {
            if (_guard is not null)
                _authorizeField!.SetValue(_guard, _original);
        }
    }

    /// <summary>
    /// Shared live-invocation driver for ALL THREE facts. Parent execution, delivery of the child's
    /// read result to its model, and an independent host manager.AwaitAsync are distinct milestones.
    /// In particular a freshly denied parent await_sub_agents cannot stand in for a child join.
    /// </summary>
    private async Task<LiveChildReadResult> RunLiveChildReadAsync(
        ServiceProvider provider, int userId, bool disableAccountBeforeRead = false)
    {
        var childReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChildRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParentAwait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childResultDelivered = new TaskCompletionSource<FunctionResultContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChildFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ChildAuthorizationProbe();

        var parentClient = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "start_sub_agent", new Dictionary<string, object?>
            {
                ["task"] = "read the fixture file",
                ["model"] = SubAgentModel,
            })),
            Round(ToolCall("call-2", "await_sub_agents", new Dictionary<string, object?>())),
            Round(Text("finished")))
        {
            BeforeRequest = async (index, _, _, token) =>
            {
                // The delegation has already been authorized before this request. Do not let the
                // parent's await race the disablement: its fresh lookup MUST see the disabled user.
                if (index == 1 && disableAccountBeforeRead)
                    await GuardAsync("parent await request release", releaseParentAwait.Task, token);
            },
        };
        var childClient = new ScriptedRoundsChatClient(
            Round(ToolCall("child-call-1", "read_file", new Dictionary<string, object?>
            {
                ["filePath"] = "child-read-fixture.txt",
            })),
            Round(Text("the fixture was read")))
        {
            BeforeRequest = async (index, request, options, token) =>
            {
                if (index == 0)
                {
                    try
                    {
                        probe.Install(options);
                        childReady.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        childReady.TrySetException(ex);
                        throw;
                    }
                    await GuardAsync("child read request release", releaseChildRead.Task, token);
                }
                else if (index == 1)
                {
                    try
                    {
                        // Signal only when the actual read tool result is delivered in a real
                        // child request. Starting a child or capturing its context is not delivery.
                        var delivered = Assert.Single(request.SelectMany(message => message.Contents)
                            .OfType<FunctionResultContent>(), result => result.CallId == "child-call-1");
                        childResultDelivered.TrySetResult(delivered);
                    }
                    catch (Exception ex)
                    {
                        childResultDelivered.TrySetException(ex);
                        throw;
                    }
                    await GuardAsync("child final response release", releaseChildFinish.Task, token);
                }
            },
        };

        var composer = CreateComposer(provider, userId, parentClient, childClient, out var repoManager);
        var service = AgentServiceOf(composer);
        using var parentCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var hostJoinCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        CodingAgent? agent = null;
        SubAgentManager? manager = null;
        Task? parentRun = null;
        Task<IReadOnlyList<SubAgentInfo>>? hostJoin = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        LiveChildReadResult? invocation = null;

        // Read-only host seam: never force-create or substitute a manager for the real delegation.
        SubAgentManager? ReadRealManager() => agent?.ActiveSubAgentManager;

        async Task CleanupAsync(string name, Func<Task> action, bool expectedCancellation = false)
        {
            try { await GuardAsync(name, action(), CancellationToken.None); }
            catch (OperationCanceledException) when (expectedCancellation) { /* our finally cancelled this wait */ }
            catch (Exception ex)
            {
                // Re-observing the same faulted parent/host task is not a SECOND failure.
                if (!ReferenceEquals(ex, primaryFailure))
                    cleanupFailures.Add(new InvalidOperationException($"Live-child cleanup '{name}' failed.", ex));
            }
        }

        try
        {
            await WriteReadFixtureInTheAgentWorkspaceAsync(repoManager);
            await GuardAsync("Composer connection", service.ConnectAsync(parentCancellation.Token), parentCancellation.Token);
            agent = service.Agent ?? throw new InvalidOperationException("The agent was not created.");
            parentRun = RunToCompletionAsync(agent, "read the fixture through a sub-agent", parentCancellation.Token);
            await GuardAsync("child first real tools request / observer installation", childReady.Task, parentCancellation.Token);
            manager = ReadRealManager() ?? throw new InvalidOperationException("The real delegation did not create a child manager.");

            // A host join is started while the child is parked, independently of the model's
            // await_sub_agents tool. Its snapshot therefore definitely includes the accepted child.
            Assert.Single(manager.GetStatus());
            hostJoin = manager.AwaitAsync(null, hostJoinCancellation.Token);
            Assert.False(hostJoin.IsCompleted, "The independent host join must wait for the parked live child.");

            if (disableAccountBeforeRead)
            {
                await GuardAsync("disable acting account after child start",
                    _users.SetUserEnabledAsync(userId, enabled: false, parentCancellation.Token), parentCancellation.Token);
                releaseParentAwait.TrySetResult();
                await GuardAsync("parent completion BEFORE delayed child delivery", parentRun, parentCancellation.Token);
                Assert.False(releaseChildRead.Task.IsCompleted, "The child must still be parked after parent completion.");
                Assert.False(childResultDelivered.Task.IsCompleted, "Parent completion must precede child result delivery.");
                Assert.False(hostJoin.IsCompleted, "Denied parent await_sub_agents must not complete the host child join.");
                var deniedParentAwait = Assert.Single(parentClient.Requests.SelectMany(request => request)
                    .SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.CallId == "call-2");
                Assert.Contains("await_sub_agents", deniedParentAwait.Result?.ToString() ?? string.Empty, StringComparison.Ordinal);
                Assert.Contains("host denied", deniedParentAwait.Result?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }

            releaseParentAwait.TrySetResult();
            releaseChildRead.TrySetResult();
            var delivered = await GuardAsync("child read tool result delivered to child's model",
                childResultDelivered.Task, hostJoinCancellation.Token);
            Assert.False(hostJoin.IsCompleted, "Tool-result delivery alone is not the child's terminal completion.");
            releaseChildFinish.TrySetResult();
            var children = await GuardAsync("independent host manager.AwaitAsync child join", hostJoin, hostJoinCancellation.Token);
            var child = Assert.Single(children);
            Assert.Equal(SubAgentStatus.Completed, child.Status);
            Assert.Null(child.Error);
            await GuardAsync("parent execution completion", parentRun, parentCancellation.Token);

            var observed = Assert.Single(probe.Snapshot());
            var guardGate = Assert.IsType<ComposerToolAuthorizationGate>(observed.Original.Target);
            var runtimeGate = (ComposerToolAuthorizationGate)typeof(Composer)
                .GetField("_toolAuthorizationGate", PrivateFlags)!.GetValue(composer)!;
            Assert.Same(runtimeGate, guardGate);
            Assert.Equal(userId, guardGate.UserId);
            Assert.Equal($"composer-user:{userId}", observed.Context.CorrelationId);
            Assert.Equal("read_file", observed.Context.ToolName);
            Assert.Equal("child-read-fixture.txt", Assert.IsType<string>(observed.Context.Arguments["filePath"]));
            var childResults = childClient.ToolResults();
            Assert.Equal(delivered.Result?.ToString() ?? string.Empty, Assert.Single(childResults));
            invocation = new(composer, parentClient.ToolResults(), childResults, observed);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }
        finally
        {
            // Release EVERY test-owned park even if installation, disablement or an assertion failed.
            releaseParentAwait.TrySetResult();
            releaseChildRead.TrySetResult();
            releaseChildFinish.TrySetResult();
            await CleanupAsync("cancel parent execution", () =>
            {
                parentCancellation.Cancel();
                return Task.CompletedTask;
            });
            await CleanupAsync("cancel independent host wait", () =>
            {
                hostJoinCancellation.Cancel();
                return Task.CompletedTask;
            });
            if (parentRun is not null)
                await CleanupAsync("join parent execution", () => parentRun, expectedCancellation: true);

            // Read again AFTER the parent join to include any start accepted during cancellation.
            // The child lifetime is not linked to the parent token: CancelAllAsync is indispensable,
            // and also joins the manager's actual runner tasks, not merely its completion snapshots.
            await CleanupAsync("find real child manager", () =>
            {
                manager = ReadRealManager() ?? manager;
                return Task.CompletedTask;
            });
            if (manager is not null)
                await CleanupAsync("cancel children and join manager runners", () => manager.CancelAllAsync());
            if (hostJoin is not null)
                await CleanupAsync("join independent host wait", () => hostJoin, expectedCancellation: true);
            if (manager is not null)
            {
                // Independent of a cancelled test token. Cancel the underlying cleanup wait as
                // well as bounding the caller, so a broken completion path cannot orphan this wait.
                using var cleanupJoinCancellation = new CancellationTokenSource(MilestoneTimeout);
                await CleanupAsync("final host manager.AwaitAsync join", async () =>
                {
                    var finalChildren = await manager.AwaitAsync(null, cleanupJoinCancellation.Token);
                    // The manager represents runner faults as snapshots rather than throwing.
                    // Do not silently lose a secondary child failure during primary-failure cleanup.
                    Assert.All(finalChildren, child => Assert.True(
                        child.Status is SubAgentStatus.Completed or SubAgentStatus.Cancelled,
                        $"Child '{child.Id}' ended as {child.Status}: {child.Error}"));
                });
            }
            await CleanupAsync("restore child's original authorization delegate", () =>
            {
                probe.Restore();
                return Task.CompletedTask;
            });
        }

        if (primaryFailure is not null && cleanupFailures.Count == 0)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (primaryFailure is not null)
            cleanupFailures.Insert(0, primaryFailure);
        if (cleanupFailures.Count != 0)
            throw new AggregateException("Live-child invocation / cleanup failures (primary failure first, if any).", cleanupFailures);
        return invocation ?? throw new InvalidOperationException("No live-child invocation result was produced.");
    }

    private static async Task GuardAsync(string name, Task task, CancellationToken token)
    {
        try { await task.WaitAsync(MilestoneTimeout, token); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out waiting for '{name}' after {MilestoneTimeout}.", ex);
        }
    }

    private static async Task<T> GuardAsync<T>(string name, Task<T> task, CancellationToken token)
    {
        await GuardAsync(name, (Task)task, token);
        return await task;
    }

    private static async Task RunToCompletionAsync(CodingAgent agent, string prompt, CancellationToken token)
    {
        var session = AgentSession.Create("composer");
        await foreach (var _ in agent.ExecuteStreamingAsync(session, prompt, token)) { }
    }

    private static ChatResponseUpdate[] Round(params AIContent[] contents) =>
    [
        new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            FinishReason = contents.OfType<FunctionCallContent>().Any()
                ? ChatFinishReason.ToolCalls
                : ChatFinishReason.Stop,
        },
    ];

    private static TextContent Text(string text) => new(text);

    private static FunctionCallContent ToolCall(string callId, string name, IDictionary<string, object?> arguments) =>
        new(callId, name, arguments);

    /// <summary>
    /// A chat client scripted BY REQUEST NUMBER that records the messages of every request, so a test
    /// can read the tool RESULTS the model was actually given.
    /// <para>
    /// It serves BOTH paths: the parent's agent streams (<see cref="GetStreamingResponseAsync"/>),
    /// while a CHILD agent runs the NON-STREAMING path (<see cref="GetResponseAsync"/>) — so the same
    /// script drives a live child read.
    /// </para>
    /// </summary>
    private sealed class ScriptedRoundsChatClient(
        params IReadOnlyList<ChatResponseUpdate>[] rounds) : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _requests = [];

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => _requests;

        public ChatClientMetadata Metadata => new("scripted-rounds", null, "scripted-rounds-model");

        /// <summary>
        /// Runs after recording each real request but BEFORE returning its scripted response, on both
        /// client paths. Exposes the real Tools and delivered messages for forwarding observation and
        /// deterministic, cancellation-aware rendezvous only; it does not invoke tools itself.
        /// </summary>
        public Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task>? BeforeRequest { get; init; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestIndex = _requests.Count;
            var request = messages.ToList();
            _requests.Add(request);
            if (BeforeRequest is { } hook)
                await hook(requestIndex, request, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return BuildResponse(requestIndex);
        }

        private ChatResponse BuildResponse(int requestIndex)
        {
            if (requestIndex >= rounds.Length)
            {
                throw new InvalidOperationException(
                    $"Unscripted round {requestIndex + 1}: the script has {rounds.Length} round(s).");
            }

            var response = new ChatResponse();
            foreach (var update in rounds[requestIndex])
            {
                response.Messages.Add(new ChatMessage(
                    update.Role ?? ChatRole.Assistant,
                    update.Contents?.ToArray() ?? []));
            }
            return response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            var requestIndex = _requests.Count;
            var request = messages.ToList();
            _requests.Add(request);
            if (BeforeRequest is { } hook)
                await hook(requestIndex, request, options, cancellationToken);

            if (requestIndex >= rounds.Length)
            {
                throw new InvalidOperationException(
                    $"Unscripted streaming round {requestIndex + 1}: the script has {rounds.Length} round(s).");
            }

            foreach (var update in rounds[requestIndex])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        /// <summary>The tool-result texts the model received on the round AFTER a tool call.</summary>
        public IReadOnlyList<string> ToolResults() =>
            _requests
                .SelectMany(request => request)
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Select(content => content.Result?.ToString() ?? string.Empty)
                .ToArray();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
