using System.Reflection;

using CopilotHive.Configuration;
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

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// ENFORCEMENT IS NOT HOOK-ONLY: every Composer-owned MUTATING tool method consults the SAME
/// per-runtime authorization gate the SharpCoder hook uses, so a call that reaches the method
/// directly (a future seam, or a test) is still refused BEFORE it can touch anything.
/// <para>
/// These cases call the tool methods directly — no CodingAgent, no hook, no model — so they prove the
/// METHOD-LEVEL enforcement independently of SharpCoder's dispatcher. That independence matters: a
/// hook-only implementation would leave these methods unguarded, and a method-only implementation
/// would leave the hook unwired.
/// </para>
/// </summary>
public sealed class ComposerMutatingToolMethodAuthorizationTests : IAsyncLifetime
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-methodauth-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerMutatingToolMethodAuthorizationTests()
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
            var name = $"methodauth-{Guid.NewGuid():N}";
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
        services.AddSingleton(new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "test-model" }] },
            Composer = new ComposerConfig { Model = "test-model" },
        });
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private Composer CreateComposer(
        ServiceProvider provider, int userId, CopilotHiveDbContext goalDb, IIssueStore? issueStore = null)
    {
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => new MockChatClient(_ => "unused"),
            issueStore: issueStore,
            userState: StateOf(userId),
            principal: Resolve(userId));
        _composers.Add(composer);
        return composer;
    }

    // ── The method-level enforcement is present on every mutating tool method ──

    /// <summary>
    /// EVERY MUTATING TOOL METHOD IS REFUSED BEFORE ITS SIDE EFFECT when the account's role does not
    /// hold the capability — and the refusal names the tool, so the model can tell what happened.
    /// <para>
    /// The store is observed AFTER the call: an untouched store proves the method never reached the
    /// mutation, so this is a "denied BEFORE the side effect" proof and not merely a refusal message.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EveryMutatingToolMethod_IsRefusedBeforeItsSideEffect()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        await store.CreateGoalAsync(new Goal
        {
            Id = "goal-a",
            Description = "A draft goal",
            Status = GoalStatus.Draft,
            CreatedAt = DateTime.UtcNow,
        }, Ct);

        var issueStore = new RecordingIssueStore();
        var composer = CreateComposer(provider, secondId, goalDb, issueStore);

        // approve_goal — the goal must stay a Draft.
        var approve = await composer.ApproveGoalAsync("goal-a", ct: Ct);
        Assert.Contains("approve_goal", approve, StringComparison.Ordinal);
        Assert.Contains("role does not permit", approve, StringComparison.Ordinal);
        Assert.Equal(GoalStatus.Draft, (await store.GetGoalAsync("goal-a", Ct))!.Status);

        // cancel_goal — the goal must stay a Draft.
        var cancel = await composer.CancelGoalAsync("goal-a", ct: Ct);
        Assert.Contains("cancel_goal", cancel, StringComparison.Ordinal);
        Assert.Equal(GoalStatus.Draft, (await store.GetGoalAsync("goal-a", Ct))!.Status);

        // delete_goal — the goal must still exist.
        var delete = await composer.DeleteGoalAsync("goal-a", ct: Ct);
        Assert.Contains("delete_goal", delete, StringComparison.Ordinal);
        Assert.NotNull(await store.GetGoalAsync("goal-a", Ct));

        // update_goal — the description must be untouched.
        var update = await composer.UpdateGoalAsync("goal-a", "description", "rewritten", ct: Ct);
        Assert.Contains("update_goal", update, StringComparison.Ordinal);
        Assert.Equal("A draft goal", (await store.GetGoalAsync("goal-a", Ct))!.Description);

        // create_release — no release may have been created.
        var release = await composer.CreateReleaseAsync("v1.0.0", "1.0.0", ct: Ct);
        Assert.Contains("create_release", release, StringComparison.Ordinal);
        Assert.Null(await store.GetReleaseAsync("v1.0.0", Ct));

        // update_issue — the issue store must be untouched.
        var issue = await composer.UpdateIssueAsync("issue-a", status: "closed", ct: Ct);
        Assert.Contains("update_issue", issue, StringComparison.Ordinal);
        Assert.Empty(issueStore.Updates);
    }

    /// <summary>
    /// THE METHOD-LEVEL ENFORCEMENT IS OBSERVABLE IN ITS OWN RIGHT: a call that BYPASSES the hook is
    /// still refused. This is what makes the enforcement not hook-only — with the hook removed from
    /// the picture entirely (no agent, no model), the method still refuses.
    /// </summary>
    [Fact]
    public async Task TheMethodLevelEnforcement_RefusesEvenWithNoAgentAndNoHook()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        await store.CreateGoalAsync(new Goal
        {
            Id = "goal-a",
            Description = "A draft goal",
            Status = GoalStatus.Draft,
            CreatedAt = DateTime.UtcNow,
        }, Ct);

        var composer = CreateComposer(provider, secondId, goalDb);

        // The Composer was never connected: there is no CodingAgent and therefore no hook at all.
        Assert.False(composer.IsConnected);

        var refusal = await composer.ApproveGoalAsync("goal-a", ct: Ct);

        Assert.Contains("approve_goal", refusal, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refusal, StringComparison.Ordinal);
        Assert.Equal(GoalStatus.Draft, (await store.GetGoalAsync("goal-a", Ct))!.Status);
    }

    /// <summary>
    /// THE METHOD-LEVEL ENFORCEMENT IS FRESH TOO: after a demotion the SAME Composer's direct call is
    /// refused on the next invocation, with no recreation and no cached decision.
    /// </summary>
    [Fact]
    public async Task TheMethodLevelEnforcement_ObservesADemotionOnTheNextInvocation()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        await store.CreateGoalAsync(new Goal
        {
            Id = "goal-a",
            Description = "A draft goal",
            Status = GoalStatus.Draft,
            CreatedAt = DateTime.UtcNow,
        }, Ct);

        var composer = CreateComposer(provider, secondId, goalDb);

        Assert.Contains("✅", await composer.UpdateGoalAsync("goal-a", "description", "first update", ct: Ct), StringComparison.Ordinal);

        await _users.ChangeUserRoleAsync(secondId, UserRole.Viewer, Ct);

        var refused = await composer.UpdateGoalAsync("goal-a", "description", "second update", ct: Ct);
        Assert.Contains("role does not permit", refused, StringComparison.Ordinal);
        Assert.Equal("first update", (await store.GetGoalAsync("goal-a", Ct))!.Description);
    }

    /// <summary>
    /// THE METHOD-LEVEL <c>update_goal</c> RULE MATCHES THE HOOK'S: an unsupported field is refused
    /// outright — before the capability is even considered — and a supported field passes to the
    /// capability check, so neither enforcement point is weaker than the other.
    /// </summary>
    [Fact]
    public async Task TheMethodLevelUpdateGoalRule_MatchesTheHooksRule()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        await store.CreateGoalAsync(new Goal
        {
            Id = "goal-a",
            Description = "A draft goal",
            Status = GoalStatus.Draft,
            CreatedAt = DateTime.UtcNow,
        }, Ct);

        var composer = CreateComposer(provider, secondId, goalDb);

        // An unsupported field is refused and the goal is untouched.
        var unsupported = await composer.UpdateGoalAsync("goal-a", "failure_reason", "rewritten", ct: Ct);
        Assert.Contains("cannot be updated through update_goal", unsupported, StringComparison.Ordinal);
        Assert.Null((await store.GetGoalAsync("goal-a", Ct))!.FailureReason);

        // A supported field the account's role holds proceeds.
        var supported = await composer.UpdateGoalAsync("goal-a", "description", "updated description", ct: Ct);
        Assert.Contains("✅", supported, StringComparison.Ordinal);
        Assert.Equal("updated description", (await store.GetGoalAsync("goal-a", Ct))!.Description);
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

    /// <summary>An issue store that RECORDS every write, so a test can prove the method never ran.</summary>
    private sealed class RecordingIssueStore : IIssueStore
    {
        public List<Issue> Updates { get; } = [];

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default) => Task.FromResult(issue);

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
