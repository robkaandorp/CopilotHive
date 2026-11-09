using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// MINIMAL SELF-CHECKS for the five iteration-1 review repairs. Each test targets exactly one repair, so
/// reverting that repair makes its self-check fail (the mutation matrix in the report records which test
/// kills which mutant).
/// <list type="number">
/// <item>Identity-bound construction REQUIRES a gate; a partial primitive registration fails closed.</item>
/// <item>The open-mode principal comes from <see cref="HivePrincipalResolver"/>, never constructed here.</item>
/// <item><c>null</c> is the SOLE legacy sentinel — a supplied <c>0</c>/negative id is refused in both modes.</item>
/// <item><c>create_goal</c>/<c>create_issue</c> enforce InteractiveMember at the method level.</item>
/// <item>Every guard site threads the caller's token, and cancellation during the fresh lookup propagates.</item>
/// </list>
/// This is deliberately not the reviewer's deep coverage (live child invocation, advertised-tools capture,
/// same-agent freshness, deterministic concurrency, the twelve omitted mutator tests) — the Tester owns that.
/// </summary>
public sealed class ComposerAuthorizationRepairSelfChecks : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-repair-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerAuthorizationRepairSelfChecks()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(StateDir); // the agent's WorkDirectory must exist
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
            var name = $"repair-{Guid.NewGuid():N}";
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

    /// <summary>Every security primitive registered — a complete registration.</summary>
    private ServiceProvider BuildCompleteProvider(HiveAuthMode? mode = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_users);
        services.AddSingleton(mode ?? HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new CopilotHive.Configuration.HiveConfigFile
        {
            Models = new CopilotHive.Configuration.ModelsConfig
            {
                AvailableModels = [new CopilotHive.Configuration.ModelEntry { Name = "test-model" }],
            },
            Composer = new CopilotHive.Configuration.ComposerConfig { Model = "test-model" },
        });
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// A PARTIAL registration: the resolver is present but the policy catalog and the auth mode are not.
    /// </summary>
    private ServiceProvider BuildPartialProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_users);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            HiveAuthMode.AuthenticationEnabled,
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new CopilotHive.Configuration.HiveConfigFile
        {
            Models = new CopilotHive.Configuration.ModelsConfig
            {
                AvailableModels = [new CopilotHive.Configuration.ModelEntry { Name = "test-model" }],
            },
            Composer = new CopilotHive.Configuration.ComposerConfig { Model = "test-model" },
        });
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private Composer CreateBoundComposer(
        IServiceProvider? provider, int userId, CopilotHiveDbContext? goalDb = null, IIssueStore? issueStore = null)
    {
        var store = new GoalStore(goalDb ?? CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            issueStore: issueStore,
            userState: StateOf(userId),
            principal: Resolve(userId));
        _composers.Add(composer);
        return composer;
    }

    // ── Repair 1: no gate for identity-bound state; partial registration fails closed ──

    /// <summary>
    /// IDENTITY-BOUND STATE WITH NO PRIMITIVES IS REJECTED AT CONSTRUCTION — never a silently unguarded
    /// runtime. This is the legacy exception's boundary: <c>userState == null</c> may run gateless, a real
    /// account may not.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_WithoutAnySecurityPrimitives_IsRejected()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var userState = StateOf(secondId);
        var principal = Resolve(secondId);

        // serviceProvider: null ⇒ genuinely absent primitives ⇒ no gate.
        var ex = Assert.Throws<InvalidOperationException>(() => new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir, userState: userState, principal: principal));

        Assert.Contains("requires the security primitives", ex.Message, StringComparison.Ordinal);
        Assert.Contains(secondId.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ALL FOUR ABSENT is the all-or-nothing legacy case and still yields NO gate — so a legacy
    /// (<c>userState == null</c>) Composer over a primitives-free container keeps working, exactly as the
    /// approved legacy exception requires. The companion negative is that identity-bound state is rejected
    /// on the very same container.
    /// </summary>
    [Fact]
    public async Task AllPrimitivesAbsent_YieldsNoGate_ForTheLegacyConstructionOnly()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);

        // A container with NONE of the four primitives.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new CopilotHive.Configuration.HiveConfigFile
        {
            Models = new CopilotHive.Configuration.ModelsConfig
            {
                AvailableModels = [new CopilotHive.Configuration.ModelEntry { Name = "test-model" }],
            },
            Composer = new CopilotHive.Configuration.ComposerConfig { Model = "test-model" },
        });
        using var bare = services.BuildServiceProvider();

        // All-absent is NOT a partial registration: no throw, and no helper.
        Assert.Null(ComposerToolAuthorization.FromServiceProvider(bare));

        // The legacy construction is accepted (approved exception)…
        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        await using var legacy = new Composer(
            "test-model", NullLogger<Composer>.Instance, store, stateDir: StateDir, serviceProvider: bare);
        Assert.NotNull(legacy);

        // …while IDENTITY-BOUND state on the same container is rejected.
        Assert.Throws<InvalidOperationException>(() => CreateBoundComposer(bare, secondId));
    }

    /// <summary>
    /// A PARTIAL primitive registration FAILS CLOSED: it throws instead of degrading to "no authorization".
    /// </summary>
    [Fact]
    public async Task PartiallyRegisteredPrimitives_ThrowInsteadOfDisablingAuthorization()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var partial = BuildPartialProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateBoundComposer(partial, secondId));

        Assert.Contains("partially registered", ex.Message, StringComparison.Ordinal);
        // The message names what is missing, so the misconfiguration is diagnosable.
        Assert.Contains(nameof(HivePolicy), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(HiveAuthMode), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE NO-GATE BRANCH DISTINGUISHES THE TWO MEANINGS: a LEGACY Composer (<c>userState == null</c>) over a
    /// primitives-free container proceeds exactly as before (approved exception — no per-user authority
    /// exists to check), while IDENTITY-BOUND state can never reach that branch because construction rejects
    /// it. The two halves together pin "not a silent unguarded proceed for a real account".
    /// </summary>
    [Fact]
    public async Task NoGateBranch_LetsTheApprovedLegacyExceptionProceed_ButNeverIdentityBoundState()
    {
        var method = typeof(Composer).GetMethod(
            "AuthorizationDenialAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        // The approved legacy exception: no userState, no provider ⇒ no gate ⇒ the call proceeds.
        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var legacy = new Composer("test-model", NullLogger<Composer>.Instance, store, stateDir: StateDir);
        _composers.Add(legacy);

        var legacyDenial = await (Task<string?>)method.Invoke(legacy, [ "approve_goal", Ct ])!;
        Assert.Null(legacyDenial);

        // IDENTITY-BOUND state on the same kind of container cannot be built at all.
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        Assert.Throws<InvalidOperationException>(() => new Composer(
            "test-model", NullLogger<Composer>.Instance,
            new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance),
            stateDir: StateDir,
            userState: StateOf(secondId),
            principal: Resolve(secondId)));
    }

    // ── Repair 2: the open-mode principal comes from the resolver ──

    /// <summary>
    /// THE OPEN-MODE PRINCIPAL COMES FROM THE RESOLVER: in explicit open mode the gate resolves the local
    /// operator THROUGH <see cref="HivePrincipalResolver.ResolveAsync"/> and allows an Admin-only tool, so
    /// no code outside the resolver needs to construct a principal.
    /// <para>
    /// The companion assertion pins the design: <see cref="ActingUserPrincipal"/> exposes NO public factory
    /// for the open-mode identity, and the helper type contains no direct construction call — so this repair
    /// cannot regress to a locally-built principal.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OpenModePrincipal_IsProducedByTheResolver()
    {
        // No accounts at all: only the resolver's explicit open-mode branch can yield a principal.
        using var openProvider = BuildCompleteProvider(HiveAuthMode.Open);
        var helper = ComposerToolAuthorization.FromServiceProvider(openProvider)!;

        var result = await helper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct);

        Assert.True(result.IsAllowed);

        // The resolver is the only producer: the helper's source contains no ActingUserPrincipal
        // construction at all.
        var helperSource = TestSourceScanner.StripNonCode(
            File.ReadAllText(Path.Combine(
                TestRepoRoot.Find(), "src", "CopilotHive", "Security", "ComposerToolAuthorization.cs"))
            .ReplaceLineEndings("\n"));
        Assert.DoesNotContain("ActingUserPrincipal.LocalOperator(", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ActingUserPrincipal.ForActivatedAccount(", helperSource, StringComparison.Ordinal);
    }

    // ── Repair 3: null is the sole legacy sentinel ──

    /// <summary>
    /// A SUPPLIED NON-POSITIVE ID IS REFUSED, in BOTH auth modes: <c>0</c> and negatives are account ids, not
    /// a legacy signal, so they never inherit legacy authority.
    /// </summary>
    /// <param name="userId">The supplied non-positive id.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task NonPositiveSuppliedId_IsRefusedInBothAuthModes(int userId)
    {
        await SeedAccountsAsync(UserRole.Admin);

        using var authProvider = BuildCompleteProvider(HiveAuthMode.AuthenticationEnabled);
        var authHelper = ComposerToolAuthorization.FromServiceProvider(authProvider)!;
        var authResult = await authHelper.AuthorizeAsync(userId, "list_goals", arguments: null, Ct);

        Assert.False(authResult.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(authResult.DenialReason));

        using var openProvider = BuildCompleteProvider(HiveAuthMode.Open);
        var openHelper = ComposerToolAuthorization.FromServiceProvider(openProvider)!;
        var openResult = await openHelper.AuthorizeAsync(userId, "list_goals", arguments: null, Ct);

        // Even open mode refuses: the id is an identity, and an invalid one proves nothing.
        Assert.False(openResult.IsAllowed);
    }

    /// <summary>
    /// <c>null</c> IS STILL THE LEGACY SENTINEL: it selects the legacy original account while auth is
    /// enabled, so the repair removed the id&gt;0 shortcut without removing the legacy path itself.
    /// </summary>
    [Fact]
    public async Task NullId_StillSelectsTheLegacyOriginalAccount()
    {
        await SeedAccountsAsync(UserRole.Admin);

        using var provider = BuildCompleteProvider(HiveAuthMode.AuthenticationEnabled);
        var helper = ComposerToolAuthorization.FromServiceProvider(provider)!;

        var result = await helper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct);

        Assert.True(result.IsAllowed);
    }

    // ── Repair 4: create_goal / create_issue enforce InteractiveMember ──

    /// <summary>
    /// <c>create_goal</c> IS REFUSED FOR A VIEWER, and no goal row appears — the refusals happens BEFORE the
    /// store write.
    /// </summary>
    [Fact]
    public async Task CreateGoal_DirectCall_IsRefusedForAViewer_WithNoRowWritten()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Viewer);
        using var provider = BuildCompleteProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var composer = CreateBoundComposer(provider, secondId, goalDb);

        var refusal = await composer.CreateGoalAsync("viewer-goal", "A goal a Viewer must not create", ct: Ct);

        Assert.Contains("create_goal", refusal, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refusal, StringComparison.Ordinal);
        Assert.Null(await new GoalStore(goalDb, NullLogger<GoalStore>.Instance).GetGoalAsync("viewer-goal", Ct));
    }

    /// <summary>
    /// <c>create_goal</c> IS REFUSED FOR A DISABLED ACCOUNT, and no goal row appears.
    /// </summary>
    [Fact]
    public async Task CreateGoal_DirectCall_IsRefusedForADisabledAccount_WithNoRowWritten()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        var principalBeforeDisable = Resolve(secondId);
        var state = StateOf(secondId);
        await _users.SetUserEnabledAsync(secondId, enabled: false, Ct);

        using var provider = BuildCompleteProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir, serviceProvider: provider,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            userState: state, principal: principalBeforeDisable);
        _composers.Add(composer);

        var refusal = await composer.CreateGoalAsync("disabled-goal", "A goal a disabled account must not create", ct: Ct);

        Assert.Contains("create_goal", refusal, StringComparison.Ordinal);
        Assert.Null(await store.GetGoalAsync("disabled-goal", Ct));
    }

    /// <summary>
    /// <c>create_goal</c> IS ALLOWED FOR A MEMBER: the capability assignment governs WHICH principal passes,
    /// so the repair added a check without making the tool unusable for its intended role.
    /// </summary>
    [Fact]
    public async Task CreateGoal_DirectCall_SucceedsForAMember_AndWritesADraft()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildCompleteProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var composer = CreateBoundComposer(provider, secondId, goalDb);

        var result = await composer.CreateGoalAsync("member-goal", "A goal a Member may draft", ct: Ct);

        Assert.Contains("✅", result, StringComparison.Ordinal);
        var goal = await new GoalStore(goalDb, NullLogger<GoalStore>.Instance).GetGoalAsync("member-goal", Ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalStatus.Draft, goal!.Status);
    }

    /// <summary>
    /// <c>create_issue</c> IS REFUSED FOR A VIEWER, with no issue row written.
    /// </summary>
    [Fact]
    public async Task CreateIssue_DirectCall_IsRefusedForAViewer_WithNoRowWritten()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Viewer);
        using var provider = BuildCompleteProvider();
        var issueStore = new RecordingIssueStore();
        var composer = CreateBoundComposer(provider, secondId, issueStore: issueStore);

        var refusal = await composer.CreateIssueAsync("bug", "A bug", "Description", ct: Ct);

        Assert.Contains("create_issue", refusal, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refusal, StringComparison.Ordinal);
        Assert.Equal(0, issueStore.CreateCalls);
    }

    /// <summary>
    /// <c>create_issue</c> IS ALLOWED FOR A MEMBER: the check exists without breaking the intended role.
    /// </summary>
    [Fact]
    public async Task CreateIssue_DirectCall_SucceedsForAMember()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildCompleteProvider();
        var issueStore = new RecordingIssueStore();
        var composer = CreateBoundComposer(provider, secondId, issueStore: issueStore);

        var result = await composer.CreateIssueAsync("bug", "A bug", "Description", ct: Ct);

        Assert.Contains("Issue created", result, StringComparison.Ordinal);
        Assert.Equal(1, issueStore.CreateCalls);
    }

    // ── Repair 5: caller tokens are threaded; cancellation propagates ──

    /// <summary>
    /// A CANCELLED DIRECT CALL PROPAGATES CANCELLATION WITH NO SIDE EFFECT: the caller's token reaches the
    /// fresh directory lookup, so the caller observes its OWN cancellation (token identity) instead of an
    /// authorization refusal, and the guarded action never ran.
    /// </summary>
    [Fact]
    public async Task CancelledDirectCall_PropagatesCancellation_WithNoSideEffect()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildCompleteProvider();
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var composer = CreateBoundComposer(provider, secondId, goalDb);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => composer.CreateGoalAsync("cancelled-goal", "Must never be created", ct: cts.Token));

        // TOKEN IDENTITY: the caller observes ITS OWN cancellation, not a substituted one.
        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Null(await new GoalStore(goalDb, NullLogger<GoalStore>.Instance).GetGoalAsync("cancelled-goal", Ct));
    }

    /// <summary>
    /// THE HOOK FORWARDS SharpCoder's token: the wired callback threads the token it was handed into the
    /// fresh lookup, so cancelling it propagates and never becomes a denial.
    /// </summary>
    [Fact]
    public async Task Hook_ForwardsSharpCodersToken_SoCancellationPropagates()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildCompleteProvider();
        var gate = ComposerToolAuthorization.FromServiceProvider(provider)!.Bind(secondId);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var context = (SharpCoder.ToolAuthorizationContext)typeof(SharpCoder.ToolAuthorizationContext)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single()
            .Invoke(["approve_goal", "correlation", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>()]);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.AuthorizeToolAsync(context, cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    /// <summary>
    /// EVERY GUARD SITE THREADS THE CALLER'S TOKEN: no guard is invoked without one. This is a structural pin
    /// over the tool partials, so a future guarded method cannot silently drop the token again.
    /// </summary>
    [Fact]
    public void EveryGuardSite_ThreadsTheCallersToken()
    {
        var orchestration = Path.Combine(TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration");

        var sites = 0;
        foreach (var file in new[]
        {
            "ComposerGoalTools.cs",
            "ComposerConfigTools.cs",
            "ComposerIssueTools.cs",
            "ComposerKnowledgeTools.cs",
            "ComposerGitTools.cs",
        })
        {
            var body = TestSourceScanner.StripNonCode(
                File.ReadAllText(Path.Combine(orchestration, file)).ReplaceLineEndings("\n"));

            foreach (var line in body.Split('\n'))
            {
                if (!line.Contains("await AuthorizationDenialAsync(", StringComparison.Ordinal)
                    && !line.Contains("await UpdateGoalAuthorizationDenialAsync(", StringComparison.Ordinal))
                {
                    continue;
                }

                sites++;
                // The invocation must pass a token: a trailing identifier after the tool name / field.
                var hasToken = line.Contains(", ct)", StringComparison.Ordinal)
                    || line.Contains(", cancellationToken)", StringComparison.Ordinal);
                Assert.True(hasToken, $"{file}: a guard is invoked without the caller's token: {line.Trim()}");
            }
        }

        // Non-vacuous: the census actually found the guard sites.
        Assert.True(sites >= 20, $"Expected at least 20 guard sites, found {sites}.");
    }

    // ── Fixtures ──

    /// <summary>An issue store that records whether a create reached the STORE (the side-effect probe).</summary>
    private sealed class RecordingIssueStore : IIssueStore
    {
        public int CreateCalls { get; private set; }

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            CreateCalls++;
            return Task.FromResult(issue);
        }

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
