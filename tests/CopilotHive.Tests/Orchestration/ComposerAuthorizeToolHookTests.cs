using System.Reflection;
using System.Runtime.CompilerServices;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Git;
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
/// HOOK-WIRING PROOF through the REAL SharpCoder 0.23.0 <see cref="AgentOptions.AuthorizeTool"/>
/// callback — the hook is never mocked away: every case drives a real <see cref="CodingAgent"/> built
/// by the real <see cref="ComposerAgentService"/>, and the model's tool call travels through
/// SharpCoder's own authorization boundary.
/// <para>
/// What these cases establish:
/// <list type="bullet">
/// <item>an ALLOWED call really proceeds (the tool body runs and its result reaches the model);</item>
/// <item>a DENIED call is refused BEFORE the backing method runs — the side effect never happens;</item>
/// <item>an UNKNOWN/unmapped tool name defaults to DENY;</item>
/// <item><c>update_goal</c> requires ExecutionOperator for every supported field and is refused
/// outright for any unsupported field;</item>
/// <item>the CHILD read tools map to SharedRead and the FOUR sub-agent tools map to InteractiveMember,
/// with NO escalation path;</item>
/// <item>write_file/edit_file/bash/skills are DISABLED — the Composer's CodingAgent never registers
/// them.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ComposerAuthorizeToolHookTests : IAsyncLifetime
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The model id a sub-agent runs on; it resolves to its own trivial client.</summary>
    private const string SubAgentModel = "sub-model";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-hook-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerAuthorizeToolHookTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(WorkDirectory);
        _factory = new MemoryDbFactory();
        _users = new UserService(_factory, NullLogger<UserService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StateDir => Path.Combine(_dir, "state");

    /// <summary>The agent's work directory — the root the built-in read tools are confined to.</summary>
    private string WorkDirectory => Path.Combine(_dir, "work");

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
            var name = $"hook-{Guid.NewGuid():N}";
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

    private ComposerToolAuthorization Helper(ServiceProvider provider) =>
        ComposerToolAuthorization.FromServiceProvider(provider)
        ?? throw new InvalidOperationException("The fixture container must expose the security primitives.");

    /// <summary>
    /// Creates an identity-bound Composer whose agent streams over <paramref name="client"/>, with a
    /// repo manager present so SharpCoder registers its BUILT-IN read tools (read_file/glob/grep) too,
    /// and a model catalog so the FOUR SUB-AGENT TOOLS are registered as well.
    /// <para>
    /// The sub-agent model ("sub-model") resolves to its own trivial client, so a delegation the hook
    /// ALLOWS cannot consume the scripted parent's rounds.
    /// </para>
    /// </summary>
    private Composer CreateBoundComposer(
        ServiceProvider provider, int userId, IChatClient client, CopilotHiveDbContext? goalDb = null,
        bool withRepoManager = true)
    {
        var store = new GoalStore(goalDb ?? CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            repoManager: withRepoManager ? new BrainRepoManager(_dir, NullLogger<BrainRepoManager>.Instance) : null,
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
                ? new TrivialChatClient()
                : client,
            userState: StateOf(userId),
            principal: Resolve(userId));
        _composers.Add(composer);
        return composer;
    }

    private static ComposerAgentService AgentServiceOf(Composer composer) =>
        (ComposerAgentService)typeof(Composer).GetField("_agentService", PrivateFlags)!.GetValue(composer)!;

    /// <summary>The model-visible tool names the Composer registers on its agent's custom-tool list.</summary>
    private static IEnumerable<string> RegisteredToolNames(Composer composer)
    {
        var tools = (List<AITool>)typeof(Composer).GetField("_composerTools", PrivateFlags)!.GetValue(composer)!;
        return tools.Select(tool => tool.Name);
    }

    private static async Task RunToCompletionAsync(ComposerAgentService service, string prompt)
    {
        await service.ConnectAsync(Ct);
        var agent = service.Agent ?? throw new InvalidOperationException("The agent was not created.");
        var session = AgentSession.Create("composer");
        await foreach (var _ in agent.ExecuteStreamingAsync(session, prompt, Ct)) { }
    }

    private static IReadOnlyList<string> ToolResults(ScriptedRoundsChatClient client) =>
        client.Requests
            .SelectMany(request => request)
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(content => content.Result?.ToString() ?? string.Empty)
            .ToArray();

    // ── (d1) An allowed call proceeds ──

    /// <summary>
    /// AN ALLOWED TOOL CALL PROCEEDS through the REAL hook: the model's <c>get_current_time</c> call
    /// actually runs (the tool body produced its result) and the model receives it on the next round.
    /// A hook that denied everything would fail this case.
    /// </summary>
    [Fact]
    public async Task AllowedToolCall_RunsTheBackingMethodAndReachesTheModel()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();
        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "get_current_time", new Dictionary<string, object?>())),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, client);

        await RunToCompletionAsync(AgentServiceOf(composer), "what time is it?");

        var results = ToolResults(client);
        Assert.Single(results);
        // The tool really ran: its payload (the ISO timestamp) is what the model received.
        Assert.Contains("iso", results[0], StringComparison.Ordinal);
    }

    // ── (d2) A denied capability is refused BEFORE the side effect ──

    /// <summary>
    /// A DENIED CAPABILITY IS REFUSED BEFORE THE BACKING METHOD RUNS: a Member's <c>approve_goal</c>
    /// is refused, the goal stays a Draft (the backing method never touched the store), and the model
    /// receives the host-authored refusal.
    /// </summary>
    [Fact]
    public async Task DeniedCapability_IsRefusedBeforeTheBackingMethodRuns()
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

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "approve_goal", new Dictionary<string, object?> { ["id"] = "goal-a" })),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, client, goalDb);

        await RunToCompletionAsync(AgentServiceOf(composer), "approve goal-a");

        // NO SIDE EFFECT: the backing method never ran, so the goal is untouched.
        var goal = await store.GetGoalAsync("goal-a", Ct);
        Assert.NotNull(goal);
        Assert.Equal(GoalStatus.Draft, goal!.Status);

        var refusal = Assert.Single(ToolResults(client));
        Assert.Contains("approve_goal", refusal, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A DENIED CAPABILITY IS REFUSED BEFORE THE BACKING METHOD RUNS — for a SECOND capability band
    /// and for a tool whose side effect is a WRITE: a Viewer's <c>update_issue</c> leaves the issue
    /// exactly as it was.
    /// </summary>
    [Fact]
    public async Task DeniedMutatingCapability_LeavesTheIssueUntouched()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();
        var issueStore = new RecordingIssueStore();
        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "update_issue", new Dictionary<string, object?>
            {
                ["issue_id"] = "issue-a",
                ["status"] = "closed",
            })),
            Round(Text("finished")));

        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        await using var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => client,
            issueStore: issueStore,
            userState: StateOf(secondId),
            principal: Resolve(secondId));
        _composers.Add(composer);

        await RunToCompletionAsync(AgentServiceOf(composer), "close issue-a");

        // The backing method never ran: the issue store was never written to.
        Assert.Empty(issueStore.Updates);

        var refusal = Assert.Single(ToolResults(client));
        Assert.Contains("update_issue", refusal, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refusal, StringComparison.Ordinal);
    }

    // ── (d3) An unknown tool name defaults to deny ──

    /// <summary>
    /// AN UNKNOWN/UNMAPPED TOOL NAME DEFAULTS TO DENY through the REAL hook — even for an Admin who
    /// holds every capability — and the refusal happens BEFORE the tool body runs.
    /// </summary>
    [Fact]
    public async Task UnknownToolName_DefaultsToDenyThroughTheRealHook()
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

            var refusal = Assert.Single(ToolResults(client));
            Assert.Contains("definitely_not_a_composer_tool", refusal, StringComparison.Ordinal);
            Assert.Contains("not an available Composer tool", refusal, StringComparison.Ordinal);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    // ── (d4) update_goal: supported fields need ExecutionOperator, unsupported fields deny ──

    /// <summary>
    /// EVERY SUPPORTED <c>update_goal</c> FIELD requires ExecutionOperator: an Operator's call goes
    /// through, a Member's is refused with the same host-authored reason, for each supported field.
    /// </summary>
    /// <param name="field">The supported field the call touches.</param>
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
    public async Task UpdateGoal_SupportedField_RequiresExecutionOperator(string field)
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
                ["field"] = field,
                ["value"] = "High",
            })),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, client, goalDb);

        await RunToCompletionAsync(AgentServiceOf(composer), "update the goal");

        // The Operator's call was NOT refused by the authorization boundary.
        var results = ToolResults(client);
        Assert.Single(results);
        Assert.DoesNotContain("role does not permit", results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be updated through update_goal", results[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>update_goal</c> call naming an UNSUPPORTED FIELD is refused outright through the REAL hook
    /// — even for an Admin who holds every capability — and the goal is untouched.
    /// </summary>
    /// <param name="field">The unsupported field the call names.</param>
    [Theory]
    [InlineData("failure_reason")]
    [InlineData("iterations")]
    [InlineData("created_at")]
    [InlineData("id")]
    public async Task UpdateGoal_UnsupportedField_DefaultsToDenyEvenForAnAdmin(string field)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
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
                ["field"] = field,
                ["value"] = "rewritten",
            })),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, client, goalDb);

        await RunToCompletionAsync(AgentServiceOf(composer), "rewrite the goal");

        // The backing method never ran: the goal is untouched.
        var goal = await store.GetGoalAsync("goal-a", Ct);
        Assert.NotNull(goal);
        Assert.Null(goal!.FailureReason);
        Assert.Equal("A draft goal", goal.Description);

        var refusal = Assert.Single(ToolResults(client));
        Assert.Contains("cannot be updated through update_goal", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE HOOK'S OWN ARGUMENT INSPECTION REFUSES AN UNSUPPORTED FIELD: the central helper — the
    /// code the SharpCoder callback runs — refuses a call naming an unsupported field, even for an
    /// Admin who holds every capability. This is the rule the hook applies on the SNAPSHOT SharpCoder
    /// hands it, so it is asserted directly on the helper (the end-to-end case above additionally
    /// proves the goal is left untouched).
    /// </summary>
    /// <param name="field">The unsupported field the call names.</param>
    [Theory]
    [InlineData("failure_reason")]
    [InlineData("iterations")]
    [InlineData("created_at")]
    [InlineData("id")]
    public async Task UpdateGoal_UnsupportedField_IsRefusedByTheHelpersArgumentInspection(string field)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();

        var result = await Helper(provider).AuthorizeAsync(secondId, "update_goal", new Dictionary<string, object?>
        {
            ["id"] = "goal-a",
            ["field"] = field,
            ["value"] = "rewritten",
        }, Ct);

        Assert.False(result.IsAllowed);
        Assert.Contains("cannot be updated through update_goal", result.DenialReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE HOOK'S ARGUMENT INSPECTION IS FAIL CLOSED: a call that names <c>update_goal</c> but supplies
    /// NO field at all cannot be verified, so it is refused rather than skipping the rule.
    /// </summary>
    [Fact]
    public async Task UpdateGoal_WithoutAField_IsRefusedRatherThanSkippingTheRule()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();

        var result = await Helper(provider).AuthorizeAsync(secondId, "update_goal", new Dictionary<string, object?>
        {
            ["id"] = "goal-a",
            ["value"] = "rewritten",
        }, Ct);

        Assert.False(result.IsAllowed);
        Assert.Contains("cannot be updated through update_goal", result.DenialReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY SUPPORTED <c>update_goal</c> FIELD requires ExecutionOperator at the HOOK level too: the
    /// helper refuses a Member's supported-field call and allows an Operator's.
    /// </summary>
    /// <param name="field">The supported field the call touches.</param>
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
    public async Task UpdateGoal_SupportedField_RequiresExecutionOperatorAtTheHookLevel(string field)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();
        var arguments = new Dictionary<string, object?>
        {
            ["id"] = "goal-a",
            ["field"] = field,
            ["value"] = "High",
        };

        var refused = await Helper(provider).AuthorizeAsync(secondId, "update_goal", arguments, Ct);
        Assert.False(refused.IsAllowed);
        Assert.Contains("role does not permit", refused.DenialReason!, StringComparison.Ordinal);

        // The SAME call by an Operator is allowed: the rule is the capability, not the field.
        await _users.ChangeUserRoleAsync(secondId, UserRole.Operator, Ct);
        var allowed = await Helper(provider).AuthorizeAsync(secondId, "update_goal", arguments, Ct);
        Assert.True(allowed.IsAllowed);
    }

    // ── (d5) The child read tools map to SharedRead ──

    /// <summary>
    /// THE CHILD READ TOOLS MAP TO SharedRead: a Viewer's <c>read_file</c>, <c>glob</c> and
    /// <c>grep</c> calls all go through the REAL hook and are ALLOWED — the read surface is reachable
    /// by the lowest role, so the hook is not over-restricting the reads it must let through.
    /// </summary>
    /// <param name="toolName">The built-in read tool name.</param>
    /// <param name="arguments">The tool's arguments.</param>
    [Theory]
    [InlineData("read_file", "sample.txt")]
    [InlineData("glob", "*.txt")]
    [InlineData("grep", "sample")]
    public async Task TheChildReadTools_AreAllowedForAViewerThroughTheRealHook(
        string toolName, string arguments)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Viewer);
        using var provider = BuildProvider();
        await File.WriteAllTextAsync(Path.Combine(WorkDirectory, "sample.txt"), "sample content", Ct);

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", toolName, new Dictionary<string, object?>
            {
                [toolName == "read_file" ? "filePath" : toolName == "glob" ? "pattern" : "pattern"] = arguments,
            })),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, client);

        await RunToCompletionAsync(AgentServiceOf(composer), "read the workspace");

        var results = ToolResults(client);
        Assert.Single(results);
        // The tool really ran: its own output reached the model, NOT a refusal.
        Assert.DoesNotContain("role does not permit", results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("not an available Composer tool", results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("was not executed", results[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CHILD READ TOOLS ARE REFUSED FOR A DISABLED ACCOUNT through the REAL hook: a read is a
    /// capability too, so disabling the account stops the reads at once.
    /// </summary>
    [Fact]
    public async Task AChildReadTool_IsRefusedForADisabledAccountThroughTheRealHook()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();
        await File.WriteAllTextAsync(Path.Combine(WorkDirectory, "sample.txt"), "sample content", Ct);

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", "read_file", new Dictionary<string, object?> { ["filePath"] = "sample.txt" })),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, client);

        // The account is disabled BEFORE the run.
        await _users.SetUserEnabledAsync(secondId, enabled: false, Ct);

        await RunToCompletionAsync(AgentServiceOf(composer), "read the workspace");

        var refusal = Assert.Single(ToolResults(client));
        Assert.Contains("read_file", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("sample content", refusal, StringComparison.Ordinal);
    }

    // ── (d6) The four sub-agent tools map to InteractiveMember ──

    /// <summary>
    /// THE FOUR SUB-AGENT TOOLS MAP TO InteractiveMember through the REAL hook: a Member may call
    /// them (they are allowed), a Viewer may not (they are refused) — and none of them escalates to a
    /// higher capability.
    /// </summary>
    /// <param name="toolName">The sub-agent tool name.</param>
    /// <param name="arguments">The tool's arguments.</param>
    [Theory]
    [InlineData("start_sub_agent", "task")]
    [InlineData("await_sub_agents", "ids")]
    [InlineData("get_sub_agent_status", "id")]
    [InlineData("list_sub_agent_models", "")]
    public async Task TheFourSubAgentTools_RequireInteractiveMemberThroughTheRealHook(
        string toolName, string arguments)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Viewer);
        using var provider = BuildProvider();

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", toolName, ArgumentsFor(toolName, arguments))),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, client);

        await RunToCompletionAsync(AgentServiceOf(composer), "use the sub-agent tools");

        var refusal = Assert.Single(ToolResults(client));
        // A Viewer holds no InteractiveMember: the call is refused by the boundary.
        Assert.Contains(toolName, refusal, StringComparison.Ordinal);
        Assert.Contains("role does not permit", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A MEMBER IS ALLOWED THE FOUR SUB-AGENT TOOLS through the REAL hook: the mapping to
    /// InteractiveMember is not a blanket refusal of the delegation surface.
    /// </summary>
    /// <param name="toolName">The sub-agent tool name.</param>
    /// <param name="arguments">The tool's arguments.</param>
    [Theory]
    [InlineData("start_sub_agent", "task")]
    [InlineData("await_sub_agents", "ids")]
    [InlineData("get_sub_agent_status", "id")]
    [InlineData("list_sub_agent_models", "")]
    public async Task TheFourSubAgentTools_AreAllowedForAMemberThroughTheRealHook(
        string toolName, string arguments)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider();

        var client = new ScriptedRoundsChatClient(
            Round(ToolCall("call-1", toolName, ArgumentsFor(toolName, arguments))),
            Round(Text("finished")));

        await using var composer = CreateBoundComposer(provider, secondId, client);

        await RunToCompletionAsync(AgentServiceOf(composer), "use the sub-agent tools");

        var results = ToolResults(client);
        Assert.Single(results);
        Assert.DoesNotContain("role does not permit", results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("not an available Composer tool", results[0], StringComparison.Ordinal);
    }

    // ── (d7) The disabled shape: write_file/edit_file/bash/skills are never registered ──

    /// <summary>
    /// THE DISABLED SHAPE: the Composer's CodingAgent NEVER REGISTERS write_file, edit_file,
    /// execute_bash_command, load_skill or list_skills — neither as a custom tool nor as one of
    /// SharpCoder's built-ins — so the model cannot even name them.
    /// </summary>
    /// <param name="toolName">A tool outside the Composer surface.</param>
    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("execute_bash_command")]
    [InlineData("load_skill")]
    [InlineData("list_skills")]
    public async Task TheDisabledTools_AreNeverRegisteredOnTheComposersCodingAgent(string toolName)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();
        var client = new ScriptedRoundsChatClient(Round(Text("hello")));

        await using var composer = CreateBoundComposer(provider, secondId, client);

        // Not on the Composer's own custom-tool list…
        Assert.DoesNotContain(toolName, RegisteredToolNames(composer), StringComparer.Ordinal);

        // …and not among the tools the real agent registers for the model.
        await AgentServiceOf(composer).ConnectAsync(Ct);
        var options = AgentServiceOf(composer).AgentOptions;
        Assert.False(options.EnableBash);
        Assert.False(options.EnableFileWrites);
        Assert.False(options.EnableSkills);

        var registered = RegisteredAgentToolNames(client);
        Assert.DoesNotContain(toolName, registered, StringComparer.Ordinal);
    }

    /// <summary>
    /// THE DISABLED SHAPE IS ALSO ENFORCED BY THE MAP: a call naming one of the disabled tools is
    /// refused by the central helper even for an Admin (default deny), so a stray registration could
    /// not become an unguarded capability.
    /// </summary>
    /// <param name="toolName">A tool outside the Composer surface.</param>
    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("execute_bash_command")]
    [InlineData("load_skill")]
    [InlineData("list_skills")]
    public async Task TheDisabledTools_AreRefusedByTheCentralHelperEvenForAnAdmin(string toolName)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();

        Assert.Null(ComposerToolAuthorization.CapabilityFor(toolName));

        var result = await Helper(provider).AuthorizeAsync(secondId, toolName, arguments: null, Ct);
        Assert.False(result.IsAllowed);
        Assert.Contains("not an available Composer tool", result.DenialReason!, StringComparison.Ordinal);
    }

    // ── Helpers ──

    private static IDictionary<string, object?> ArgumentsFor(string toolName, string arguments) =>
        toolName switch
        {
            "start_sub_agent" => new Dictionary<string, object?> { ["task"] = "inspect the workspace" },
            "await_sub_agents" => new Dictionary<string, object?> { ["ids"] = Array.Empty<string>() },
            "get_sub_agent_status" => new Dictionary<string, object?> { ["id"] = "sub-1" },
            _ => new Dictionary<string, object?>(),
        };

    /// <summary>
    /// The tool names the REAL agent registered for the model — read from the options the agent
    /// actually built, not from the Composer's own list.
    /// </summary>
    private static IEnumerable<string> RegisteredAgentToolNames(ScriptedRoundsChatClient client) =>
        client.Requests
            .SelectMany(request => request)
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Select(call => call.Name);

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
    /// A streaming chat client scripted BY REQUEST NUMBER, recording the messages of every request so
    /// a test can inspect what the model was actually told (including the tool results it received).
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

    /// <summary>
    /// A trivial client for a SUB-AGENT run: it answers with a single text message and never consumes
    /// the parent's scripted rounds. It exists so a delegation the hook ALLOWS can actually run.
    /// </summary>
    private sealed class TrivialChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("trivial", null, "trivial-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "sub-agent done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "sub-agent done")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// An issue store that RECORDS every write, so a test can prove the backing method never ran
    /// (an empty record list means no write happened at all).
    /// </summary>
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
