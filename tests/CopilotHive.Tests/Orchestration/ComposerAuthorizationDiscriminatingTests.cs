using System.Reflection;
using System.Diagnostics;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Runtime.CompilerServices;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// FINDING 8 — DISCRIMINATING FRESHNESS, CONCURRENCY AND MUTATOR COVERAGE.
/// <para>
/// (a) SAME-AGENT DISPATCHED FRESHNESS: two tool dispatches through ONE CONNECTED agent, with a
/// mid-sequence demotion or disablement, so the SECOND dispatch observes the change. This is NOT a
/// reflected-gate call — the change is observed by the model-facing tool path.
/// </para>
/// <para>
/// (b) A DETERMINISTIC TWO-USER CONCURRENCY test with a REAL overlap rendezvous (both agents are
/// inside their dispatch at the same time), proving the two guards stay independent.
/// </para>
/// <para>
/// (c) EVERY PREVIOUSLY OMITTED GUARDED MUTATOR gets an ISOLATING denial AND an allowed control:
/// extend_goal_iterations, review_goal, update_release, git_fetch, the three config mutations, the
/// five knowledge mutations — plus create_goal and create_issue.
/// </para>
/// </summary>
public sealed class ComposerAuthorizationDiscriminatingTests : IAsyncLifetime
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-discrim-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerAuthorizationDiscriminatingTests()
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
        internal string ConnectionString { get; }

        public MemoryDbFactory(string? connectionString = null, GuardReadRendezvous? readGate = null)
        {
            ConnectionString = connectionString ?? $"Data Source=discrim-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            _keeper = new SqliteConnection(ConnectionString);
            _keeper.Open();
            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(ConnectionString);
            if (readGate is not null) builder.AddInterceptors(readGate);
            _options = builder.Options;
            using var ctx = new CopilotHiveDbContext(_options);
            ctx.Database.EnsureCreated();
        }

        // Every context owns its own connection. No SqliteConnection is shared by concurrent readers.
        public CopilotHiveDbContext CreateDbContext() => new(_options);
        public void Dispose() => _keeper.Dispose();
    }

    /// <summary>Rendezvous on the real SQL read performed by UserService inside fresh authorization.</summary>
    private sealed class GuardReadRendezvous(params int[] userIds) : DbCommandInterceptor
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, DbConnection> _connections = [];
        internal readonly TaskCompletionSource BothReadsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Armed { get; set; }
        internal int InFlight { get; private set; }
        internal KeyValuePair<int, DbConnection>[] Snapshot()
        {
            lock (_lock) return _connections.ToArray();
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Armed || !command.CommandText.Contains("FROM " + (char)34 + "users" + (char)34, StringComparison.Ordinal)
                || !command.CommandText.Contains("WHERE", StringComparison.Ordinal)) return result;
            var id = command.Parameters.Cast<DbParameter>().Select(p => p.Value)
                .OfType<int>().SingleOrDefault(i => userIds.Contains(i));
            if (!userIds.Contains(id)) return result;
            lock (_lock)
            {
                InFlight++;
                _connections.TryAdd(id, command.Connection!);
                if (_connections.Count == userIds.Length) BothReadsEntered.TrySetResult();
            }
            try { await Release.Task.WaitAsync(cancellationToken); }
            finally { lock (_lock) InFlight--; }
            return result;
        }
    }

    private async Task<(int OriginalId, int SecondId, int ThirdId)> SeedAccountsAsync(
        UserRole secondRole, UserRole thirdRole)
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "original", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", secondRole, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        await _users.InviteUserAsync("third", thirdRole, Ct);
        var third = await _users.ActivateInvitationAsync("third", "300", null, null, null, Ct);
        return (original.Id, second.Id, third.Id);
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

    private ServiceProvider BuildProvider(Action<ServiceCollection>? configure = null, UserService? directory = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(directory ?? _users);
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddSingleton<IGoalStore>(new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "test-model" }] },
            Composer = new ComposerConfig { Model = "test-model" },
        });
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private Composer CreateComposer(
        ServiceProvider provider, int userId, CopilotHiveDbContext goalDb,
        IIssueStore? issueStore = null, ConfigRepoManager? configRepo = null,
        KnowledgeGraph? knowledgeGraph = null, IBrainRepoManager? repoManager = null,
        IChatClient? chatClient = null, GoalReviewService? goalReviewService = null)
    {
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            repoManager: repoManager,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => chatClient ?? new MockChatClient(_ => "unused"),
            configRepo: configRepo,
            goalReviewService: goalReviewService,
            knowledgeGraph: knowledgeGraph,
            issueStore: issueStore,
            userState: StateOf(userId),
            principal: Resolve(userId));
        _composers.Add(composer);
        return composer;
    }

    private static async Task<Goal> SeedDraftGoalAsync(GoalStore store, string id = "goal-a")
    {
        var goal = new Goal
        {
            Id = id,
            Description = "A draft goal",
            Status = GoalStatus.Draft,
            CreatedAt = DateTime.UtcNow,
        };
        await store.CreateGoalAsync(goal, Ct);
        return goal;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // (a) SAME-AGENT DISPATCHED FRESHNESS
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SAME-AGENT DISPATCHED FRESHNESS — DEMOTION: ONE connected agent dispatches the SAME tool TWICE.
    /// The first dispatch is allowed (the account is an Operator); the account is then demoted mid
    /// sequence, and the SECOND dispatch through that SAME agent is refused.
    /// <para>
    /// This is the model-facing path: both dispatches travel through the real SharpCoder guard, so
    /// the freshness is observed where the model sees it — not by reflecting on the gate.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SameAgentDispatchedFreshness_AnInSequenceDemotionRefusesTheSecondDispatch()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Operator, UserRole.Viewer);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        await SeedDraftGoalAsync(store);

        // The client dispatches approve_goal TWICE; between the two dispatches the test demotes the
        // account (the client signals when it has been asked for the second call).
        var secondDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var demoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "approve_goal", new Dictionary<string, object?> { ["id"] = "goal-a" })),
            Round(ToolCall("call-2", "approve_goal", new Dictionary<string, object?> { ["id"] = "goal-a" })),
            Round(Text("finished")))
        {
            BeforeRound = async round =>
            {
                // Round 1 is requested AFTER the first tool call executed and BEFORE the second one
                // is dispatched — exactly the window in which the change must take effect.
                if (round != 1) return;
                secondDispatch.TrySetResult();
                await demoted.Task;
            },
        };

        var composer = CreateComposer(provider, secondId, goalDb, chatClient: client);
        var service = AgentServiceOf(composer);

        var run = RunToCompletionAsync(service, "approve twice");

        await secondDispatch.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await _users.ChangeUserRoleAsync(secondId, UserRole.Viewer, Ct);
        demoted.TrySetResult();

        await run;

        var results = client.ToolResults();
        Assert.Equal(2, results.Count);

        // The FIRST dispatch was allowed…
        Assert.DoesNotContain("role does not permit", results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("was not executed", results[0], StringComparison.Ordinal);

        // …and the SECOND, through the SAME agent, was refused by the authorization boundary.
        Assert.Contains("approve_goal", results[1], StringComparison.Ordinal);
        Assert.Contains("role does not permit", results[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// SAME-AGENT DISPATCHED FRESHNESS — DISABLEMENT: ONE connected agent dispatches the same READ
    /// tool twice; the account is disabled between the two dispatches, and the second is refused.
    /// A read is a capability too, so this proves the freshness is not limited to mutating tools.
    /// </summary>
    [Fact]
    public async Task SameAgentDispatchedFreshness_AnInSequenceDisablementRefusesTheSecondDispatch()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Admin, UserRole.Viewer);
        using var provider = BuildProvider();

        var secondDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "read_file", new Dictionary<string, object?> { ["filePath"] = "sample.txt" })),
            Round(ToolCall("call-2", "read_file", new Dictionary<string, object?> { ["filePath"] = "sample.txt" })),
            Round(Text("finished")))
        {
            BeforeRound = async round =>
            {
                // Round 1 is requested AFTER the first tool call executed and BEFORE the second one
                // is dispatched — exactly the window in which the change must take effect.
                if (round != 1) return;
                secondDispatch.TrySetResult();
                await disabled.Task;
            },
        };

        var repoManager = new BrainRepoManager(_dir, NullLogger<BrainRepoManager>.Instance);
        await File.WriteAllTextAsync(
            Path.Combine(repoManager.WorkDirectory, "sample.txt"), "fixture content", Ct);

        var composer = CreateComposer(provider, secondId, CopilotHiveDbContext.CreateInMemory(),
            repoManager: repoManager, chatClient: client);
        var service = AgentServiceOf(composer);

        var run = RunToCompletionAsync(service, "read twice");

        await secondDispatch.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await _users.SetUserEnabledAsync(secondId, enabled: false, Ct);
        disabled.TrySetResult();

        await run;

        var results = client.ToolResults();
        Assert.Equal(2, results.Count);

        // The first read ran (its content reached the model)…
        Assert.Contains("fixture content", results[0], StringComparison.Ordinal);

        // …and the second was refused.
        Assert.Contains("read_file", results[1], StringComparison.Ordinal);
        Assert.Contains("host denied", results[1], StringComparison.Ordinal);
        Assert.DoesNotContain("fixture content", results[1], StringComparison.Ordinal);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // (b) DETERMINISTIC TWO-USER CONCURRENCY WITH A REAL OVERLAP
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// TWO USERS, A REAL OVERLAP: two separate agents for two accounts dispatch a tool at the SAME
    /// time — both are provably inside their dispatch simultaneously (a rendezvous, not an eager
    /// enumeration) — and each agent's answer belongs to its OWN account.
    /// <para>
    /// The accounts hold DIFFERENT roles, so a shared principal or a shared cached decision would
    /// make one agent answer with the other's role.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoUsersDispatchingConcurrently_KeepTheirGuardsIndependent()
    {
        var (_, secondId, thirdId) = await SeedAccountsAsync(UserRole.Operator, UserRole.Viewer);
        var gate = new GuardReadRendezvous(secondId, thirdId);
        using var firstDirectory = new MemoryDbFactory(_factory.ConnectionString, gate);
        using var secondDirectory = new MemoryDbFactory(_factory.ConnectionString, gate);
        using var firstProvider = BuildProvider(directory: new UserService(firstDirectory, NullLogger<UserService>.Instance));
        using var secondProvider = BuildProvider(directory: new UserService(secondDirectory, NullLogger<UserService>.Instance));
        var firstClient = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "approve_goal", new Dictionary<string, object?> { ["id"] = "goal-a" })),
            Round(Text("finished")));
        var secondClient = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "approve_goal", new Dictionary<string, object?> { ["id"] = "goal-a" })),
            Round(Text("finished")));
        using var firstDb = CopilotHiveDbContext.CreateInMemory();
        var firstStore = new GoalStore(firstDb, NullLogger<GoalStore>.Instance);
        await SeedDraftGoalAsync(firstStore);
        using var secondDb = CopilotHiveDbContext.CreateInMemory();
        var secondStore = new GoalStore(secondDb, NullLogger<GoalStore>.Instance);
        await SeedDraftGoalAsync(secondStore);
        var firstComposer = CreateComposer(firstProvider, secondId, firstDb, chatClient: firstClient);
        var secondComposer = CreateComposer(secondProvider, thirdId, secondDb, chatClient: secondClient);
        var firstService = AgentServiceOf(firstComposer);
        var secondService = AgentServiceOf(secondComposer);
        await firstService.ConnectAsync(Ct);
        await secondService.ConnectAsync(Ct);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task? firstRun = null;
        Task? secondRun = null;
        ExceptionDispatchInfo? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            // Arm AFTER construction/connect. Only SQL issued by dispatched authorization reads can
            // satisfy this rendezvous; parking the raw model requests cannot satisfy it.
            gate.Armed = true;
            firstRun = RunConnectedAgentAsync(firstService, "approve", lifetime.Token);
            secondRun = RunConnectedAgentAsync(secondService, "approve", lifetime.Token);
            await GuardWaitAsync(gate.BothReadsEntered.Task, "both users' fresh authorization SQL reads entered", Ct);
            Assert.Equal(2, gate.InFlight);
            var reads = gate.Snapshot();
            Assert.Equal(new[] { secondId, thirdId }, reads.Select(r => r.Key).Order().ToArray());
            Assert.Equal(2, reads.Select(r => r.Value).Distinct(ReferenceEqualityComparer.Instance).Count());
            Assert.NotSame(reads[0].Value, reads[1].Value);
            Assert.False(firstRun.IsCompleted);
            Assert.False(secondRun.IsCompleted);
            gate.Release.TrySetResult();
            await GuardWaitAsync(Task.WhenAll(firstRun, secondRun), "both dispatched tools complete", Ct);
            var firstResults = firstClient.ToolResults();
            Assert.Single(firstResults);
            Assert.DoesNotContain("role does not permit", firstResults[0], StringComparison.Ordinal);
            Assert.StartsWith("✅", firstResults[0]);
            Assert.Equal(GoalStatus.Pending, (await firstStore.GetGoalAsync("goal-a", Ct))!.Status);
            var secondResults = secondClient.ToolResults();
            Assert.Single(secondResults);
            Assert.Contains("approve_goal", secondResults[0], StringComparison.Ordinal);
            Assert.Contains("Tool 'approve_goal' was not executed: the host denied", secondResults[0], StringComparison.Ordinal);
            // With two successful, isolated directory reads the expected reason is capability denial.
            // Keep this prior assertion as well as the explicit wrapper; do not weaken it to an error.
            Assert.Contains("role does not permit", secondResults[0], StringComparison.Ordinal);
            Assert.Equal(GoalStatus.Draft, (await secondStore.GetGoalAsync("goal-a", Ct))!.Status);
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            // Release FIRST, then cancel, then boundedly observe EVERY owned run, even after a fault.
            gate.Armed = false;
            gate.Release.TrySetResult();
            await CleanupStepAsync(() => lifetime.CancelAsync(), cleanup);
            foreach (var run in new[] { firstRun, secondRun }.OfType<Task>())
                await CleanupStepAsync(() => ObserveRunAsync(run, "concurrent agent shutdown"), cleanup);
        }
        RethrowFailures(primary, cleanup);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // (c) EVERY PREVIOUSLY OMITTED GUARDED MUTATOR
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// extend_goal_iterations IS GUARDED: a Member's direct call is refused with the tool-named
    /// reason, and the goal is untouched.
    /// </summary>
    [Fact]
    public async Task ExtendGoalIterations_IsRefusedForAnAccountWithoutExecutionOperator()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Member, UserRole.Viewer);
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        var goal = await SeedDraftGoalAsync(store); // retain the separate draft preservation pin
        var (dispatcher, pipeline) = await EligibleResumeAsync(store);
        using (dispatcher)
        using (var provider = BuildProvider(services => services.AddSingleton(dispatcher)))
        {
            var composer = CreateComposer(provider, secondId, goalDb);
            var budget = pipeline.IterationBudget.Allowed;
            var iteration = pipeline.Iteration;
            var refusal = await composer.ExtendGoalIterationsAsync("eligible-goal", 5, Ct);
            Assert.Contains("extend_goal_iterations", refusal, StringComparison.Ordinal);
            Assert.Contains("role does not permit", refusal, StringComparison.Ordinal);
            var untouched = await store.GetGoalAsync("goal-a", Ct);
            Assert.NotNull(untouched);
            Assert.Equal(GoalStatus.Draft, untouched!.Status);
            Assert.Equal(goal.Iterations, untouched.Iterations);
            Assert.Equal(budget, pipeline.IterationBudget.Allowed);
            Assert.Equal(iteration, pipeline.Iteration);
            Assert.Equal(GoalPhase.Failed, pipeline.Phase);
            Assert.Equal(GoalStatus.Failed, (await store.GetGoalAsync("eligible-goal", Ct))!.Status);
        }
    }

    /// <summary>
    /// extend_goal_iterations ALLOWED CONTROL: an Operator's direct call is NOT refused by the
    /// authorization boundary (it proceeds to the tool's own logic).
    /// </summary>
    [Fact]
    public async Task ExtendGoalIterations_IsAllowedForAnOperator()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Operator, UserRole.Viewer);
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        var (dispatcher, pipeline) = await EligibleResumeAsync(store);
        using (dispatcher)
        using (var provider = BuildProvider(services => services.AddSingleton(dispatcher)))
        {
            var composer = CreateComposer(provider, secondId, goalDb);
            var budget = pipeline.IterationBudget.Allowed;
            Assert.Equal(0, pipeline.IterationBudget.Remaining);
            var result = await composer.ExtendGoalIterationsAsync("eligible-goal", 5, Ct);
            Assert.DoesNotContain("role does not permit", result, StringComparison.Ordinal);
            Assert.DoesNotContain("extend_goal_iterations", result, StringComparison.Ordinal);
            Assert.StartsWith("✅ Resumed failed goal", result);
            Assert.Equal(budget + 5, pipeline.IterationBudget.Allowed);
            Assert.Equal(4, pipeline.IterationBudget.Remaining); // top-up, then one resumed iteration
            Assert.Equal(GoalPhase.Coding, pipeline.Phase);
            var fresh = await store.GetGoalAsync("eligible-goal", Ct);
            Assert.Equal(GoalStatus.InProgress, fresh!.Status);
            Assert.Null(fresh.FailureReason);
            Assert.NotNull(pipeline.ActiveTaskId);
        }
    }

    /// <summary>
    /// review_goal IS GUARDED: a Member's direct call is refused, and an Operator's is allowed.
    /// </summary>
    [Fact]
    public async Task ReviewGoal_IsRefusedForAnAccountWithoutExecutionOperator_AndAllowedForAnOperator()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Member, UserRole.Viewer);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        await SeedDraftGoalAsync(store);
        var graph = new KnowledgeGraph();
        var requests = 0;
        var config = new HiveConfigFile
        {
            Workers = { ["reviewer"] = new WorkerConfig { Model = "reviewer-model" } },
        };
        var service = new GoalReviewService(graph, null, config, store, null, StateDir,
            NullLogger<GoalReviewService>.Instance, chatClientFactory: _ => new MockChatClient(messages =>
            {
                Interlocked.Increment(ref requests);
                Assert.Contains(messages, m => m.Text.Contains("A draft goal", StringComparison.Ordinal));
                return """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"Executed review control"}""";
            }));
        var composer = CreateComposer(provider, secondId, goalDb, goalReviewService: service);
        var refused = await composer.ReviewGoalAsync("goal-a", Ct);
        Assert.Contains("review_goal", refused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refused, StringComparison.Ordinal);
        Assert.Equal(0, requests);
        Assert.Equal(ReviewStatus.None, (await store.GetGoalAsync("goal-a", Ct))!.ReviewStatus);
        Assert.Null(graph.GetDocument("review-goal-a"));

        await _users.ChangeUserRoleAsync(secondId, UserRole.Operator, Ct);
        var allowed = await composer.ReviewGoalAsync("goal-a", Ct);
        Assert.DoesNotContain("role does not permit", allowed, StringComparison.Ordinal);
        Assert.DoesNotContain("review_goal", allowed, StringComparison.Ordinal);
        Assert.StartsWith("✅ Goal 'goal-a' has been reviewed and approved", allowed);
        Assert.Equal(1, requests);
        var fresh = await store.GetGoalAsync("goal-a", Ct);
        Assert.Equal(ReviewStatus.Approved, fresh!.ReviewStatus);
        Assert.Contains("review-goal-a", fresh.Documents);
        Assert.Contains("Executed review control", graph.GetDocument("review-goal-a")!.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// update_release IS GUARDED: a Member's direct call is refused and the release is untouched; an
    /// Operator's is allowed to proceed.
    /// </summary>
    [Fact]
    public async Task UpdateRelease_IsRefusedForAnAccountWithoutExecutionOperator_AndAllowedForAnOperator()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Member, UserRole.Viewer);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        await store.CreateReleaseAsync(new Release
        {
            Id = "v1.0.0",
            Tag = "1.0.0",
            Status = ReleaseStatus.Planning,
            CreatedAt = DateTime.UtcNow,
        }, Ct);

        var composer = CreateComposer(provider, secondId, goalDb);

        var refused = await composer.UpdateReleaseAsync("v1.0.0", "notes", "changed", Ct);
        Assert.Contains("update_release", refused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refused, StringComparison.Ordinal);
        // The release is untouched.
        Assert.Null((await store.GetReleaseAsync("v1.0.0", Ct))!.Notes);

        // ALLOWED CONTROL.
        await _users.ChangeUserRoleAsync(secondId, UserRole.Operator, Ct);
        var allowed = await composer.UpdateReleaseAsync("v1.0.0", "notes", "changed", Ct);
        Assert.DoesNotContain("role does not permit", allowed, StringComparison.Ordinal);
        Assert.Equal("changed", (await store.GetReleaseAsync("v1.0.0", Ct))!.Notes);
    }

    /// <summary>
    /// git_fetch IS GUARDED: a Member's direct call is refused before any git work happens; an
    /// Operator's is allowed to proceed.
    /// </summary>
    [Fact]
    public async Task GitFetch_IsRefusedForAnAccountWithoutExecutionOperator_AndAllowedForAnOperator()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Member, UserRole.Viewer);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var origin = Path.Combine(_dir, "fetch-source");
        await InitGitAsync(origin);
        await File.WriteAllTextAsync(Path.Combine(origin, "fetch.txt"), "fetched payload", Ct);
        await GitAsync(origin, "add", "--all");
        await GitAsync(origin, "commit", "-m", "fetch source control");
        var sourceSha = (await GitAsync(origin, "rev-parse", "HEAD")).Trim();
        var sourceRef = (await GitAsync(origin, "symbolic-ref", "HEAD")).Trim();
        var calls = new List<BrainGitRequest>();
        var repoManager = new BrainRepoManager(_dir, NullLogger<BrainRepoManager>.Instance,
            gitRunner: request =>
            {
                calls.Add(request);
                // Existing manager seam records the actual LOCAL fetch, then executes it against a
                // test-local origin. No network service, no push, and no fabricated success output.
                return RunLocalGitAsync(request.WorkingDirectory, request.Arguments.ToArray(), Ct).GetAwaiter().GetResult();
            });
        var clone = repoManager.GetClonePath("repo-a");
        await InitGitAsync(clone);
        await GitAsync(clone, "remote", "set-url", "origin", origin);
        var composer = CreateComposer(provider, secondId, goalDb, repoManager: repoManager);
        var refused = await composer.GitFetchAsync("repo-a", cancellationToken: Ct);
        Assert.Contains("git_fetch", refused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refused, StringComparison.Ordinal);
        Assert.Empty(calls);
        Assert.Empty((await GitAsync(clone, "for-each-ref", "--format=%(refname)", "refs/remotes/origin")).Trim());

        await _users.ChangeUserRoleAsync(secondId, UserRole.Operator, Ct);
        var allowed = await composer.GitFetchAsync("repo-a", cancellationToken: Ct);
        Assert.DoesNotContain("role does not permit", allowed, StringComparison.Ordinal);
        Assert.DoesNotContain("git_fetch", allowed, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", allowed, StringComparison.OrdinalIgnoreCase);
        var fetch = Assert.Single(calls, r => r.Arguments.Contains("fetch"));
        Assert.Equal(clone, fetch.WorkingDirectory);
        Assert.Equal(new[] { "fetch", "origin" }, fetch.Arguments);
        var tracking = "refs/remotes/origin/" + sourceRef["refs/heads/".Length..];
        Assert.Equal(sourceSha, (await GitAsync(clone, "rev-parse", tracking)).Trim());
        Assert.Equal("fetched payload", await GitAsync(clone, "show", tracking + ":fetch.txt"));
    }

    /// <summary>
    /// THE THREE CONFIG MUTATIONS ARE GUARDED: update_agents_md, edit_agents_md and
    /// commit_config_changes are each refused for an account without GlobalAdmin, and each is
    /// allowed for an Admin.
    /// </summary>
    [Fact]
    public async Task TheThreeConfigMutations_AreRefusedWithoutGlobalAdmin_AndAllowedForAnAdmin()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Operator, UserRole.Viewer);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var path = Path.Combine(_dir, "config-repo");
        Directory.CreateDirectory(Path.Combine(path, "agents"));
        var agentPath = Path.Combine(path, "agents", "coder.agents.md");
        await File.WriteAllTextAsync(agentPath, "old baseline", Ct);
        await InitGitAsync(path);
        await GitAsync(path, "add", "--all");
        await GitAsync(path, "commit", "-m", "baseline");
        var baseline = await GitAsync(path, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(path, "pending.txt"), "pending commit payload", Ct);

        // Execute real LOCAL git operations through the existing seam; network operations are
        // recorded but never launched. In particular no git push process is ever run by this test.
        var commands = new List<string[]>();
        var configRepo = new ConfigRepoManager("https://example.invalid/config.git", path)
        {
            GitRunner = async (workDir, args, ct) =>
            {
                commands.Add(args.ToArray());
                if (args[0] is "push" or "pull")
                    return new ConfigRepoManager.GitRunResult(0, "network operation recorded", "");
                var r = await RunLocalGitAsync(workDir, args, ct);
                return new ConfigRepoManager.GitRunResult(r.ExitCode, r.Stdout, r.Stderr);
            },
        };
        var composer = CreateComposer(provider, secondId, goalDb, configRepo: configRepo);

        var updateRefused = await composer.UpdateAgentsMdAsync("Coder", "content", Ct);
        Assert.Contains("update_agents_md", updateRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", updateRefused, StringComparison.Ordinal);
        Assert.Equal("old baseline", await File.ReadAllTextAsync(agentPath, Ct));
        var editRefused = await composer.EditAgentsMdAsync("Coder", "old", "new", Ct);
        Assert.Contains("edit_agents_md", editRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", editRefused, StringComparison.Ordinal);
        Assert.Equal("old baseline", await File.ReadAllTextAsync(agentPath, Ct));
        var commitRefused = await composer.CommitConfigChangesAsync("message", Ct);
        Assert.Contains("commit_config_changes", commitRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", commitRefused, StringComparison.Ordinal);
        Assert.Empty(commands);
        Assert.Equal(baseline, await GitAsync(path, "rev-parse", "HEAD"));

        await _users.ChangeUserRoleAsync(secondId, UserRole.Admin, Ct);
        var updateAllowed = await composer.UpdateAgentsMdAsync("Coder", "content", Ct);
        Assert.DoesNotContain("role does not permit", updateAllowed, StringComparison.Ordinal);
        Assert.Contains("✅", updateAllowed, StringComparison.Ordinal);
        Assert.True(File.Exists(agentPath));
        Assert.Equal("content", await File.ReadAllTextAsync(agentPath, Ct));
        var editAllowed = await composer.EditAgentsMdAsync("Coder", "content", "edited content", Ct);
        Assert.Contains("✅ Replacement applied", editAllowed, StringComparison.Ordinal);
        Assert.Equal("edited content", await File.ReadAllTextAsync(agentPath, Ct));
        var commitAllowed = await composer.CommitConfigChangesAsync("message", Ct);
        Assert.Contains("✅ Config repo changes committed", commitAllowed, StringComparison.Ordinal);
        Assert.Single(commands, a => a[0] == "commit");
        Assert.Contains(commands, a => a.SequenceEqual(new[] { "commit", "-m", "message" }));
        Assert.NotEqual(baseline, await GitAsync(path, "rev-parse", "HEAD"));
        Assert.Equal("message", (await GitAsync(path, "log", "-1", "--format=%s")).Trim());
        Assert.Equal("edited content", await GitAsync(path, "show", "HEAD:agents/coder.agents.md"));
        Assert.Equal("pending commit payload", await GitAsync(path, "show", "HEAD:pending.txt"));
    }

    /// <summary>
    /// THE FIVE KNOWLEDGE MUTATIONS ARE GUARDED: create_document, update_document, delete_document,
    /// link_document and unlink_document are each refused for an account without GlobalAdmin, and the
    /// knowledge corpus is untouched.
    /// </summary>
    [Fact]
    public async Task TheFiveKnowledgeMutations_AreRefusedWithoutGlobalAdmin_AndLeaveTheCorpusUntouched()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Admin, UserRole.Viewer);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var path = Path.Combine(_dir, "knowledge-repo");
        Directory.CreateDirectory(path);
        var graph = new KnowledgeGraph();
        var composer = CreateComposer(provider, secondId, goalDb,
            configRepo: new ConfigRepoManager("https://example.invalid/config.git", path), knowledgeGraph: graph);
        // Valid baselines: create both targets through the actual tool, derive IDs from its output,
        // and seed a link that unlink_document could really remove.
        var source = DocumentId(await composer.CreateDocumentAsync(
            "architecture", "doc-a", "Title", "implementation", "body", cancellationToken: Ct));
        var target = DocumentId(await composer.CreateDocumentAsync(
            "architecture", "doc-b", "Target", "implementation", "target body", cancellationToken: Ct));
        Assert.Contains("✅", await composer.LinkDocumentAsync(source, target, "related", "baseline link", Ct), StringComparison.Ordinal);
        Assert.Equal("body", (await ReloadGraphAsync(path)).GetDocument(source)!.Content);
        Assert.Single((await ReloadGraphAsync(path)).GetOutgoingLinks(source));
        await _users.ChangeUserRoleAsync(secondId, UserRole.Operator, Ct);

        var createRefused = await composer.CreateDocumentAsync(
            "architecture", "new-doc", "Title", "implementation", "body", cancellationToken: Ct);
        Assert.Contains("create_document", createRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", createRefused, StringComparison.Ordinal);
        Assert.Null((await ReloadGraphAsync(path)).GetDocument("architecture-new-doc"));
        var updateRefused = await composer.UpdateDocumentAsync(source, content: "new body", cancellationToken: Ct);
        Assert.Contains("update_document", updateRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", updateRefused, StringComparison.Ordinal);
        Assert.Equal("body", graph.GetDocument(source)!.Content);
        Assert.Equal("body", (await ReloadGraphAsync(path)).GetDocument(source)!.Content);
        var deleteRefused = await composer.DeleteDocumentAsync(source, cancellationToken: Ct);
        Assert.Contains("delete_document", deleteRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", deleteRefused, StringComparison.Ordinal);
        Assert.NotNull(graph.GetDocument(source));
        Assert.NotNull((await ReloadGraphAsync(path)).GetDocument(source));
        var linkRefused = await composer.LinkDocumentAsync(source, target, "references", "new link", Ct);
        Assert.Contains("link_document", linkRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", linkRefused, StringComparison.Ordinal);
        Assert.Single(graph.GetOutgoingLinks(source));
        var unlinkRefused = await composer.UnlinkDocumentAsync(source, target, "related", Ct);
        Assert.Contains("unlink_document", unlinkRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", unlinkRefused, StringComparison.Ordinal);
        Assert.Single(graph.GetOutgoingLinks(source));
        var fresh = await ReloadGraphAsync(path);
        Assert.Equal(2, fresh.GetAllDocuments().Count);
        var preserved = Assert.Single(fresh.GetOutgoingLinks(source));
        Assert.Equal(target, preserved.TargetId);
        Assert.Equal(LinkType.Related, preserved.Type);
        Assert.Equal("baseline link", preserved.Description);
    }

    /// <summary>
    /// THE FIVE KNOWLEDGE MUTATIONS ARE ALLOWED FOR AN ADMIN: each one proceeds past the boundary
    /// (the document is really created, so the update/link/unlink/delete calls have a target).
    /// </summary>
    [Fact]
    public async Task TheFiveKnowledgeMutations_AreAllowedForAnAdmin()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Admin, UserRole.Viewer);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var path = Path.Combine(_dir, "knowledge-repo");
        Directory.CreateDirectory(path);
        var knowledgeGraph = new KnowledgeGraph();
        var composer = CreateComposer(provider, secondId, goalDb,
            configRepo: new ConfigRepoManager("https://example.invalid/config.git", path), knowledgeGraph: knowledgeGraph);
        var created = await composer.CreateDocumentAsync(
            "architecture", "doc-a", "Title", "implementation", "body", cancellationToken: Ct);
        Assert.DoesNotContain("role does not permit", created, StringComparison.Ordinal);
        Assert.Single(knowledgeGraph.GetAllDocuments());
        var source = DocumentId(created);
        Assert.Equal("body", (await ReloadGraphAsync(path)).GetDocument(source)!.Content);
        var target = DocumentId(await composer.CreateDocumentAsync(
            "architecture", "doc-b", "Target", "implementation", "target body", cancellationToken: Ct));
        var updated = await composer.UpdateDocumentAsync(source, content: "new body", cancellationToken: Ct);
        Assert.DoesNotContain("role does not permit", updated, StringComparison.Ordinal);
        Assert.Contains("✅", updated, StringComparison.Ordinal);
        var fresh = await ReloadGraphAsync(path);
        Assert.Equal("new body", fresh.GetDocument(source)!.Content);
        Assert.Equal("Title", fresh.GetDocument(source)!.Title); // named content, not positional title
        var linked = await composer.LinkDocumentAsync(source, target, "related", "allowed link", Ct);
        Assert.Contains("✅ Link added", linked, StringComparison.Ordinal);
        var link = Assert.Single((await ReloadGraphAsync(path)).GetOutgoingLinks(source));
        Assert.Equal(target, link.TargetId);
        Assert.Equal(LinkType.Related, link.Type);
        Assert.Equal("allowed link", link.Description);
        var unlinked = await composer.UnlinkDocumentAsync(source, target, "related", Ct);
        Assert.Contains("✅ Link removed", unlinked, StringComparison.Ordinal);
        Assert.Empty((await ReloadGraphAsync(path)).GetOutgoingLinks(source));
        var deleted = await composer.DeleteDocumentAsync(source, cancellationToken: Ct);
        Assert.DoesNotContain("role does not permit", deleted, StringComparison.Ordinal);
        Assert.Contains("✅", deleted, StringComparison.Ordinal);
        Assert.Null(knowledgeGraph.GetDocument(source));
        fresh = await ReloadGraphAsync(path);
        Assert.Null(fresh.GetDocument(source));
        Assert.Equal(target, Assert.Single(fresh.GetAllDocuments()).Id);
    }

    /// <summary>
    /// create_goal IS GUARDED AT THE METHOD LEVEL: a Viewer's direct call is refused and NO goal row
    /// is written; a disabled account is refused the same way; and an ALLOWED Member control really
    /// persists a Draft goal.
    /// </summary>
    [Fact]
    public async Task CreateGoal_IsRefusedForAViewerAndADisabledAccount_AndAllowedForAMember()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Viewer, UserRole.Viewer);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);

        var composer = CreateComposer(provider, secondId, goalDb);

        // A VIEWER is refused, and nothing is written.
        var viewerRefused = await composer.CreateGoalAsync("new-goal", "A new goal", ct: Ct);
        Assert.Contains("create_goal", viewerRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", viewerRefused, StringComparison.Ordinal);
        Assert.Null(await store.GetGoalAsync("new-goal", Ct));

        // A DISABLED account is refused too, and nothing is written. A disabled account resolves to
        // NO principal at all, so its refusal is the "could not be verified" one — the point is that
        // it is refused and that NO row was written.
        await _users.ChangeUserRoleAsync(secondId, UserRole.Member, Ct);
        await _users.SetUserEnabledAsync(secondId, enabled: false, Ct);
        var disabledRefused = await composer.CreateGoalAsync("new-goal", "A new goal", ct: Ct);
        Assert.Contains("create_goal", disabledRefused, StringComparison.Ordinal);
        Assert.Contains("could not be verified", disabledRefused, StringComparison.Ordinal);
        Assert.Null(await store.GetGoalAsync("new-goal", Ct));

        // ALLOWED CONTROL: an enabled Member really persists a DRAFT goal.
        await _users.SetUserEnabledAsync(secondId, enabled: true, Ct);
        var allowed = await composer.CreateGoalAsync("new-goal", "A new goal", ct: Ct);
        Assert.DoesNotContain("role does not permit", allowed, StringComparison.Ordinal);
        Assert.Contains("✅", allowed, StringComparison.Ordinal);
        var persisted = await store.GetGoalAsync("new-goal", Ct);
        Assert.NotNull(persisted);
        Assert.Equal(GoalStatus.Draft, persisted!.Status);
    }

    /// <summary>
    /// create_issue IS GUARDED AT THE METHOD LEVEL: a Viewer's direct call is refused and NO issue
    /// row is written; a disabled account is refused the same way; and an ALLOWED Member control
    /// really persists an issue.
    /// </summary>
    [Fact]
    public async Task CreateIssue_IsRefusedForAViewerAndADisabledAccount_AndAllowedForAMember()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Viewer, UserRole.Viewer);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var issueStore = new RecordingIssueStore();

        var composer = CreateComposer(provider, secondId, goalDb, issueStore: issueStore);

        // A VIEWER is refused, and nothing is written.
        var viewerRefused = await composer.CreateIssueAsync("bug", "Title", "Body", ct: Ct);
        Assert.Contains("create_issue", viewerRefused, StringComparison.Ordinal);
        Assert.Contains("role does not permit", viewerRefused, StringComparison.Ordinal);
        Assert.Empty(issueStore.Created);

        // A DISABLED account is refused too, and nothing is written. A disabled account resolves to
        // NO principal at all, so its refusal is the "could not be verified" one — the point is that
        // it is refused and that NO row was written.
        await _users.ChangeUserRoleAsync(secondId, UserRole.Member, Ct);
        await _users.SetUserEnabledAsync(secondId, enabled: false, Ct);
        var disabledRefused = await composer.CreateIssueAsync("bug", "Title", "Body", ct: Ct);
        Assert.Contains("create_issue", disabledRefused, StringComparison.Ordinal);
        Assert.Contains("could not be verified", disabledRefused, StringComparison.Ordinal);
        Assert.Empty(issueStore.Created);

        // ALLOWED CONTROL: an enabled Member really persists an issue.
        await _users.SetUserEnabledAsync(secondId, enabled: true, Ct);
        var allowed = await composer.CreateIssueAsync("bug", "Title", "Body", ct: Ct);
        Assert.DoesNotContain("role does not permit", allowed, StringComparison.Ordinal);
        Assert.Single(issueStore.Created);
    }

    private static async Task RunConnectedAgentAsync(ComposerAgentService service, string prompt, CancellationToken ct)
    {
        var agent = service.Agent ?? throw new InvalidOperationException("connected agent missing");
        await foreach (var _ in agent.ExecuteStreamingAsync(SharpCoder.AgentSession.Create("composer"), prompt, ct)) { }
    }

    private static async Task GuardWaitAsync(Task task, string milestone, CancellationToken ct)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (TimeoutException ex) { throw new TimeoutException($"Timed out: {milestone}", ex); }
    }

    private static async Task ObserveRunAsync(Task task, string milestone)
    {
        try { await GuardWaitAsync(task, milestone, CancellationToken.None); }
        catch (OperationCanceledException) when (task.IsCanceled) { }
    }

    private static async Task CleanupStepAsync(Func<Task> step, List<Exception> errors)
    {
        try { await step(); }
        catch (Exception ex) { errors.Add(ex); }
    }

    private static void RethrowFailures(ExceptionDispatchInfo? primary, List<Exception> errors)
    {
        if (primary is not null)
        {
            if (errors.Count > 0) primary.SourceException.Data["CleanupFailures"] = new AggregateException(errors);
            primary.Throw();
        }
        if (errors.Count > 0) throw new AggregateException("Authorization fixture cleanup failed", errors);
    }

    private async Task<(GoalDispatcher Dispatcher, GoalPipeline Pipeline)> EligibleResumeAsync(GoalStore store)
    {
        var goal = new Goal
        {
            Id = "eligible-goal", Description = "Eligible resume control", Status = GoalStatus.Failed,
            FailureReason = "Exceeded max iterations", RepositoryNames = ["fixture-repo"],
        };
        await store.CreateGoalAsync(goal, Ct);
        var manager = new GoalPipelineManager();
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3, maxIterations: 3);
        while (pipeline.IterationBudget.TryConsume()) { }
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);
        var goals = new GoalManager();
        goals.AddSource(store);
        var config = new HiveConfigFile
        {
            Repositories = [new RepositoryConfig
            {
                Name = "fixture-repo", Url = "https://example.invalid/fixture.git", DefaultBranch = "fixture-default",
            }],
            Workers = { ["coder"] = new WorkerConfig { Model = "coder-model" } },
        };
        var dispatcher = new GoalDispatcher(goals, manager, new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()), new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance, new BrainRepoManager(_dir, NullLogger<BrainRepoManager>.Instance),
            new NoOpDistributedBrain(), config: config, goalStore: store);
        return (dispatcher, pipeline);
    }

    private static string DocumentId(string result)
    {
        Assert.StartsWith("✅ Document created:", result);
        return result.Split('\n').Single(line => line.StartsWith("- ID: ", StringComparison.Ordinal))[6..].Trim();
    }

    private static async Task<KnowledgeGraph> ReloadGraphAsync(string path)
    {
        var graph = new KnowledgeGraph();
        await graph.ReloadFromConfigRepoAsync(path, Ct);
        return graph;
    }

    private static async Task InitGitAsync(string path)
    {
        Directory.CreateDirectory(path);
        await GitAsync(path, "init");
        await GitAsync(path, "config", "user.name", "AuthorizationFixture");
        await GitAsync(path, "config", "user.email", "fixture@example.invalid");
        await GitAsync(path, "remote", "add", "origin", "https://example.invalid/config.git");
    }

    private static async Task<string> GitAsync(string path, params string[] args)
    {
        var result = await RunLocalGitAsync(path, args, Ct);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout;
    }

    private static async Task<BrainGitResult> RunLocalGitAsync(string path, string[] args, CancellationToken ct)
    {
        if (args.Any(a => a is "push" or "checkout" or "branch" or "switch"))
            throw new InvalidOperationException("Infrastructure-managed git command cannot be launched by this fixture.");
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        return new BrainGitResult(process.ExitCode, await stdout, await stderr);
    }

    // ── Helpers ──

    private static ComposerAgentService AgentServiceOf(Composer composer) =>
        (ComposerAgentService)typeof(Composer).GetField("_agentService", PrivateFlags)!.GetValue(composer)!;

    private static async Task RunToCompletionAsync(ComposerAgentService service, string prompt)
    {
        await service.ConnectAsync(Ct);
        var agent = service.Agent ?? throw new InvalidOperationException("The agent was not created.");
        var session = SharpCoder.AgentSession.Create("composer");
        await foreach (var _ in agent.ExecuteStreamingAsync(session, prompt, Ct)) { }
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
    /// A chat client scripted BY REQUEST NUMBER that records the messages of every request, and can
    /// hold each round at a rendezvous so a test can change the world mid-sequence.
    /// </summary>
    private sealed class ScriptedRoundsChatClient(
        params IReadOnlyList<ChatResponseUpdate>[] rounds) : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _requests = [];

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => _requests;

        public ChatClientMetadata Metadata => new("scripted-rounds", null, "scripted-rounds-model");

        /// <summary>
        /// An optional hook awaited BEFORE each round is served — a deterministic rendezvous seam, so a
        /// test can change the world (demote, disable) exactly between two dispatches. Never a sleep.
        /// </summary>
        public Func<int, Task>? BeforeRound { get; init; }

        private int _served;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var requestIndex = _requests.Count;
            _requests.Add(messages.ToList());

            if (Interlocked.Exchange(ref _served, 1) == 0 && BeforeRound is { } hook)
            {
                return AwaitHookThenRespondAsync(hook, requestIndex, cancellationToken);
            }

            return Task.FromResult(BuildResponse(requestIndex));
        }

        private async Task<ChatResponse> AwaitHookThenRespondAsync(
            Func<int, Task> hook, int requestIndex, CancellationToken cancellationToken)
        {
            await hook(requestIndex).WaitAsync(cancellationToken);
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

            var requestIndex = _requests.Count;
            _requests.Add(messages.ToList());

            if (requestIndex >= rounds.Length)
            {
                throw new InvalidOperationException(
                    $"Unscripted streaming round {requestIndex + 1}: the script has {rounds.Length} round(s).");
            }

            if (BeforeRound is { } hook)
            {
                await hook(requestIndex).WaitAsync(cancellationToken);
            }

            foreach (var update in rounds[requestIndex])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        /// <summary>
        /// The tool-result texts the model received, ONE PER TOOL CALL and in dispatch order.
        /// <para>
        /// The agent re-sends its accumulated messages, so the same result appears in more than one
        /// request; the results are therefore de-duplicated by their CALL ID (which is unique per
        /// dispatch) and returned in the order the calls were dispatched.
        /// </para>
        /// </summary>
        public IReadOnlyList<string> ToolResults()
        {
            // The requests are chronological, and within a request the results appear in dispatch
            // order, so the FIRST occurrence of each call id IS the dispatch order.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();

            foreach (var request in _requests)
            {
                foreach (var message in request)
                {
                    foreach (var content in message.Contents.OfType<FunctionResultContent>())
                    {
                        var key = content.CallId ?? string.Empty;
                        if (!seen.Add(key)) continue;
                        ordered.Add(content.Result?.ToString() ?? string.Empty);
                    }
                }
            }

            return ordered;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A chat client that must be disposed by the owner (the agent service) — a bare stub.</summary>
    private sealed class MockChatClient(Func<IEnumerable<ChatMessage>, string> responder) : IChatClient
    {
        public ChatClientMetadata Metadata => new("mock", null, "mock-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responder(messages))));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>An issue store that RECORDS every create and write, so a test can prove the guarded
    /// method never ran.</summary>
    private sealed class RecordingIssueStore : IIssueStore
    {
        public List<Issue> Created { get; } = [];

        public List<Issue> Updates { get; } = [];

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Created.Add(issue);
            return Task.FromResult(issue);
        }

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default) => Task.FromResult<Issue?>(null);

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Issue>>([]);

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Issue>>([]);

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Updates.Add(issue);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default) => Task.FromResult(true);
    }
}
