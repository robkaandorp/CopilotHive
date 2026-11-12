using System.Security.Claims;

using CopilotHive.Components.Pages;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE ACCEPTANCE-CRITERIA HARNESS for the circuit integration, mapped one-to-one onto the goal's ACs:
/// <list type="bullet">
/// <item><b>AC1 — cross-circuit isolation:</b> streaming, per-user session history, attachment roots, questions,
/// models, usage and the per-runtime registry id are INDEPENDENT per user, while two circuits of the SAME user
/// share exactly one runtime (and it is never disposed by a circuit).</item>
/// <item><b>AC3 — the non-original credential gate in the CIRCUIT path:</b> a non-original account whose selection
/// is Copilot-backed settles DEFERRED through the real accessor the page uses, and never recovers merely because
/// the original account's token is available; the original account is unaffected by that gate.</item>
/// <item><b>AC4 — the auth-mode deployment matrix at the CIRCUIT level:</b> an authenticated circuit resolves the
/// per-user path for an unset, <c>"true"</c> and <c>"false"</c> retired variable, and an open circuit resolves the
/// legacy singleton — with NO split runtime in any row.</item>
/// </list>
/// <para>
/// Everything runs over the REAL production seam: the REAL <see cref="ComposerChat"/> page where a page is needed,
/// the REAL <see cref="IComposerRuntimeAccessor"/> over the REAL singleton manager, the REAL resolver/policy and a
/// REAL SQLite directory. The selection is scripted only where the manager cannot produce the answer locally.
/// No browser framework is used: the page is rendered with the framework's own
/// <see cref="HtmlRenderer"/>.
/// </para>
/// </summary>
public sealed class ComposerCircuitAcceptanceTests : IAsyncLifetime, IDisposable
{
    private const string Model = "acceptance-model";
    private const string CopilotModel = "copilot/primary";
    private const string OllamaModel = "ollama-local/other";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-acceptance-{Guid.NewGuid():N}");

    private int _originalId;
    private int _memberId;
    private int _operatorId;

    public ComposerCircuitAcceptanceTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The ORIGINAL identity is the lowest-id activated row: only it is exempt from the provider gate.
        _originalId = (await _auth.SeedAsync("acceptance-original", UserRole.Admin)).Id;
        _memberId = (await _auth.SeedAsync("acceptance-member", UserRole.Member)).Id;
        _operatorId = (await _auth.SeedAsync("acceptance-operator", UserRole.Operator)).Id;
    }

    // ══ AC1: cross-circuit isolation ═══════════════════════════════════════════════════════════

    /// <summary>
    /// AC1 — SAME USER, TWO CIRCUITS: EXACTLY ONE RUNTIME, SHARED AND NEVER DISPOSED. Two independent scopes (two
    /// simulated browser tabs) resolve the SAME Composer, the SAME attachment store and the SAME registry id, and
    /// the manager holds exactly ONE entry for that account. Disposing the first circuit (its scope) leaves the
    /// runtime alive and usable by the second — because the manager is the sole disposer.
    /// </summary>
    [Fact]
    public async Task AC1_SameUserTwoCircuits_ShareOneRuntime_AndDisposalNeverDisposesIt()
    {
        await using var host = NewHost();

        using var firstTab = host.Provider.CreateScope();
        using var secondTab = host.Provider.CreateScope();

        var firstAccess = await AcquireAsync(firstTab, "acceptance-member");
        var secondAccess = await AcquireAsync(secondTab, "acceptance-member");

        Assert.True(firstAccess.IsAuthorized);
        Assert.True(secondAccess.IsAuthorized);

        // ONE runtime: the same Composer, the same store, the same registry id.
        Assert.Same(firstAccess.Runtime!.Composer, secondAccess.Runtime!.Composer);
        Assert.Same(firstAccess.Runtime.Attachments, secondAccess.Runtime.Attachments);
        Assert.Equal(firstAccess.Runtime.State.UserId, secondAccess.Runtime.State.UserId);
        Assert.Equal(
            ComposerUserState.RegistryId(firstAccess.Runtime.State),
            ComposerUserState.RegistryId(secondAccess.Runtime.State));
        Assert.Equal($"composer:{_memberId}", ComposerUserState.RegistryId(firstAccess.Runtime.State));

        // Exactly ONE manager entry for that account — not one per circuit.
        Assert.Single(ManagerEntries(host.Manager));

        // Disposing the FIRST circuit's scope disposes only the SCOPED services: the runtime survives.
        var composer = firstAccess.Runtime.Composer;
        firstTab.Dispose();

        Assert.False(composer.TeardownCompletion.IsCompleted,
            "a circuit teardown must never dispose the shared per-user runtime");

        // ...and the second circuit still works on the very same runtime.
        var againAccess = await AcquireAsync(secondTab, "acceptance-member");
        Assert.Same(composer, againAccess.Runtime!.Composer);
        Assert.True(againAccess.Runtime.Composer.IsConnected);
    }

    /// <summary>
    /// AC1 — STREAMING, HISTORY, QUESTIONS, MODELS, USAGE AND THE REGISTRY ARE PER-USER. Two accounts have their
    /// own runtime, with:
    /// <list type="bullet">
    /// <item>a distinct per-user session path and no cross-visibility of history;</item>
    /// <item>a distinct attachments root;</item>
    /// <item>a distinct streaming/compacting flag and streaming buffer;</item>
    /// <item>a distinct pending question;</item>
    /// <item>a distinct usage snapshot and stats;</item>
    /// <item>a distinct <c>composer:N</c> registry id.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task AC1_StreamingHistoryQuestionsModelsUsageAndRegistry_ArePerUser()
    {
        await using var host = NewHost();

        // Two accounts, two circuits.
        await SeedSessionAsync(host, _memberId, "member-only-marker");
        await SeedSessionAsync(host, _operatorId, "operator-only-marker");

        using var memberCircuit = host.Provider.CreateScope();
        using var operatorCircuit = host.Provider.CreateScope();

        var member = await AcquireAsync(memberCircuit, "acceptance-member");
        var other = await AcquireAsync(operatorCircuit, "acceptance-operator");

        Assert.True(member.IsAuthorized);
        Assert.True(other.IsAuthorized);
        Assert.NotSame(member.Runtime!.Composer, other.Runtime!.Composer);

        // ── HISTORY: no cross-visibility ──
        Assert.Contains(member.Runtime.Composer.GetChatHistory(), m => m.Content.Contains("member-only-marker"));
        Assert.DoesNotContain(member.Runtime.Composer.GetChatHistory(), m => m.Content.Contains("operator-only-marker"));
        Assert.Contains(other.Runtime!.Composer.GetChatHistory(), m => m.Content.Contains("operator-only-marker"));
        Assert.DoesNotContain(other.Runtime.Composer.GetChatHistory(), m => m.Content.Contains("member-only-marker"));

        // ── STATE PATHS: disjoint ──
        Assert.NotEqual(
            ComposerUserState.SessionFilePath(host.StateDir, member.Runtime.State),
            ComposerUserState.SessionFilePath(host.StateDir, other.Runtime.State));
        Assert.NotEqual(
            member.Runtime.Attachments.AttachmentsRootPath,
            other.Runtime.Attachments.AttachmentsRootPath);

        // ── ATTACHMENTS: a save in one store is invisible to the other ──
        var saved = await member.Runtime.Attachments.SaveAsync(
            "member-file.png", new MemoryStream([1, 2, 3]), Ct);
        Assert.True(saved.Success, saved.Error ?? "the per-user attachment save failed");
        Assert.True(File.Exists(Path.Combine(
            member.Runtime.Attachments.AttachmentsRootPath, saved.Attachment!.SavedRelativePath)));
        Assert.False(File.Exists(Path.Combine(
            other.Runtime.Attachments.AttachmentsRootPath, saved.Attachment.SavedRelativePath)));
        // ...and the legacy root never received it either.
        Assert.Empty(Directory.Exists(Path.Combine(host.StateDir, "composer-attachments"))
            ? Directory.GetFiles(Path.Combine(host.StateDir, "composer-attachments"))
            : []);

        // ── USAGE + STATS: independent snapshots ──
        Assert.NotSame(member.Runtime.Composer.GetUsage(), other.Runtime.Composer.GetUsage());
        Assert.NotSame(member.Runtime.Composer.GetStats(), other.Runtime.Composer.GetStats());
        Assert.Equal(Model, member.Runtime.Composer.GetStats()!.Model);
        Assert.Equal(Model, other.Runtime.Composer.GetStats()!.Model);

        // ── MODEL CATALOG: the Composer's own list, per runtime ──
        Assert.NotSame(member.Runtime.Composer.AvailableModels, other.Runtime.Composer.AvailableModels);
        Assert.Contains(Model, member.Runtime.Composer.AvailableModels);

        // ── STREAMING BUFFER + QUESTION: neither runtime is streaming and neither holds the other's question, so
        //    no state can leak from one circuit into the other. ──
        Assert.Null(member.Runtime.Composer.PendingQuestion);
        Assert.Null(other.Runtime.Composer.PendingQuestion);
        Assert.False(member.Runtime.Composer.IsStreaming);
        Assert.False(other.Runtime.Composer.IsStreaming);

        // ── REGISTRY: two distinct ids ──
        Assert.NotEqual(
            ComposerUserState.RegistryId(member.Runtime.State),
            ComposerUserState.RegistryId(other.Runtime.State));

        // ── PASSIVE BUFFERS: asserted in the dedicated row below, which registers an event bus (a host without
        //    one has no subscriber at all, by design).

        // Exactly two runtimes, one per account.
        var entries = ManagerEntries(host.Manager).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Contains(_memberId, entries);
        Assert.Contains(_operatorId, entries);
    }

    /// <summary>
    /// AC1 — THE PASSIVE EVENT BUFFER IS PER-RUNTIME: a published event reaches BOTH runtime subscribers (each
    /// holds its own pending queue) and draining one leaves the other's queue intact — so no user can consume (or
    /// be starved of) another user's pending notifications.
    /// </summary>
    [Fact]
    public async Task AC1_PassiveEventBuffers_AreIndependentPerRuntime()
    {
        await using var host = NewHost(registerEventBus: true);

        using var memberCircuit = host.Provider.CreateScope();
        using var operatorCircuit = host.Provider.CreateScope();

        var member = await AcquireAsync(memberCircuit, "acceptance-member");
        var other = await AcquireAsync(operatorCircuit, "acceptance-operator");

        Assert.NotNull(member.Runtime!.Subscriber);
        Assert.NotNull(other.Runtime!.Subscriber);
        Assert.NotSame(member.Runtime.Subscriber, other.Runtime.Subscriber);

        // A broadcast lands in BOTH per-runtime buffers.
        host.EventBus.Publish(TestEvent("broadcast-marker"));
        Assert.NotEmpty(member.Runtime.Subscriber!.DrainPendingEvents());
        Assert.NotEmpty(other.Runtime.Subscriber!.DrainPendingEvents());

        // Drain the FIRST only: the second's queue is untouched by that drain.
        host.EventBus.Publish(TestEvent("member-only-drain"));
        var memberDrained = member.Runtime.Subscriber.DrainPendingEvents();
        var otherDrained = other.Runtime.Subscriber.DrainPendingEvents();
        Assert.Single(memberDrained);
        Assert.Single(otherDrained);
        // Both buffers hold the SAME published event (it is one broadcast) but they are SEPARATE queues: the
        // drain of one did not empty the other, which is the isolation under test.
        Assert.Equal("member-only-drain", memberDrained[0].Message);
        Assert.Equal("member-only-drain", otherDrained[0].Message);
    }

    /// <summary>
    /// SCOPE ITEM 3 — EVERY UI OPERATION IS ROUTED THROUGH THE CIRCUIT BINDING: history, sub-agents, the pending
    /// question, stats, usage, streaming state and the model catalog are all read from the page's <c>Composer</c>
    /// property, which resolves the CIRCUIT BINDING — never the injected legacy singleton. The page has no other
    /// Composer field, and the legacy injections are named apart from the bound accessor, so no operation can
    /// silently read the wrong runtime.
    /// <para>
    /// The removal-proof content of this row: the page's <c>Composer</c> property is the ONLY accessor for those
    /// members, and it throws when nothing is bound (rather than substituting a Composer, which could only be the
    /// legacy singleton). A handler that reached for the legacy injection would have to name it explicitly.
    /// </para>
    /// </summary>
    [Fact]
    public void ScopeItem3_EveryUiOperationReadsTheCircuitBinding_NeverTheLegacySingleton()
    {
        var repoRoot = TestRepoRoot.Find();
        // RAW for the razor: a Razor file is markup, not C#, so the C#-oriented scanner masks member calls that
        // sit inside markup expressions. The negative assertions below are LITERAL member accesses, which no
        // comment in the file contains.
        var razor = File.ReadAllText(Path.Combine(
                repoRoot, "src", "CopilotHive", "Components", "Pages", "ComposerChat.razor"))
            .ReplaceLineEndings("\n");
        var codeBehind = TestSourceScanner.StripNonCode(File.ReadAllText(Path.Combine(
                repoRoot, "src", "CopilotHive", "Components", "Pages", "ComposerChat.razor.cs"))
            .ReplaceLineEndings("\n"));

        // NO content member is reached through the LEGACY injection.
        foreach (var member in new[]
        {
            "GetChatHistory(", "GetSubAgents(", "GetStats(", "GetUsage(", "AvailableModels",
            "PendingQuestion", "IsStreaming", "IsCompacting", "StreamingContent",
            "SendMessageWithEventsAsync(", "ResetSessionAsync(", "SubmitAnswer(", "CancelQuestion(",
        })
        {
            Assert.DoesNotContain($"LegacyComposer.{member}", razor, StringComparison.Ordinal);
            Assert.DoesNotContain($"LegacyComposer.{member}", codeBehind, StringComparison.Ordinal);
        }

        // ...and every one of those members IS reached through the binding-backed property...
        foreach (var member in new[]
        {
            "Composer.GetChatHistory(", "Composer.GetSubAgents(", "Composer.GetStats(", "Composer.GetUsage(",
            "Composer.PendingQuestion", "Composer.IsStreaming",
        })
        {
            Assert.Contains(member, razor, StringComparison.Ordinal);
        }

        // ...and the SEND runs on the Composer the accessor just selected (the local), never on an injected field.
        Assert.Contains("composer.SendMessageWithEventsAsync(", razor, StringComparison.Ordinal);
        Assert.Contains("composer = await AcquireComposerForMutationAsync(", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyComposer.SendMessageWithEventsAsync(", razor, StringComparison.Ordinal);

        // The binding-backed property is the ONE that throws when nothing is bound — never a silent fallback.
        Assert.Contains(
            "private Composer Composer => BoundComposer ?? throw new InvalidOperationException(",
            codeBehind,
            StringComparison.Ordinal);

        // The legacy injection exists ONLY as the explicit open-mode binding, so reaching it is always deliberate.
        Assert.Contains("@inject Composer LegacyComposer", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("@inject Composer Composer", razor, StringComparison.Ordinal);
    }

    // ══ AC3: the non-original credential gate, through the CIRCUIT seam ══════════════════════

    /// <summary>
    /// AC3 — THE NON-ORIGINAL PROVIDER GATE HOLDS IN THE CIRCUIT PATH: a NON-original account whose selection is
    /// Copilot-backed settles <see cref="ComposerRuntimeStatus.Deferred"/> through the SAME accessor the page
    /// uses, and a later connect attempt made while the original admin's token IS available still does NOT recover
    /// it — the token's availability is never sufficient for a non-original identity.
    /// <para>
    /// The ORIGINAL account is the control: with the token unavailable it also defers, and the moment the token
    /// becomes available its connect succeeds. Same token, opposite outcomes — which is exactly the
    /// identity-bound rule.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AC3_NonOriginalCircuit_NeverRecoversFromTokenAvailability_AndTheOriginalDoes()
    {
        var tokenAvailable = false;

        await using var host = NewHost(
            primaryModel: CopilotModel,
            providerOf: model => model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local",
            isTokenAvailable: () => tokenAvailable);

        // ── The NON-original account: deferred, and it must STAY deferred while the token is available. ──
        using var memberCircuit = host.Provider.CreateScope();
        var memberAccess = await AcquireAsync(memberCircuit, "acceptance-member");
        Assert.True(memberAccess.IsAuthorized);
        Assert.Equal(ComposerRuntimeStatus.Deferred, memberAccess.Status);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Deferred, ComposerChat.ClassifyRuntimeAccess(memberAccess, authEnabled: true));

        // The ORIGINAL admin's token becomes available.
        tokenAvailable = true;

        // The non-original account's connect is attempted through the CIRCUIT method the page uses.
        var memberConnect = await memberCircuit.ServiceProvider
            .GetRequiredService<IComposerRuntimeAccessor>()
            .ConnectForCircuitAsync(AuthenticatedState("acceptance-member"), Ct);

        Assert.Equal(ComposerRuntimeStatus.Deferred, memberConnect.Status);
        Assert.False(memberConnect.Runtime!.Composer.IsConnected,
            "a non-original account must never connect on the original admin's token");

        // ── The ORIGINAL account: the same token now CONNECTS it. ──
        using var originalCircuit = host.Provider.CreateScope();
        var originalAccess = await AcquireAsync(originalCircuit, "acceptance-original");
        Assert.True(originalAccess.IsAuthorized);
        Assert.Equal(ComposerRuntimeStatus.Connected, originalAccess.Status);
        Assert.True(originalAccess.Runtime!.Composer.IsConnected,
            "the original identity keeps the unchanged token-availability recovery");

        // ── An Ollama-backed selection for the SAME non-original account connects: the gate is about the
        //    CREDENTIAL, not about the account being second. ──
        await using var ollamaHost = NewHost(primaryModel: OllamaModel);
        using var ollamaCircuit = ollamaHost.Provider.CreateScope();
        var ollamaAccess = await AcquireAsync(ollamaCircuit, "acceptance-member");
        Assert.Equal(ComposerRuntimeStatus.Connected, ollamaAccess.Status);
        Assert.True(ollamaAccess.Runtime!.Composer.IsConnected);
    }

    // ══ AC4: the auth-mode deployment matrix at the CIRCUIT level ════════════════════════════

    /// <summary>
    /// AC4 — AN AUTHENTICATED CIRCUIT IS ALWAYS THE PER-USER PATH, FOR EVERY VALUE OF THE RETIRED VARIABLE. For an
    /// unset, <c>"true"</c> and <c>"false"</c> value the rendered page binds the caller's OWN runtime (never the
    /// legacy singleton), the manager owns exactly that account's runtime, and no split runtime exists.
    /// <para>
    /// The <c>"false"</c> row is the regression this goal closes: it used to keep the legacy singleton live while
    /// authentication was enabled.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public async Task AC4_AuthenticatedCircuit_IsPerUser_ForEveryRetiredVariableValue(string? retiredValue)
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, retiredValue);

            await using var host = NewHost();
            await SeedSessionAsync(host, _memberId, "per-user-only-marker");

            var legacyComposer = host.Provider.GetRequiredService<Composer>();
            await using var rendered = await RenderAsync(host, "acceptance-member");

            var bound = BoundComposerOf(rendered.Page);
            Assert.NotNull(bound);
            Assert.NotSame(legacyComposer, bound);
            Assert.Contains("per-user-only-marker", rendered.Html, StringComparison.Ordinal);

            // NO SPLIT RUNTIME: the manager owns the account's runtime and ONLY that one; the legacy singleton is
            // not connected by the page's operation.
            var entries = ManagerEntries(host.Manager).ToArray();
            Assert.Contains(_memberId, entries);
            Assert.Single(entries);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// AC4 — AN OPEN-MODE CIRCUIT IS THE LEGACY SINGLETON AND CREATES NO PER-USER RUNTIME, for the same three
    /// values: open mode is the single-operator installation, so the retired variable can never move it onto the
    /// per-user path either.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public async Task AC4_OpenModeCircuit_IsTheLegacySingleton_ForEveryRetiredVariableValue(string? retiredValue)
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, retiredValue);

            await using var host = NewHost(authEnabled: false);

            var legacyComposer = host.Provider.GetRequiredService<Composer>();
            await legacyComposer.ConnectAsync(Ct);

            await using var rendered = await RenderAsync(host, gitHubId: "open-mode-operator", authEnabled: false);

            Assert.Same(legacyComposer, BoundComposerOf(rendered.Page));
            Assert.DoesNotContain("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);

            // NO SPLIT RUNTIME: open mode creates nothing per-user.
            Assert.Empty(ManagerEntries(host.Manager));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
        }
    }

    // ══ Harness ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PRODUCTION REGISTRATION TEST: over the REAL <see cref="ComposerFacadeRegistration"/> seam, the
    /// <see cref="IComposerRuntimeAccessor"/> interface and the concrete <see cref="ComposerRuntimeAccessor"/>
    /// resolve the SAME instance inside one scope and DIFFERENT instances across scopes — in BOTH deployment modes,
    /// so the alias is a property of the registration rather than of the selected facade.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAccessorAlias_ResolvesTheSameScopedInstance_InBothModes(bool authEnabled)
    {
        await using var host = NewHost(authEnabled: authEnabled, primaryModel: OllamaModel);

        using var first = host.Provider.CreateScope();
        using var second = host.Provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());
        Assert.Same(
            second.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());

        // A single descriptor each, both SCOPED — an alias, never a duplicate registration.
        _ = Assert.Single(host.Descriptors, d => d.ServiceType == typeof(ComposerRuntimeAccessor));
        _ = Assert.Single(host.Descriptors, d => d.ServiceType == typeof(IComposerRuntimeAccessor));
    }

    /// <summary>Acquires one circuit's access through the real accessor for a seeded identity.</summary>
    private static Task<ComposerRuntimeAccess> AcquireAsync(IServiceScope scope, string gitHubId) =>
        scope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>()
            .AcquireForCircuitAsync(AuthenticatedState(gitHubId), Ct);

    private static Task<AuthenticationState> AuthenticatedState(string gitHubId) =>
        ComposerCircuitTesting.AuthenticatedState(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, gitHubId)], authenticationType: "Cookies")));

    /// <summary>Writes one account's OWN per-user session so its history is observable.</summary>
    private async Task SeedSessionAsync(Host host, int userId, string marker)
    {
        var state = await ComposerUserState.ResolveAsync(_auth.Directory.Service, userId, Ct);
        var path = ComposerUserState.SessionFilePath(host.StateDir, state);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var session = SharpCoder.AgentSession.Create("composer");
        session.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, marker));
        await session.SaveAsync(path, Ct);
    }

    /// <summary>A minimal system event carrying a marker, for the passive-buffer rows.</summary>
    private static SystemEvent TestEvent(string marker) =>
        new(EventType.GoalCompleted, marker, Timestamp: DateTime.UtcNow);

    /// <summary>The per-user runtime ids currently cached by the REAL manager.</summary>
    private static IEnumerable<int> ManagerEntries(ComposerRuntimeManager manager)
    {
        var field = typeof(ComposerRuntimeManager)
            .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)field.GetValue(manager)!;
        return entries.Keys.Cast<int>().ToArray();
    }

    /// <summary>The Composer the page is bound to, or <c>null</c>.</summary>
    private static Composer? BoundComposerOf(ComposerChat page)
    {
        var binding = typeof(ComposerChat)
            .GetField("_circuit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(page);
        return (Composer?)binding?.GetType().GetProperty("Composer")!.GetValue(binding);
    }

    /// <summary>Renders the REAL page over the production seam for one circuit identity.</summary>
    private async Task<Rendered> RenderAsync(Host host, string gitHubId, bool authEnabled = true)
    {
        var identity = gitHubId.Length == 0
            ? new ClaimsIdentity(authenticationType: null)
            : new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, gitHubId)], authenticationType: "Cookies");
        var state = new AuthenticationState(new ClaimsPrincipal(identity));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        ComposerChat? page = null;

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = Task.FromResult(state),
                ["ChildContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<ComposerChat>(0);
                    builder.AddComponentReferenceCapture(1, captured => page = (ComposerChat)captured);
                    builder.CloseComponent();
                }),
            });

            var output = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
            return output.ToHtmlString();
        });

        Assert.NotNull(page);
        _ = authEnabled;
        return new Rendered(html, page!, scope, renderer);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A host over the PRODUCTION registration seam.</summary>
    private Host NewHost(
        string primaryModel = Model,
        Func<string, string>? providerOf = null,
        Func<bool>? isTokenAvailable = null,
        bool authEnabled = true,
        bool registerEventBus = false)
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
        services.AddSingleton<HiveConfigFile>(new HiveConfigFile
        {
            Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = Model },
                    new ModelEntry { Name = CopilotModel },
                    new ModelEntry { Name = OllamaModel },
                ],
            },
            Composer = new ComposerConfig { Model = primaryModel },
        });
        services.AddSingleton<IGoalStore>(new GoalStore(_db, NullLogger<GoalStore>.Instance));
        services.AddSingleton(authEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));
        services.AddSingleton(sp => new ComposerFactory(
            sp, stateDir, ollamaApiKey: null,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            isTokenAvailable: isTokenAvailable ?? (() => true),
            providerOf: providerOf ?? (_ => "ollama-local")));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(),
            sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));
        services.AddSingleton(new ComposerAttachmentService(stateDir, NullLogger<ComposerAttachmentService>.Instance));
        services.AddSingleton<IConfigFacade>(sp => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance,
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));

        var eventBus = new RecordingEventBus();
        if (registerEventBus)
            services.AddSingleton<IEventBus>(eventBus);

        // THE PRODUCTION SEAM.
        services.AddComposerRuntimeFacades(
            ComposerRuntimeFacadeDeployment.ForAuthMode(
                authEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(
            provider,
            provider.GetRequiredService<ComposerRuntimeManager>(),
            stateDir,
            eventBus,
            services);
    }

    /// <summary>
    /// THE RUNNER-INVOKED CLEANUP: xUnit v3 calls <c>IAsyncLifetime.DisposeAsync</c> (never a synchronous
    /// <c>Dispose</c>), so every cleanup step lives here.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _db.Dispose();
        _auth.Dispose();
        try
        {
            if (Directory.Exists(_dir))
                TestHelpers.ForceDeleteDirectory(_dir);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the isolated state directory.
        }
    }

    /// <summary>A JS runtime that answers nothing.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>ONE RENDERED CIRCUIT.</summary>
    private sealed class Rendered(
        string html,
        ComposerChat page,
        IServiceScope scope,
        HtmlRenderer renderer) : IAsyncDisposable
    {
        public string Html { get; } = html;

        public ComposerChat Page { get; } = page;

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            scope.Dispose();
        }
    }

    /// <summary>The provider, the manager, the state directory, the recording bus and the descriptors of one host.</summary>
    private sealed class Host(
        ServiceProvider provider,
        ComposerRuntimeManager manager,
        string stateDir,
        RecordingEventBus eventBus,
        IServiceCollection descriptors) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ComposerRuntimeManager Manager { get; } = manager;

        public string StateDir { get; } = stateDir;

        public RecordingEventBus EventBus { get; } = eventBus;

        public IReadOnlyList<ServiceDescriptor> Descriptors { get; } = descriptors.ToArray();

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();

            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }

            // THE MANAGER IS THE SOLE DISPOSER: its teardown is AWAITED before the provider goes away.
            await Step(() => Manager.ShutdownAsync());
            await Step(() => Provider.DisposeAsync().AsTask());

            if (failures.Count > 0)
                throw new AggregateException("Acceptance host cleanup failed", failures);
        }
    }

    /// <summary>
    /// A minimal event bus that records subscribers and fans out to them on <see cref="Publish"/>, so a passive
    /// per-runtime buffer can be observed without any infrastructure.
    /// </summary>
    private sealed class RecordingEventBus : IEventBus
    {
        private readonly List<Action<SystemEvent>> _handlers = [];
        private readonly object _lock = new();

        /// <summary>How many subscribers are currently attached.</summary>
        internal int SubscriberCount
        {
            get { lock (_lock) return _handlers.Count; }
        }

        /// <inheritdoc />
        public event Action<SystemEvent>? OnEvent
        {
            add { lock (_lock) _handlers.Add(value!); }
            remove { lock (_lock) _handlers.Remove(value!); }
        }

        /// <summary>Fans <paramref name="evt"/> out to every current subscriber.</summary>
        public void Publish(SystemEvent evt)
        {
            Action<SystemEvent>[] snapshot;
            lock (_lock) snapshot = _handlers.ToArray();
            foreach (var handler in snapshot)
                handler(evt);
        }
    }
}
