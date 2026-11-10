using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE SERVER-SIDE AUTHORIZATION CONTRACT of <see cref="ComposerFacade"/>: every operation requires
/// a FRESH <see cref="HiveCapability.InteractiveMember"/> resolution of the caller's CURRENT stored
/// state, and the runtime underneath stays the LEGACY singleton <see cref="Composer"/>.
/// <para>
/// The matrix is exercised per operation against a REAL directory (production <see cref="UserService"/>
/// over SQLite) and a REAL Composer fixture, so the success rows assert ACTUAL values (the applied
/// model, the real post-compaction message count) instead of merely claiming that a refusal branch
/// was reached.
/// </para>
/// </summary>
public sealed class ComposerFacadeAuthorizationTests : IDisposable
{
    internal const string ViewerId = "viewer-1";
    internal const string MemberId = "member-1";
    internal const string OperatorId = "operator-1";
    internal const string AdminId = "admin-1";
    internal const string OpenOperatorGitHubId = "open-operator";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly List<string> _tempDirs = [];

    public ComposerFacadeAuthorizationTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HivePrincipalResolver AuthResolver => _auth.Resolver(HiveAuthMode.AuthenticationEnabled);

    private ComposerFacade Facade(Composer? composer, HiveAuthMode? mode = null) =>
        new(
            composer,
            mode is { } m ? _auth.Resolver(m) : AuthResolver,
            _auth.Policy,
            NullLogger<ComposerFacade>.Instance);

    /// <summary>Creates a real, isolated (never connected) Composer.</summary>
    private Composer CreateComposer()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-facade-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        return new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    /// <summary>Returns the Composer's <see cref="SharpCoder.AgentSession"/> via reflection.</summary>
    private static SharpCoder.AgentSession GetSessionOf(Composer composer)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        var agentService = (ComposerAgentService)typeof(Composer).GetField("_agentService", Flags)!.GetValue(composer)!;
        return (SharpCoder.AgentSession)agentService.GetType().GetField("_session", Flags)!.GetValue(agentService)!;
    }

    /// <summary>Creates a CONNECTED Composer with enough history for a real compaction.</summary>
    private async Task<Composer> CreateConnectedComposerAsync(bool populateMessages = true)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-facade-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));

        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => mockClient.Object);

        await composer.ConnectAsync(Ct);

        if (populateMessages)
        {
            var session = GetSessionOf(composer);
            session.MessageHistory.Clear();
            session.MessageHistory.Add(new ChatMessage(ChatRole.System, "You are a helpful assistant."));
            for (var i = 0; i < 15; i++)
                session.MessageHistory.Add(new ChatMessage(
                    i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"Message {i}"));
        }

        return composer;
    }

    /// <summary>
    /// THE OPERATION MATRIX: one row per (operation, identity shape). <c>Denied</c> rows must return
    /// <see cref="FacadeErrorKind.Forbidden"/> touching NOTHING; <c>Allowed</c> rows must reach the
    /// real Composer.
    /// </summary>
    public enum Operation
    {
        CurrentModel,
        Models,
        Switch,
        Compact,
        CompactPartial,
    }

    private Task<FacadeErrorKind> InvokeAsync(IComposerFacade facade, Operation operation, ClaimsPrincipal? actingUser)
        => operation switch
        {
            Operation.CurrentModel => GetKind(facade.GetCurrentModelAsync(actingUser, Ct)),
            Operation.Models => GetKind(facade.GetModelsAsync(actingUser, Ct)),
            Operation.Switch => GetKind(facade.SwitchModelAsync(actingUser, "claude-opus", "medium", Ct)),
            Operation.Compact => GetKind(facade.CompactAsync(actingUser, Ct)),
            Operation.CompactPartial => GetKind(facade.CompactPartialAsync(actingUser, 50, Ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static async Task<FacadeErrorKind> GetKind<T>(Task<FacadeResult<T>> call)
        => (await call).Kind;

    // ── Viewer refused Forbidden, on EVERY operation ───────────────────────

    /// <summary>
    /// A Viewer holds <see cref="HiveCapability.SharedRead"/> only, so ALL five runtime operations
    /// are refused with <see cref="FacadeErrorKind.Forbidden"/> — and, because denial happens before
    /// any Composer work, the refusal is identical for a null Composer and a real one (no
    /// NotConfigured leak, no BadRequest from the compaction catch-all).
    /// </summary>
    [Theory]
    [InlineData(Operation.CurrentModel)]
    [InlineData(Operation.Models)]
    [InlineData(Operation.Switch)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task Viewer_IsRefusedForbidden_OnEveryOperation(Operation operation)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var composer = CreateComposer();
        await using (composer)
        {
            var kind = await InvokeAsync(Facade(composer), operation, FacadeAuthorizationHarness.Principal(ViewerId));

            Assert.Equal(FacadeErrorKind.Forbidden, kind);
        }
    }

    /// <summary>
    /// The denial text is SANITIZED: it names no account, role, capability, refusal reason or
    /// directory detail, so an unauthorized caller cannot probe the directory through the message.
    /// </summary>
    [Fact]
    public async Task Viewer_DenialMessage_IsSanitized()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var facade = Facade(CreateComposer());

        var result = await facade.GetModelsAsync(FacadeAuthorizationHarness.Principal(ViewerId), Ct);

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal("The acting account is not authorized to perform this operation.", result.Error);
        Assert.DoesNotContain(ViewerId, result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("Viewer", result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("Member", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An UNAUTHENTICATED caller (signed out / no identity at all) is refused on every operation
    /// while authentication is enabled — there is no unauthenticated overload and no anonymous
    /// fall-through to the legacy Composer.
    /// </summary>
    [Theory]
    [InlineData(Operation.CurrentModel)]
    [InlineData(Operation.Models)]
    [InlineData(Operation.Switch)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task Unauthenticated_IsRefusedForbidden_OnEveryOperation(Operation operation)
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = CreateComposer();
        await using (composer)
        {
            var facade = Facade(composer);

            Assert.Equal(FacadeErrorKind.Forbidden, await InvokeAsync(facade, operation, actingUser: null));
            Assert.Equal(
                FacadeErrorKind.Forbidden,
                await InvokeAsync(facade, operation, FacadeAuthorizationHarness.Anonymous()));
        }
    }

    /// <summary>
    /// An authenticated GitHub identity with NO activated account (never invited / pending) and a
    /// DISABLED account are both refused on every operation.
    /// </summary>
    [Theory]
    [InlineData(Operation.CurrentModel)]
    [InlineData(Operation.Models)]
    [InlineData(Operation.Switch)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task UnknownAndDisabledAccounts_AreRefusedForbidden_OnEveryOperation(Operation operation)
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync("disabled-member", UserRole.Member, enabled: false);

        var composer = CreateComposer();
        await using (composer)
        {
            var facade = Facade(composer);

            Assert.Equal(
                FacadeErrorKind.Forbidden,
                await InvokeAsync(facade, operation, FacadeAuthorizationHarness.Principal("never-invited")));

            Assert.Equal(
                FacadeErrorKind.Forbidden,
                await InvokeAsync(facade, operation, FacadeAuthorizationHarness.Principal("disabled-member")));
        }
    }

    /// <summary>
    /// THE REMAINING REFUSAL REASONS AT THE FACADE BOUNDARY: an account whose PERSISTED role is
    /// not a supported role, and an account whose row cannot be read at all, are both refused with
    /// <see cref="FacadeErrorKind.Forbidden"/> on every operation.
    /// <para>
    /// The resolver classifies those refusals (<see cref="PrincipalRefusalReason.UnknownRole"/> and
    /// <see cref="PrincipalRefusalReason.DirectoryFailure"/>); this row pins that the FACADE turns
    /// EVERY refusal into the same single, sanitized authorization answer — never into a 400, a 500
    /// or a partially-authorized success.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(Operation.CurrentModel)]
    [InlineData(Operation.Models)]
    [InlineData(Operation.Switch)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task UnreadableAccountState_IsRefusedForbidden_OnEveryOperation(Operation operation)
    {
        // A row whose PERSISTED role text is not a supported role: the resolver refuses it.
        _auth.Directory.SeedUnsupportedPersistedRole("corrupt-role", "superuser");

        var composer = CreateComposer();
        await using (composer)
        {
            var facade = Facade(composer);

            var result = await InvokeAsync(facade, operation, FacadeAuthorizationHarness.Principal("corrupt-role"));

            Assert.Equal(FacadeErrorKind.Forbidden, result);
        }
    }

    /// <summary>
    /// ORDERING CONTROL: authorization runs BEFORE the null-Composer check, so an unauthorized caller
    /// gets <see cref="FacadeErrorKind.Forbidden"/> from a NULL-Composer facade too — never
    /// <see cref="FacadeErrorKind.NotConfigured"/>, which would leak whether a Composer exists and
    /// would let an unauthenticated caller reach the legacy runtime's configuration state. An
    /// implementation that checked the Composer first fails this test.
    /// </summary>
    [Theory]
    [InlineData(Operation.CurrentModel)]
    [InlineData(Operation.Models)]
    [InlineData(Operation.Switch)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task UnauthorizedCaller_GetsForbidden_EvenWhenTheComposerIsNull(Operation operation)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        // The SAME facade shape the production DI can produce when no Composer is registered.
        var kind = await InvokeAsync(Facade(null), operation, FacadeAuthorizationHarness.Principal(ViewerId));

        Assert.Equal(FacadeErrorKind.Forbidden, kind);
        Assert.NotEqual(FacadeErrorKind.NotConfigured, kind);
    }

    // ── Member SUCCEEDS through the LEGACY Composer (discriminating controls) ──

    /// <summary>
    /// EVERY role that HOLDS <see cref="HiveCapability.InteractiveMember"/> reaches the real
    /// Composer and gets the REAL values: a Member, an Operator and an Admin all read the
    /// Composer's own catalog, the Composer's own current model and a really applied switch.
    /// <para>
    /// The three rows are separate on purpose: a facade that gated on the WRONG capability (for
    /// example <see cref="HiveCapability.GlobalAdmin"/> or <see cref="HiveCapability.SharedRead"/>)
    /// would refuse one of them, and a facade that let a Viewer through would fail the refusal rows
    /// above. Every row asserts the ACTUAL value the Composer holds — never merely that a
    /// dispatch branch was reached.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(MemberId, UserRole.Member)]
    [InlineData(OperatorId, UserRole.Operator)]
    [InlineData(AdminId, UserRole.Admin)]
    public async Task InteractiveMemberRoles_ReadAndSwitch_ReachTheLiveLegacyComposer(
        string gitHubId, UserRole role)
    {
        await _auth.SeedAsync(gitHubId, role);
        var composer = CreateComposer();
        await using (composer)
        {
            await composer.ConnectAsync(Ct);
            var facade = Facade(composer);
            var caller = FacadeAuthorizationHarness.Principal(gitHubId);

            var models = await facade.GetModelsAsync(caller, Ct);
            Assert.True(models.Success, models.Error ?? "models read failed");
            Assert.Equal(FacadeErrorKind.None, models.Kind);
            Assert.Equal(composer.AvailableModels, models.Value!.Models);
            Assert.Equal(["claude-sonnet-4", "claude-opus"], models.Value.Models);

            var current = await facade.GetCurrentModelAsync(caller, Ct);
            Assert.True(current.Success, current.Error ?? "current-model read failed");
            Assert.Equal("claude-sonnet-4", current.Value!.Model);

            var switched = await facade.SwitchModelAsync(caller, "claude-opus", "high", Ct);
            Assert.True(switched.Success, switched.Error ?? "switch failed");
            Assert.Equal("claude-opus", switched.Value!.Model);
            Assert.Equal(ReasoningEffort.High, switched.Value.ReasoningEffort);

            // ...and the SAME legacy Composer instance really switched: the facade drives the
            // instance it was handed and never fabricates the applied values.
            Assert.Equal("claude-opus", composer.GetStats()?.Model);
        }
    }

    /// <summary>
    /// EVERY role that holds <see cref="HiveCapability.InteractiveMember"/> really COMPACTS: the
    /// compaction runs on the real Composer and reports its real post-compaction message count.
    /// The row is a success control for the compaction path for each of those roles — a facade
    /// that refused an Operator or an Admin would fail here.
    /// </summary>
    [Theory]
    [InlineData(MemberId, UserRole.Member)]
    [InlineData(OperatorId, UserRole.Operator)]
    [InlineData(AdminId, UserRole.Admin)]
    public async Task InteractiveMemberRoles_Compact_RunsOnTheRealComposer(
        string gitHubId, UserRole role)
    {
        await _auth.SeedAsync(gitHubId, role);
        var composer = await CreateConnectedComposerAsync();
        await using (composer)
        {
            var facade = Facade(composer);
            var caller = FacadeAuthorizationHarness.Principal(gitHubId);

            var full = await facade.CompactAsync(caller, Ct);
            Assert.True(full.Success, full.Error ?? "compaction failed");
            Assert.True(full.Value!.Compacted);
            Assert.True(full.Value.MessageCount > 0, "the real post-compaction message count must be reported");

            var partial = await facade.CompactPartialAsync(caller, 50, Ct);
            Assert.True(partial.Success, partial.Error ?? "partial compaction failed");
        }
    }

    /// <summary>
    /// THE DISCONNECTED-NULL-MODEL READ: a Composer that was never connected has no active model,
    /// and that is a SUCCESSFUL read carrying a NULL model — never a failure and never a
    /// fabricated catalog entry. Every authorized role sees exactly that, so an authorization
    /// implementation cannot turn a normal disconnected state into an error.
    /// </summary>
    [Theory]
    [InlineData(MemberId, UserRole.Member)]
    [InlineData(OperatorId, UserRole.Operator)]
    [InlineData(AdminId, UserRole.Admin)]
    public async Task InteractiveMemberRoles_DisconnectedComposer_ReadsANullModelSuccessfully(
        string gitHubId, UserRole role)
    {
        await _auth.SeedAsync(gitHubId, role);
        var composer = CreateComposer();
        await using (composer)
        {
            var result = await Facade(composer)
                .GetCurrentModelAsync(FacadeAuthorizationHarness.Principal(gitHubId), Ct);

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.Null(result.Error);
            Assert.NotNull(result.Value);
            Assert.Null(result.Value!.Model);
        }
    }

    /// <summary>
    /// A Member READ reaches the REAL Composer: the catalog is the Composer's own list and the
    /// current model is the model the Composer actually connected with. A facade that answered
    /// "Forbidden" for everyone, or fabricated a payload, fails these assertions.
    /// </summary>
    [Fact]
    public async Task Member_ReadAndSwitch_ReachTheLiveLegacyComposer()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = CreateComposer();
        await using (composer)
        {
            await composer.ConnectAsync(Ct);
            var facade = Facade(composer);
            var member = FacadeAuthorizationHarness.Principal(MemberId);

            var models = await facade.GetModelsAsync(member, Ct);
            Assert.True(models.Success, models.Error ?? "models read failed");
            Assert.Equal(FacadeErrorKind.None, models.Kind);
            Assert.Equal(composer.AvailableModels, models.Value!.Models);

            var current = await facade.GetCurrentModelAsync(member, Ct);
            Assert.True(current.Success, current.Error ?? "current-model read failed");
            Assert.Equal("claude-sonnet-4", current.Value!.Model);

            var switched = await facade.SwitchModelAsync(member, "claude-opus", "high", Ct);
            Assert.True(switched.Success, switched.Error ?? "switch failed");
            Assert.Equal("claude-opus", switched.Value!.Model);
            Assert.Equal(ReasoningEffort.High, switched.Value.ReasoningEffort);

            // ...and the SAME legacy Composer instance really switched: the facade drives the
            // instance it was handed and never fabricates the applied values.
            Assert.Equal("claude-opus", composer.GetStats()?.Model);
        }
    }

    /// <summary>
    /// A Member COMPACTION runs on the real Composer and reports its real post-compaction message
    /// count. This is the discriminating success control for the compaction path (the refusal
    /// rows above prove denial; this proves the authorized path still does the work).
    /// </summary>
    [Fact]
    public async Task Member_Compaction_RunsOnTheRealComposer()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = await CreateConnectedComposerAsync();
        await using (composer)
        {
            var facade = Facade(composer);
            var member = FacadeAuthorizationHarness.Principal(MemberId);

            var full = await facade.CompactAsync(member, Ct);
            Assert.True(full.Success, full.Error ?? "compaction failed");
            Assert.True(full.Value!.Compacted);
            Assert.True(full.Value.MessageCount > 0, "the real post-compaction message count must be reported");

            var partial = await facade.CompactPartialAsync(member, 50, Ct);
            Assert.True(partial.Success, partial.Error ?? "partial compaction failed");
        }
    }

    /// <summary>
    /// The runtime selection is deliberately UNCHANGED: the facade drives the Composer instance it
    /// was handed (the legacy singleton) and never asks a <see cref="ComposerRuntimeManager"/> for a
    /// per-user runtime. A per-user runtime here would be a private-session claim this slice does
    /// not make.
    /// </summary>
    [Fact]
    public void ComposerFacade_HasNoRuntimeManagerDependency()
    {
        var fieldTypes = typeof(ComposerFacade)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Select(f => f.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(ComposerRuntimeManager), fieldTypes);
        Assert.DoesNotContain(typeof(ComposerFactory), fieldTypes);
        // ...and it never captures the SCOPED circuit accessor (or a resolved principal).
        Assert.DoesNotContain(typeof(CircuitUserPrincipalAccessor), fieldTypes);
        Assert.DoesNotContain(typeof(ActingUserPrincipal), fieldTypes);
    }

    /// <summary>
    /// THE LEGACY SINGLETON IS THE RUNTIME — BEHAVIOURALLY, NOT JUST STRUCTURALLY: the facade
    /// drives the EXACT <see cref="Composer"/> instance it was handed, so two facades over two
    /// different Composer instances stay completely independent. A per-user runtime selection (a
    /// <see cref="ComposerRuntimeManager"/> handing out a shared per-user runtime) would make the
    /// two instances converge — the second facade would observe the FIRST facade's switch.
    /// <para>
    /// The structural pin above (no runtime-manager field) cannot see a runtime chosen through
    /// some other seam; this behavioural pin does.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Facade_DrivesExactlyTheComposerInstanceItWasHanded()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);

        var first = CreateComposer();
        var second = CreateComposer();
        await using (first)
        await using (second)
        {
            await first.ConnectAsync(Ct);
            await second.ConnectAsync(Ct);

            var member = FacadeAuthorizationHarness.Principal(MemberId);

            await Facade(first).SwitchModelAsync(member, "claude-opus", "high", Ct);

            // The SECOND composer never switched: it is still on its own startup model, so the
            // facade did not route the call through a shared per-user runtime.
            Assert.Equal("claude-opus", first.GetStats()?.Model);
            Assert.Equal("claude-sonnet-4", second.GetStats()?.Model);
        }
    }

    // ── FRESH resolution: a stale Member demoted between two calls ──────────

    /// <summary>
    /// A Member admitted on the FIRST call and demoted to Viewer before the SECOND is refused on
    /// that second call: role state is re-read from the directory every time, so the cookie's stale
    /// claim confers nothing.
    /// </summary>
    [Theory]
    [InlineData(Operation.CurrentModel)]
    [InlineData(Operation.Models)]
    [InlineData(Operation.Switch)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task DemotedBetweenTwoCalls_IsRefusedOnTheSecondCall(Operation operation)
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = CreateComposer();
        await using (composer)
        {
            await composer.ConnectAsync(Ct);
            var facade = Facade(composer);
            // The SAME principal object for both calls: nothing about the caller's own claims changes.
            var member = FacadeAuthorizationHarness.Principal(MemberId);

            var first = await InvokeAsync(facade, operation, member);
            Assert.NotEqual(FacadeErrorKind.Forbidden, first);

            await _auth.SetRoleAsync(MemberId, UserRole.Viewer);

            var second = await InvokeAsync(facade, operation, member);
            Assert.Equal(FacadeErrorKind.Forbidden, second);
        }
    }

    /// <summary>
    /// The mirror case: an account DISABLED between two calls is refused on the second call even
    /// though the first call succeeded.
    /// </summary>
    [Fact]
    public async Task DisabledBetweenTwoCalls_IsRefusedOnTheSecondCall()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = CreateComposer();
        await using (composer)
        {
            var facade = Facade(composer);
            var member = FacadeAuthorizationHarness.Principal(MemberId);

            Assert.Equal(FacadeErrorKind.None, await InvokeAsync(facade, Operation.Models, member));

            await _auth.SetEnabledAsync(MemberId, enabled: false);

            Assert.Equal(FacadeErrorKind.Forbidden, await InvokeAsync(facade, Operation.Models, member));
        }
    }

    // ── Open mode: the local operator succeeds exactly as today ──────────────

    /// <summary>
    /// THE OPEN-MODE LOCAL OPERATOR REALLY OPERATES THE RUNTIME: every one of the five
    /// operations succeeds in open mode and reports the ACTUAL value — the Composer's own
    /// catalog, the Composer's own current model, a really applied switch, and a real compaction
    /// result.
    /// <para>
    /// The refusal rows above are all about AUTHENTICATION-ENABLED mode; this row proves the
    /// open-mode installation (no OAuth configured, a single local operator) is unchanged. A
    /// facade that gated on the wrong thing in open mode — or that turned the local operator into
    /// an unauthorized caller — fails here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OpenMode_LocalOperator_ReallyOperatesEveryOperation()
    {
        var composer = await CreateConnectedComposerAsync();
        await using (composer)
        {
            var openFacade = Facade(composer, HiveAuthMode.Open);

            var models = await openFacade.GetModelsAsync(actingUser: null, Ct);
            Assert.True(models.Success, models.Error ?? "open-mode models read failed");
            Assert.Equal(FacadeErrorKind.None, models.Kind);
            Assert.Equal(composer.AvailableModels, models.Value!.Models);

            var current = await openFacade.GetCurrentModelAsync(actingUser: null, Ct);
            Assert.True(current.Success, current.Error ?? "open-mode current-model read failed");
            Assert.Equal("claude-sonnet-4", current.Value!.Model);

            var switched = await openFacade.SwitchModelAsync(
                actingUser: null, "claude-opus", "medium", Ct);
            Assert.True(switched.Success, switched.Error ?? "open-mode switch failed");
            Assert.Equal("claude-opus", switched.Value!.Model);
            Assert.Equal(ReasoningEffort.Medium, switched.Value.ReasoningEffort);

            // The legacy Composer instance really switched.
            Assert.Equal("claude-opus", composer.GetStats()?.Model);

            var compacted = await openFacade.CompactAsync(actingUser: null, Ct);
            Assert.True(compacted.Success, compacted.Error ?? "open-mode compaction failed");
            Assert.True(compacted.Value!.Compacted);
            Assert.True(compacted.Value.MessageCount > 0, "the real post-compaction message count must be reported");

            var partially = await openFacade.CompactPartialAsync(actingUser: null, 50, Ct);
            Assert.True(partially.Success, partially.Error ?? "open-mode partial compaction failed");
        }
    }

    /// <summary>
    /// In OPEN mode (no OAuth configured) the resolver yields the local operator and every
    /// operation succeeds exactly as before — including the null-Composer NotConfigured path, whose
    /// message and kind are byte-unchanged for an authorized caller.
    /// </summary>
    [Fact]
    public async Task OpenMode_LocalOperator_Succeeds_AndNullComposerKeepsNotConfigured()
    {
        // Open mode never touches the directory: no account is seeded at all.
        var openFacade = Facade(null, HiveAuthMode.Open);

        var current = await openFacade.GetCurrentModelAsync(actingUser: null, Ct);
        Assert.False(current.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, current.Kind);
        Assert.Equal("Composer is not available.", current.Error);

        var models = await openFacade.GetModelsAsync(actingUser: null, Ct);
        Assert.Equal(FacadeErrorKind.NotConfigured, models.Kind);
        Assert.Equal("Composer is not available.", models.Error);

        var switched = await openFacade.SwitchModelAsync(actingUser: null, model: null, reasoning: null, Ct);
        Assert.Equal(FacadeErrorKind.NotConfigured, switched.Kind);

        var compact = await openFacade.CompactAsync(actingUser: null, Ct);
        Assert.Equal(FacadeErrorKind.NotConfigured, compact.Kind);
        Assert.Equal("Composer is not available.", compact.Error);

        var partial = await openFacade.CompactPartialAsync(actingUser: null, 50, Ct);
        Assert.Equal(FacadeErrorKind.NotConfigured, partial.Kind);

        // A REAL Composer in open mode is fully operable by the local operator.
        var composer = CreateComposer();
        await using (composer)
        {
            await composer.ConnectAsync(Ct);
            var realOpenFacade = Facade(composer, HiveAuthMode.Open);

            var success = await realOpenFacade.GetCurrentModelAsync(actingUser: null, Ct);
            Assert.True(success.Success);
            Assert.Equal("claude-sonnet-4", success.Value!.Model);

            var switchedOpen = await realOpenFacade.SwitchModelAsync(actingUser: null, "claude-opus", "medium", Ct);
            Assert.True(switchedOpen.Success, switchedOpen.Error ?? "open-mode switch failed");
            Assert.Equal("claude-opus", switchedOpen.Value!.Model);
        }
    }

    // ── Operational compaction failure stays 400/BadRequest ─────────────────

    /// <summary>
    /// An OPERATIONAL compaction failure (a never-connected Composer) still maps to
    /// <see cref="FacadeErrorKind.BadRequest"/> carrying the exception message — unchanged — while
    /// the SAME operation for a Viewer is Forbidden. The two answers are distinct, which proves the
    /// authorization decision sits OUTSIDE the compaction catch-all.
    /// </summary>
    [Fact]
    public async Task OperationalCompactionFailure_StaysBadRequest_WhileDenialIsForbidden()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var composer = CreateComposer();
        await using (composer)
        {
            var facade = Facade(composer);

            var operational = await facade.CompactAsync(FacadeAuthorizationHarness.Principal(MemberId), Ct);
            Assert.False(operational.Success);
            Assert.Equal(FacadeErrorKind.BadRequest, operational.Kind);
            Assert.Contains("not connected", operational.Error!, StringComparison.OrdinalIgnoreCase);

            var operationalPartial = await facade.CompactPartialAsync(
                FacadeAuthorizationHarness.Principal(MemberId), 50, Ct);
            Assert.Equal(FacadeErrorKind.BadRequest, operationalPartial.Kind);
            Assert.Contains("not connected", operationalPartial.Error!, StringComparison.OrdinalIgnoreCase);

            // The SAME operation, denied: never the operational 400, and never the operational text.
            var denied = await facade.CompactAsync(FacadeAuthorizationHarness.Principal(ViewerId), Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, denied.Kind);
            Assert.NotEqual(FacadeErrorKind.BadRequest, denied.Kind);
            Assert.DoesNotContain("not connected", denied.Error!, StringComparison.OrdinalIgnoreCase);

            var deniedPartial = await facade.CompactPartialAsync(
                FacadeAuthorizationHarness.Principal(ViewerId), 50, Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, deniedPartial.Kind);
        }
    }

    // ── Cancellation propagates (never mapped) ──────────────────────────────

    /// <summary>
    /// A pre-cancelled token propagates <see cref="OperationCanceledException"/> out of EVERY
    /// operation: cancellation is not an authorization answer and not a compaction failure, so it is
    /// never converted into a <see cref="FacadeResult{T}"/> (which would also let a caller turn a
    /// cancelled authorization into a 403/400).
    /// </summary>
    [Theory]
    [InlineData(Operation.CurrentModel)]
    [InlineData(Operation.Models)]
    [InlineData(Operation.Switch)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task CancelledToken_Propagates_OnEveryOperation(Operation operation)
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = CreateComposer();
        await using (composer)
        {
            var facade = Facade(composer);
            var member = FacadeAuthorizationHarness.Principal(MemberId);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> call = operation switch
            {
                Operation.CurrentModel => () => facade.GetCurrentModelAsync(member, cts.Token),
                Operation.Models => () => facade.GetModelsAsync(member, cts.Token),
                Operation.Switch => () => facade.SwitchModelAsync(member, "claude-opus", "medium", cts.Token),
                Operation.Compact => () => facade.CompactAsync(member, cts.Token),
                Operation.CompactPartial => () => facade.CompactPartialAsync(member, 50, cts.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(call);
        }
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _auth.Dispose();
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the isolated state directory.
            }
        }
    }
}
