using System.Reflection;
using System.Runtime.CompilerServices;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
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

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Covers the CENTRAL Composer tool-authorization helper, the identity-bound PRINCIPAL-MATCH enforcement,
/// and the wiring of the delivered SharpCoder 0.23.0 tool-authorization hook
/// (<see cref="AgentOptions.AuthorizeTool"/> + <see cref="AgentOptions.ToolAuthorizationCorrelationId"/>).
/// <para>
/// Every authorization answer is produced by the REAL <see cref="HivePrincipalResolver"/> over the REAL
/// <see cref="UserService"/> over an in-memory SQLite database: the directory is never mocked, so the
/// freshness and fail-closed properties are proven against current stored state.
/// </para>
/// <para>
/// The hook cases drive a real <see cref="CodingAgent"/> built by the real
/// <see cref="ComposerAgentService"/> over a scripted streaming client, so the tool call travels through
/// SharpCoder's own guard — an allowed call runs, a denied call is refused BEFORE it can produce its side
/// effect. These are the INITIAL self-check cases; the deeper acceptance matrix belongs to the Tester phase.
/// </para>
/// </summary>
public sealed class ComposerToolAuthorizationTests : IAsyncLifetime
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-toolauth-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users = null!;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerToolAuthorizationTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(StateDir); // the agent's WorkDirectory must exist
        _factory = new MemoryDbFactory();
        _users = new UserService(_factory, NullLogger<UserService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    // ── Directory fixture ──

    /// <summary>Separate connections share a uniquely named in-memory database kept open by one holder.</summary>
    private sealed class MemoryDbFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        public MemoryDbFactory()
        {
            var name = $"toolauth-{Guid.NewGuid():N}";
            _keeper = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
            _keeper.Open();
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_keeper).Options;
            using var ctx = new CopilotHiveDbContext(_options);
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);

        public void Dispose() => _keeper.Dispose();
    }

    private string StateDir => Path.Combine(_dir, "state");

    /// <summary>Seeds the original account (id 1) and one activated invited account (id 2) with the given role.</summary>
    private async Task<(int OriginalId, int SecondId)> SeedAccountsAsync(UserRole secondRole)
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "original", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", secondRole, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        return (original.Id, second.Id);
    }

    /// <summary>Resolves an activated account from CURRENT stored state through the production resolver.</summary>
    private ActingUserPrincipal Resolve(int userId)
    {
        var resolver = new HivePrincipalResolver(_users, HiveAuthMode.AuthenticationEnabled, NullLogger<HivePrincipalResolver>.Instance);
        var resolution = resolver.ResolveByUserIdAsync(userId, Ct).GetAwaiter().GetResult();
        Assert.True(resolution.IsResolved, $"Account {userId} did not resolve: {resolution.RefusalReason}.");
        return resolution.Principal!;
    }

    private ComposerUserState StateOf(int userId) =>
        ComposerUserState.ResolveAsync(_users, userId, Ct).GetAwaiter().GetResult();

    /// <summary>Builds a container with the production security primitives over THIS fixture's directory.</summary>
    private ServiceProvider BuildProvider(HiveAuthMode? mode = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_users);
        services.AddSingleton(mode ?? HiveAuthMode.AuthenticationEnabled);
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

    private ComposerToolAuthorization Helper(ServiceProvider provider) =>
        ComposerToolAuthorization.FromServiceProvider(provider)
        ?? throw new InvalidOperationException("The fixture container must expose the security primitives.");

    /// <summary>Creates an identity-bound Composer over the fixture's security primitives.</summary>
    private Composer CreateBoundComposer(
        ServiceProvider provider, int userId, ActingUserPrincipal? principal,
        CopilotHiveDbContext? goalDb = null)
    {
        var store = new GoalStore(goalDb ?? CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => new MockChatClient(_ => "unused"),
            userState: StateOf(userId),
            principal: principal);
        _composers.Add(composer);
        return composer;
    }

    // ── (1) Identity-bound principal-match enforcement ──

    /// <summary>
    /// An identity-bound Composer WITHOUT a principal is rejected: there is no legacy-admin fallback and no
    /// silent proceed, so a per-user runtime can never run unauthenticated.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_WithoutPrincipal_IsRejected()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();

        var ex = Assert.Throws<ArgumentException>(() => CreateBoundComposer(provider, secondId, principal: null));

        Assert.Equal("principal", ex.ParamName);
        Assert.Contains("no principal was supplied", ex.Message, StringComparison.Ordinal);
        // The message names the identity it refused, so an operator can tell WHICH runtime was refused.
        Assert.Contains(secondId.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A principal of a DIFFERENT account is rejected: one user's state can never be driven by another
    /// user's authority.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_WithAnotherAccountsPrincipal_IsRejected()
    {
        var (originalId, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();

        var ex = Assert.Throws<ArgumentException>(
            () => CreateBoundComposer(provider, secondId, Resolve(originalId)));

        Assert.Equal("principal", ex.ParamName);
        Assert.Contains("does not match the Composer's identity", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An OPEN-MODE local-operator principal is rejected for identity-bound state: open mode has no
    /// persisted account, and an identity-bound Composer exists only for one.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_WithLocalOperatorPrincipal_IsRejected()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();

        var ex = Assert.Throws<ArgumentException>(
            () => CreateBoundComposer(provider, secondId, ActingUserPrincipal.LocalOperator()));

        Assert.Equal("principal", ex.ParamName);
        Assert.Contains("open-mode local operator", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The MATCHING resolver-produced principal is accepted, and the legacy (userState == null) construction
    /// keeps working WITHOUT one — the only place an absent principal is legitimate.
    /// </summary>
    [Fact]
    public async Task MatchingPrincipal_IsAccepted_AndLegacyConstructionNeedsNone()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();

        await using var bound = CreateBoundComposer(provider, secondId, Resolve(secondId));
        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        await using var legacy = new Composer(
            "test-model", NullLogger<Composer>.Instance, store, stateDir: StateDir, serviceProvider: provider);

        Assert.NotNull(bound);
        Assert.NotNull(legacy);
    }

    // ── (2) The tool map: complete, default-deny, and the disabled shape ──

    /// <summary>
    /// EVERY tool name the Composer registers is mapped, so no legitimate call is a default-deny. The names
    /// are enumerated from the Composer's OWN registered tool list, so a tool added later without a map entry
    /// fails here instead of silently becoming an authorization hole.
    /// </summary>
    [Fact]
    public async Task EveryRegisteredComposerTool_IsMappedToACapability()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();

        // A fully-equipped Composer: config repo, knowledge graph, issue store and web tools present, so the
        // OPTIONAL tool groups are registered too and their names are part of the census.
        var composer = CreateFullyEquippedComposer(provider, secondId);

        var registered = RegisteredToolNames(composer).ToArray();

        // The four optional groups really are represented — the census is not vacuous.
        Assert.Contains("web_search", registered, StringComparer.Ordinal);
        Assert.Contains("update_agents_md", registered, StringComparer.Ordinal);
        Assert.Contains("create_document", registered, StringComparer.Ordinal);
        Assert.Contains("update_issue", registered, StringComparer.Ordinal);

        var unmapped = registered.Where(name => ComposerToolAuthorization.CapabilityFor(name) is null).ToArray();
        Assert.True(unmapped.Length == 0,
            "These registered Composer tools have no capability mapping (they would default-deny): "
            + string.Join(", ", unmapped));
    }

    /// <summary>
    /// DEFAULT DENY: a name that is not in the map is refused, with model-visible text, for an account that
    /// would otherwise hold every capability.
    /// </summary>
    [Fact]
    public async Task UnknownToolName_IsDenied_EvenForAnAdmin()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();
        var helper = Helper(provider);

        var result = await helper.AuthorizeAsync(secondId, "definitely_not_a_composer_tool", arguments: null, Ct);

        Assert.False(result.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(result.DenialReason));
        Assert.Contains("not an available Composer tool", result.DenialReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The DISABLED SHAPE: write_file, edit_file, bash and the skill tools are never part of the Composer
    /// surface. They are absent from the registered tool list, absent from the map (so a stray call
    /// default-denies), and the Composer's agent is constructed with those facilities DISABLED.
    /// </summary>
    /// <param name="toolName">A tool outside the Composer surface.</param>
    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("execute_bash_command")]
    [InlineData("load_skill")]
    [InlineData("list_skills")]
    public async Task ToolsOutsideTheComposerSurface_AreDisabledAndUnmapped(string toolName)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();
        var composer = CreateFullyEquippedComposer(provider, secondId);

        // Not registered on the Composer's CodingAgent…
        Assert.DoesNotContain(toolName, RegisteredToolNames(composer), StringComparer.Ordinal);
        // …not mapped (so a stray call default-denies)…
        Assert.Null(ComposerToolAuthorization.CapabilityFor(toolName));
        // …and the agent itself is built with those facilities switched off. Connect first: the options
        // object only exists once a CodingAgent has been constructed.
        var service = AgentServiceOf(composer);
        await service.ConnectAsync(Ct);
        var options = service.AgentOptions;
        Assert.False(options.EnableBash);
        Assert.False(options.EnableFileWrites);
        Assert.False(options.EnableSkills);

        var result = await Helper(provider).AuthorizeAsync(secondId, toolName, arguments: null, Ct);
        Assert.False(result.IsAllowed);
    }

    // ── (3) Capability evaluation, default-deny fields, and freshness ──

    /// <summary>
    /// ENFORCEMENT IS NOT HOOK-ONLY: calling a Composer-owned MUTATING tool method directly (bypassing
    /// SharpCoder's dispatcher entirely) is still refused before it can touch the store, because the method
    /// consults the same per-runtime gate. The Viewer's <c>approve_goal</c> leaves the goal a Draft.
    /// </summary>
    [Fact]
    public async Task DirectMutatingToolCall_IsRefusedWithoutTheHook()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Viewer);
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

        var composer = CreateBoundComposer(provider, secondId, Resolve(secondId), goalDb);

        // Direct call — no CodingAgent, no hook, no model.
        var refusal = await composer.ApproveGoalAsync("goal-a", ct: Ct);

        Assert.Contains("approve_goal", refusal, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refusal, StringComparison.Ordinal);

        var goal = await store.GetGoalAsync("goal-a", Ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalStatus.Draft, goal!.Status);
    }

    /// <summary>
    /// THE METHOD-LEVEL <c>update_goal</c> RULE matches the hook: an unsupported field is refused outright, and
    /// a supported field passes to the capability check — so neither enforcement point is weaker.
    /// </summary>
    [Fact]
    public async Task DirectUpdateGoalCall_AppliesTheSameFieldRule()
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

        var composer = CreateBoundComposer(provider, secondId, Resolve(secondId), goalDb);

        // An unsupported field is refused, and the goal is untouched.
        var unsupported = await composer.UpdateGoalAsync("goal-a", "failure_reason", "rewritten", ct: Ct);
        Assert.Contains("cannot be updated through update_goal", unsupported, StringComparison.Ordinal);
        Assert.Null((await store.GetGoalAsync("goal-a", Ct))!.FailureReason);

        // A supported field the account's role holds proceeds.
        var supported = await composer.UpdateGoalAsync("goal-a", "description", "updated description", ct: Ct);
        Assert.Contains("✅", supported, StringComparison.Ordinal);
        Assert.Equal("updated description", (await store.GetGoalAsync("goal-a", Ct))!.Description);
    }

    /// <summary>
    /// THE METHOD-LEVEL RULE ALSO TRACKS FRESHNESS: after a demotion the SAME Composer's direct mutating call
    /// is refused on the next invocation, with no recreation and no cached decision.
    /// </summary>
    [Fact]
    public async Task DirectMutatingToolCall_ObservesADemotionOnTheNextInvocation()
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

        var composer = CreateBoundComposer(provider, secondId, Resolve(secondId), goalDb);

        Assert.Contains("✅", await composer.UpdateGoalAsync("goal-a", "description", "first update", ct: Ct), StringComparison.Ordinal);

        await _users.ChangeUserRoleAsync(secondId, UserRole.Viewer, Ct);

        var refused = await composer.UpdateGoalAsync("goal-a", "description", "second update", ct: Ct);
        Assert.Contains("role does not permit", refused, StringComparison.Ordinal);
        Assert.Equal("first update", (await store.GetGoalAsync("goal-a", Ct))!.Description);
    }

    /// <summary>
    /// ONE row per capability band: the minimum role holds the tool, and the role immediately below does not.
    /// This makes every mapping's threshold load-bearing instead of incidental.
    /// </summary>
    /// <param name="toolName">The mapped tool name.</param>
    /// <param name="minimumRole">The role that holds it.</param>
    /// <param name="belowRole">The role immediately below, or <c>null</c> when none exists.</param>
    [Theory]
    [InlineData("list_goals", UserRole.Viewer, null)]
    [InlineData("get_current_time", UserRole.Member, UserRole.Viewer)]
    [InlineData("approve_goal", UserRole.Operator, UserRole.Member)]
    [InlineData("update_agents_md", UserRole.Admin, UserRole.Operator)]
    [InlineData("read_file", UserRole.Viewer, null)]
    [InlineData("start_sub_agent", UserRole.Member, UserRole.Viewer)]
    [InlineData("git_fetch", UserRole.Operator, UserRole.Member)]
    [InlineData("create_document", UserRole.Admin, UserRole.Operator)]
    public async Task EveryToolName_IsGrantedAtItsMinimumRole_AndDeniedOneRoleBelow(
        string toolName, UserRole minimumRole, UserRole? belowRole)
    {
        using var directory = new HiveSecurityTestDirectoryForRoles();
        var helper = directory.Helper();

        Assert.True(await directory.AuthorizeAsRoleAsync(helper, minimumRole, toolName, Ct),
            $"'{toolName}' must be allowed for {minimumRole}.");

        if (belowRole is { } below)
        {
            Assert.False(await directory.AuthorizeAsRoleAsync(helper, below, toolName, Ct),
                $"'{toolName}' must be denied for {below}.");
        }
    }

    /// <summary>
    /// <c>update_goal</c> with a SUPPORTED field requires ExecutionOperator; with an UNSUPPORTED field it is
    /// denied outright — even for an Admin, and even before the capability is considered.
    /// </summary>
    /// <param name="field">The field argument of the call.</param>
    [Theory]
    [InlineData("description")]
    [InlineData("priority")]
    [InlineData("scope")]
    [InlineData("repositories")]
    [InlineData("target_repositories")]
    [InlineData("depends_on")]
    [InlineData("documents")]
    [InlineData("status")]
    [InlineData("release")]
    public async Task UpdateGoal_WithASupportedField_RequiresExecutionOperator(string field)
    {
        using var directory = new HiveSecurityTestDirectoryForRoles();
        var helper = directory.Helper();
        var arguments = new Dictionary<string, object?> { ["id"] = "goal-a", ["field"] = field, ["value"] = "x" };

        Assert.True(await directory.AuthorizeAsRoleAsync(helper, UserRole.Operator, "update_goal", arguments, Ct));
        Assert.False(await directory.AuthorizeAsRoleAsync(helper, UserRole.Member, "update_goal", arguments, Ct));
        // A VIEWER is below Member too — the row above is a threshold, not a two-role coincidence.
        Assert.False(await directory.AuthorizeAsRoleAsync(helper, UserRole.Viewer, "update_goal", arguments, Ct));
    }

    /// <summary>
    /// <c>update_goal</c> with ANY unsupported field defaults to DENY — including for an Admin who holds
    /// every capability, so the rule can never be reached through a role.
    /// </summary>
    /// <param name="field">The unsupported field argument.</param>
    [Theory]
    [InlineData("id")]
    [InlineData("failure_reason")]
    [InlineData("iterations")]
    [InlineData("created_at")]
    [InlineData("")]
    public async Task UpdateGoal_WithAnUnsupportedField_IsDeniedEvenForAnAdmin(string field)
    {
        using var directory = new HiveSecurityTestDirectoryForRoles();
        var helper = directory.Helper();
        var arguments = new Dictionary<string, object?> { ["id"] = "goal-a", ["field"] = field, ["value"] = "x" };

        var result = await directory.AuthorizeResultAsRoleAsync(helper, UserRole.Admin, "update_goal", arguments, Ct);

        Assert.False(result.IsAllowed);
        Assert.Contains("cannot be updated through update_goal", result.DenialReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// FRESHNESS: a demotion between two calls takes effect on the NEXT call through the SAME helper and the
    /// same bound identity — no stale authorization truth is cached anywhere.
    /// </summary>
    [Fact]
    public async Task ADemotionBetweenCalls_TakesEffectOnTheNextCall()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();
        var helper = Helper(provider);
        var gate = helper.Bind(secondId);

        Assert.True((await helper.AuthorizeAsync(secondId, "approve_goal", arguments: null, Ct)).IsAllowed);

        await _users.ChangeUserRoleAsync(secondId, UserRole.Viewer, Ct);

        Assert.False((await helper.AuthorizeAsync(secondId, "approve_goal", arguments: null, Ct)).IsAllowed);
        // The bound gate (what the hook uses) observes the same change, because a gate stores no decision.
        Assert.False((await gate.AuthorizeToolAsync(Context("approve_goal"), Ct)).IsAllowed);
    }

    /// <summary>
    /// A DISABLED account denies every tool on the next call, including a read it was previously allowed —
    /// and a RE-ENABLE restores it, proving the answer is derived from stored state, not latched.
    /// </summary>
    [Fact]
    public async Task ADisableAndReEnableBetweenCalls_TakeEffectOnTheNextCall()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();
        var helper = Helper(provider);

        Assert.True((await helper.AuthorizeAsync(secondId, "list_goals", arguments: null, Ct)).IsAllowed);

        await _users.SetUserEnabledAsync(secondId, enabled: false, Ct);
        Assert.False((await helper.AuthorizeAsync(secondId, "list_goals", arguments: null, Ct)).IsAllowed);

        await _users.SetUserEnabledAsync(secondId, enabled: true, Ct);
        Assert.True((await helper.AuthorizeAsync(secondId, "list_goals", arguments: null, Ct)).IsAllowed);
    }

    /// <summary>
    /// FAIL CLOSED on an unreadable directory: the check denies rather than allowing, and never throws.
    /// </summary>
    [Fact]
    public async Task AnUnreadableDirectory_DeniesInsteadOfAllowing()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        var provider = BuildProvider();
        var helper = Helper(provider);

        // Dispose the directory AFTER the helper captured it: every later read fails.
        _factory.Dispose();

        var result = await helper.AuthorizeAsync(secondId, "list_goals", arguments: null, Ct);

        Assert.False(result.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(result.DenialReason));
        await provider.DisposeAsync();
    }

    /// <summary>
    /// The LEGACY runtime's identity input is re-resolved per invocation: in open mode it is the explicit local
    /// operator (allowed everything), and while authentication is enabled it is the ORIGINAL account — so
    /// disabling that account denies at once with no fallback.
    /// </summary>
    [Fact]
    public async Task LegacyRuntime_ResolvesOpenModeOrTheOriginalAccountPerInvocation()
    {
        // A second ENABLED Admin, because UserService refuses to disable the last one. The bootstrap
        // account is still the original (lowest id), so the legacy runtime resolves exactly it.
        var (originalId, _) = await SeedAccountsAsync(UserRole.Admin);

        using var openProvider = BuildProvider(HiveAuthMode.Open);
        var openHelper = Helper(openProvider);
        Assert.True((await openHelper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct)).IsAllowed);

        using var authProvider = BuildProvider(HiveAuthMode.AuthenticationEnabled);
        var authHelper = Helper(authProvider);
        // The original account was seeded as Admin, so an Admin-only tool is allowed…
        Assert.True((await authHelper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct)).IsAllowed);

        // …and disabling it denies immediately, because the bootstrap id is re-read every call.
        await _users.SetUserEnabledAsync(originalId, enabled: false, Ct);
        Assert.False((await authHelper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct)).IsAllowed);
    }

    // ── (4) The delivered SharpCoder hook, end to end ──

    /// <summary>
    /// AN ALLOWED TOOL CALL PROCEEDS through the real hook: the model's <c>get_current_time</c> call actually
    /// runs, and its result reaches the model on the next round.
    /// </summary>
    [Fact]
    public async Task AllowedToolCall_ProceedsThroughTheDeliveredHook()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();
        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "get_current_time", new Dictionary<string, object?>())),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, Resolve(secondId), out var service, client);

        await RunToCompletionAsync(service, "what time is it?");

        var results = ToolResults(client);
        Assert.Single(results);
        Assert.Contains("iso", results[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A DENIED TOOL CALL IS REFUSED BEFORE ITS SIDE EFFECT: a Viewer's <c>approve_goal</c> never flips the
    /// goal's status, and the model is told why on the next round.
    /// </summary>
    [Fact]
    public async Task DeniedToolCall_IsRefusedBeforeItsSideEffect()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Viewer);
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

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "approve_goal", new Dictionary<string, object?> { ["id"] = "goal-a" })),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, Resolve(secondId), out var service, client, goalDb);

        await RunToCompletionAsync(service, "approve goal-a");

        // NO SIDE EFFECT: the goal is still a Draft.
        var goal = await store.GetGoalAsync("goal-a", Ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalStatus.Draft, goal!.Status);

        // The refusal is model-visible, and it is the host-authored reason — not a stack trace.
        var refusal = Assert.Single(ToolResults(client));
        Assert.Contains("approve_goal", refusal, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// AN UNKNOWN TOOL NAME IS DENIED THROUGH THE HOOK. The Composer itself never registers an unmapped tool
    /// (<see cref="EveryRegisteredComposerTool_IsMappedToACapability"/> pins that), so this case registers one
    /// deliberately: a tool that IS on the model's list but has no capability mapping must still be refused by
    /// the hook — and it must be refused BEFORE its side effect, which is what the side-effect flag proves.
    /// </summary>
    [Fact]
    public async Task UnknownToolName_IsDeniedThroughTheHook()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();

        var executed = false;
        var unmapped = AIFunctionFactory.Create(
            () =>
            {
                executed = true;
                return "the unmapped tool ran";
            },
            "definitely_not_a_composer_tool",
            "A tool that has no capability mapping.");

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "definitely_not_a_composer_tool", new Dictionary<string, object?>())),
            Round(Text("finished")));

        var service = new ComposerAgentService(
            "test-model", 64_000, 50, null, null, "system prompt", [unmapped], null, StateDir,
            null, NullLogger<ComposerAgentService>.Instance, _ => client,
            null, ["test-model"], null, null, false, [], null,
            userState: StateOf(secondId),
            toolAuthorizationGate: Helper(provider).Bind(secondId));

        try
        {
            await RunToCompletionAsync(service, "run the unmapped tool");

            // NO SIDE EFFECT: the tool body never ran.
            Assert.False(executed);

            // The refusal reached the model and names the tool.
            var refusal = Assert.Single(ToolResults(client));
            Assert.Contains("definitely_not_a_composer_tool", refusal, StringComparison.Ordinal);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    /// <summary>
    /// <c>update_goal</c> WITH AN UNSUPPORTED FIELD is denied through the hook even for an Operator, so the
    /// argument inspection runs on the SNAPSHOTTED arguments SharpCoder hands the callback.
    /// </summary>
    [Fact]
    public async Task UpdateGoalWithUnsupportedField_IsDeniedThroughTheHook()
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

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "update_goal", new Dictionary<string, object?>
            {
                ["id"] = "goal-a",
                ["field"] = "failure_reason",
                ["value"] = "rewritten",
            })),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, Resolve(secondId), out var service, client, goalDb);

        await RunToCompletionAsync(service, "rewrite the failure reason");

        var goal = await store.GetGoalAsync("goal-a", Ct);
        Assert.NotNull(goal);
        Assert.Null(goal!.FailureReason);

        var refusal = Assert.Single(ToolResults(client));
        Assert.Contains("cannot be updated through update_goal", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE HOOK IS WIRED with the acting user's identity as the correlation id, and the pair SharpCoder
    /// captured at construction is the one this runtime's gate produced.
    /// </summary>
    [Fact]
    public async Task TheHook_IsWiredWithTheActingUsersIdentity()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();
        var client = new ScriptedRoundsChatClient(Round(Text("hello")));

        await using var composer = CreateBoundComposer(provider, secondId, Resolve(secondId), out var service, client);
        await ConnectAsync(service);

        var options = service.AgentOptions;
        Assert.NotNull(options.AuthorizeTool);
        Assert.Equal($"composer-user:{secondId}", options.ToolAuthorizationCorrelationId);
    }

    /// <summary>
    /// A LEGACY Composer (no identity) keeps the prior tool surface: no security primitives means no
    /// authorization pair, which is exactly the byte-identical no-callback behaviour of SharpCoder 0.23.0.
    /// </summary>
    [Fact]
    public async Task ALegacyComposerWithoutSecurityPrimitives_ConfiguresNoAuthorizationPair()
    {
        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var client = new ScriptedRoundsChatClient(Round(Text("hello")));
        await using var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store, stateDir: StateDir,
            chatClientFactory: _ => client);
        _composers.Add(composer);

        var service = AgentServiceOf(composer);
        await ConnectAsync(service);

        Assert.Null(service.AgentOptions.AuthorizeTool);
        Assert.Null(service.AgentOptions.ToolAuthorizationCorrelationId);
    }

    // ── Helpers ──

    /// <summary>
    /// Creates an identity-bound Composer whose <see cref="ComposerAgentService"/> streams over
    /// <paramref name="client"/>, and returns that service.
    /// </summary>
    private Composer CreateBoundComposer(
        ServiceProvider provider, int userId, ActingUserPrincipal? principal,
        out ComposerAgentService service, IChatClient client, CopilotHiveDbContext? goalDb = null)
    {
        var store = new GoalStore(goalDb ?? CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => client,
            userState: StateOf(userId),
            principal: principal);
        _composers.Add(composer);
        service = AgentServiceOf(composer);
        return composer;
    }

    /// <summary>
    /// A Composer with EVERY optional tool group registered (config repo, knowledge graph, issue store, web
    /// tools), so the tool census covers the whole surface rather than only the always-on names.
    /// </summary>
    private Composer CreateFullyEquippedComposer(ServiceProvider provider, int userId)
    {
        var configRepoDir = Path.Combine(_dir, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var configRepo = new ConfigRepoManager("https://example.invalid/config.git", configRepoDir);

        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            ollamaApiKey: "test-key",
            configRepo: configRepo,
            knowledgeGraph: new CopilotHive.Knowledge.KnowledgeGraph(),
            chatClientFactory: _ => new MockChatClient(_ => "unused"),
            issueStore: new NullIssueStore(),
            userState: StateOf(userId),
            principal: Resolve(userId));
        _composers.Add(composer);
        return composer;
    }

    private static ComposerAgentService AgentServiceOf(Composer composer) =>
        (ComposerAgentService)(typeof(Composer).GetField("_agentService", PrivateFlags)!.GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null"));

    /// <summary>The model-visible tool names the Composer registers on its agent's custom-tool list.</summary>
    private static IEnumerable<string> RegisteredToolNames(Composer composer)
    {
        var tools = (List<AITool>)typeof(Composer).GetField("_composerTools", PrivateFlags)!.GetValue(composer)!;
        return tools.Select(tool => tool.Name);
    }

    /// <summary>Connects the service so a real <see cref="CodingAgent"/> (and its captured hook) exists.</summary>
    private static async Task ConnectAsync(ComposerAgentService service)
    {
        await service.ConnectAsync(Ct);
    }

    /// <summary>
    /// Runs one full agent execution to completion and returns the streamed updates. The STREAMING entry
    /// point is used because the fixture's chat client scripts its rounds over
    /// <see cref="IChatClient.GetStreamingResponseAsync"/> (the path the Composer itself streams through), and
    /// SharpCoder's streaming loop is what executes the model's tool calls.
    /// </summary>
    private static async Task<IReadOnlyList<StreamingUpdate>> RunToCompletionAsync(
        ComposerAgentService service, string prompt)
    {
        await ConnectAsync(service);
        var agent = service.Agent ?? throw new InvalidOperationException("The agent was not created.");
        var session = AgentSession.Create("composer");

        var updates = new List<StreamingUpdate>();
        await foreach (var update in agent.ExecuteStreamingAsync(session, prompt, Ct))
        {
            updates.Add(update);
        }

        return updates;
    }

    /// <summary>The tool-result texts the model received on the round AFTER a tool call.</summary>
    private static IReadOnlyList<string> ToolResults(ScriptedRoundsChatClient client) =>
        client.Requests
            .SelectMany(request => request)
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(content => content.Result?.ToString() ?? string.Empty)
            .ToArray();

    /// <summary>
    /// Builds the immutable <see cref="SharpCoder.ToolAuthorizationContext"/> SharpCoder hands the callback. Its
    /// constructor is deliberately not public (only SharpCoder creates real ones), so a test that wants to call
    /// the bound gate directly builds one through its non-public constructor.
    /// </summary>
    private static SharpCoder.ToolAuthorizationContext Context(string toolName) =>
        (SharpCoder.ToolAuthorizationContext)typeof(SharpCoder.ToolAuthorizationContext)
            .GetConstructors(PrivateFlags)
            .Single()
            .Invoke([toolName, "correlation", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>()]);

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

    /// <summary>
    /// A streaming chat client scripted BY REQUEST NUMBER, recording the messages of every request so a test
    /// can inspect what the model was actually told (including the tool results it received).
    /// </summary>
    private sealed class ScriptedRoundsChatClient(
        params IReadOnlyList<ChatResponseUpdate>[] rounds) : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _requests = [];

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => _requests;

        public ChatClientMetadata Metadata => new("scripted-rounds", null, "scripted-rounds-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Only the streaming path is scripted.");

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

            foreach (var update in rounds[requestIndex])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A minimal issue store so the issue tool group is registered (the census needs its names).</summary>
    private sealed class NullIssueStore : IIssueStore
    {
        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default) => Task.FromResult(issue);

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default) => Task.FromResult<Issue?>(null);

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Issue>>([]);

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Issue>>([]);

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default) => Task.FromResult(true);
    }
}

/// <summary>
/// A REAL user directory with one activated account per role, plus the production authorization helper over
/// it — so a per-role threshold test never needs a mocked identity source.
/// </summary>
internal sealed class HiveSecurityTestDirectoryForRoles : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Dictionary<UserRole, int> _ids = [];
    private readonly UserService _users;
    private readonly HivePrincipalResolver _resolver;

    internal HiveSecurityTestDirectoryForRoles()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new CopilotHiveDbContext(options))
        {
            ctx.Database.EnsureCreated();
        }

        _users = new UserService(new FactoryImpl(options), NullLogger<UserService>.Instance);
        _resolver = new HivePrincipalResolver(_users, HiveAuthMode.AuthenticationEnabled, NullLogger<HivePrincipalResolver>.Instance);

        // One activated account per role, seeded directly so any role can be created.
        var next = 1;
        foreach (var role in new[] { UserRole.Viewer, UserRole.Member, UserRole.Operator, UserRole.Admin })
        {
            using var db = new CopilotHiveDbContext(options);
            var entity = new CopilotHive.Persistence.Entities.UserEntity
            {
                GitHubId = (900 + next).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Username = $"role-{role}",
                AccessToken = "token",
                Role = role,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            };
            db.Users.Add(entity);
            db.SaveChanges();
            _ids[role] = entity.Id;
            next++;
        }
    }

    internal ComposerToolAuthorization Helper() =>
        new(_resolver, new HivePolicy(), _users, HiveAuthMode.AuthenticationEnabled,
            NullLogger<ComposerToolAuthorization>.Instance);

    /// <summary>
    /// Authorizes <paramref name="toolName"/> as the account holding <paramref name="role"/>. The role's
    /// account id comes from this fixture's directory, so no principal is fabricated.
    /// </summary>
    internal Task<bool> AuthorizeAsRoleAsync(
        ComposerToolAuthorization helper, UserRole role, string toolName, CancellationToken ct) =>
        AuthorizeAsync(helper, _ids[role], toolName, ct);

    /// <summary>
    /// Authorizes one call as the account holding <paramref name="role"/>, returning whether it was allowed.
    /// </summary>
    internal async Task<bool> AuthorizeAsRoleAsync(
        ComposerToolAuthorization helper, UserRole role, string toolName,
        IReadOnlyDictionary<string, object?>? arguments, CancellationToken ct) =>
        (await helper.AuthorizeAsync(_ids[role], toolName, arguments, ct)).IsAllowed;

    /// <summary>
    /// Authorizes one call as the account holding <paramref name="role"/>, returning the whole result so a test
    /// can assert the model-visible denial text too.
    /// </summary>
    internal Task<ComposerToolAuthorizationResult> AuthorizeResultAsRoleAsync(
        ComposerToolAuthorization helper, UserRole role, string toolName,
        IReadOnlyDictionary<string, object?>? arguments, CancellationToken ct) =>
        helper.AuthorizeAsync(_ids[role], toolName, arguments, ct);

    /// <summary>Authorizes as the account id itself (the helper takes the id a runtime is bound to).</summary>
    internal async Task<bool> AuthorizeAsync(
        ComposerToolAuthorization helper, int userId, string toolName, CancellationToken ct) =>
        (await helper.AuthorizeAsync(userId, toolName, arguments: null, ct)).IsAllowed;

    public void Dispose() => _connection.Dispose();

    private sealed class FactoryImpl(DbContextOptions<CopilotHiveDbContext> options)
        : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => new(options);
    }
}
