using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE PER-USER RECOVERY MAPPING of <see cref="ComposerRuntimeFacade"/>: every
/// <see cref="ComposerRuntimeStatus"/> the manager can settle on is mapped to ONE defined outcome, and
/// the open-mode legacy fallback is the single exception that never applies to an authenticated refusal.
/// <para>
/// The runtime selection is substituted through <see cref="IComposerRuntimeAccessor"/> because these rows
/// pin the FACADE's mapping, not the manager's lifecycle (which has its own suite). The
/// <see cref="Composer"/> behind each handle is a REAL one, so a row that claims "the operation proceeded"
/// is observed on a real runtime (a real model switch, a real compaction refusal), not on a stub.
/// </para>
/// </summary>
public sealed class ComposerRuntimeFacadeRecoveryTests : IAsyncLifetime, IDisposable
{
    private const string Model = "claude-sonnet-4";
    private const string OtherModel = "claude-opus";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly List<string> _tempDirs = [];
    private readonly List<Composer> _composers = [];

    public ComposerRuntimeFacadeRecoveryTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Seeds the fixture's real directory account (the identity every runtime handle belongs to).</summary>
    public async ValueTask InitializeAsync()
    {
        var account = await _auth.SeedAsync("recovery-member", UserRole.Member);
        _runtimeUserId = account.Id;
        await _auth.SeedAsync("recovery-viewer", UserRole.Viewer);
    }

    /// <summary>No asynchronous teardown of its own: <see cref="Dispose"/> joins everything.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The seeded MEMBER identity a cookie would produce (an activated account).</summary>
    private static ClaimsPrincipal Member { get; } = FacadeAuthorizationHarness.Principal("recovery-member");

    /// <summary>A real Composer over an isolated state directory with its own model catalog.</summary>
    private Composer CreateComposer(params string[] models)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        var composer = new Composer(
            models.Length > 0 ? models[0] : Model,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: models.Length > 0 ? models : [Model, OtherModel],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        _composers.Add(composer);
        return composer;
    }

    /// <summary>A real Composer with NO configured model: the explicit disconnected shell.</summary>
    private Composer CreateShellComposer() => CreateComposerWithModel(null);

    private Composer CreateComposerWithModel(string? model)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        var composer = new Composer(
            model,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: [Model, OtherModel],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        _composers.Add(composer);
        return composer;
    }

    /// <summary>
    /// A real per-user runtime HANDLE around <paramref name="composer"/>. The handle is what the facade
    /// receives from the accessor; the manager owns disposal in production, so these handles wrap composers
    /// this fixture owns and disposes.
    /// </summary>
    private ComposerUserRuntime Handle(Composer composer)
    {
        // A REAL validated per-user identity over this fixture's directory (never a fabricated id).
        var state = ComposerUserState
            .ResolveAsync(_auth.Directory.Service, _runtimeUserId, Ct)
            .GetAwaiter()
            .GetResult();

        return new ComposerUserRuntime(
            state,
            composer,
            new ComposerAttachmentService(Path.GetTempPath(), NullLogger<ComposerAttachmentService>.Instance),
            subscriber: null,
            new ComposerConstructionSnapshot(composer.StartupDefaultModel, compactionModel: null));
    }

    /// <summary>
    /// The validated account id every handle in this fixture is built for. <see cref="InitializeAsync"/>
    /// seeds it through the REAL directory, so the identity is genuine.
    /// </summary>
    private int _runtimeUserId;

    /// <summary>The production legacy facade over <paramref name="composer"/> (the open-mode fallback target).</summary>
    private ComposerFacade LegacyOver(Composer composer) =>
        new(composer, _auth.Resolver(HiveAuthMode.Open), _auth.Policy, NullLogger<ComposerFacade>.Instance);

    private ComposerRuntimeFacade Facade(IComposerRuntimeAccessor accessor, ComposerComposerPair pair) =>
        new(accessor, LegacyOver(pair.Legacy), NullLogger<ComposerRuntimeFacade>.Instance);

    /// <summary>The runtime Composer plus the distinct LEGACY Composer used as the fallback target.</summary>
    private sealed record ComposerComposerPair(Composer Runtime, Composer Legacy);

    private ComposerComposerPair NewPair() => new(CreateComposer(), CreateComposer());

    // ── Connected: the operation proceeds on the selected runtime ────────────────────────────────

    [Fact]
    public async Task Connected_SwitchModel_RunsOnTheSelectedRuntime_AndNeverConnectsFirst()
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(pair.Runtime))),
        };

        var result = await Facade(accessor, pair).SwitchModelAsync(Member, OtherModel, "high", Ct);

        Assert.True(result.Success, result.Error ?? "switch failed");
        Assert.Equal(OtherModel, pair.Runtime.GetStats()?.Model);
        // A connected runtime is already live: NO duplicate connect is issued.
        Assert.Equal(0, accessor.ConnectCalls);
        // The legacy fallback Composer was never touched (it never even connected).
        Assert.Equal(Model, pair.Legacy.StartupDefaultModel);
        Assert.False(pair.Legacy.IsConnected);
    }

    [Fact]
    public async Task Connected_Reads_ReportTheRuntimeValues()
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(pair.Runtime))),
        };

        await pair.Runtime.ConnectAsync(Ct);
        var facade = Facade(accessor, pair);

        var current = await facade.GetCurrentModelAsync(Member, Ct);
        Assert.True(current.Success, current.Error ?? "current-model read failed");
        Assert.Equal(Model, current.Value!.Model);

        var models = await facade.GetModelsAsync(Member, Ct);
        Assert.True(models.Success, models.Error ?? "models read failed");
        Assert.Equal(pair.Runtime.AvailableModels, models.Value!.Models);

        Assert.Equal(0, accessor.ConnectCalls);
    }

    // ── DisconnectedShell ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// READS on a model-less shell return the frozen null-model contract on a SUCCESSFUL result — never a
    /// fabricated catalog value and never a failure.
    /// </summary>
    [Fact]
    public async Task DisconnectedShell_Read_ReturnsNullModelOnSuccess()
    {
        var pair = new ComposerComposerPair(CreateShellComposer(), CreateComposer());
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.DisconnectedShell, Handle(pair.Runtime))),
        };

        var result = await Facade(accessor, pair).GetCurrentModelAsync(Member, Ct);

        Assert.True(result.Success);
        Assert.Null(result.Value!.Model);
        Assert.Equal(0, accessor.ConnectCalls);
    }

    [Fact]
    public async Task DisconnectedShell_ReadModels_ReturnsTheConfiguredCatalog()
    {
        var pair = new ComposerComposerPair(CreateShellComposer(), CreateComposer());
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.DisconnectedShell, Handle(pair.Runtime))),
        };

        var result = await Facade(accessor, pair).GetModelsAsync(Member, Ct);

        Assert.True(result.Success, result.Error ?? "models read failed");
        Assert.Equal([Model, OtherModel], result.Value!.Models);
    }

    /// <summary>
    /// The shell SWITCH PROCEEDS so the actor's first-selection path may connect: the real runtime ends up
    /// connected on the requested model.
    /// </summary>
    [Fact]
    public async Task DisconnectedShell_SwitchModel_ProceedsAgainstTheShell_AndTheFirstSelectionConnects()
    {
        var pair = new ComposerComposerPair(CreateShellComposer(), CreateComposer());
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.DisconnectedShell, Handle(pair.Runtime))),
        };

        Assert.False(pair.Runtime.IsConnected);

        var result = await Facade(accessor, pair).SwitchModelAsync(Member, OtherModel, "medium", Ct);

        Assert.True(result.Success, result.Error ?? "shell switch failed");
        Assert.Equal(OtherModel, result.Value!.Model);
        Assert.True(pair.Runtime.IsConnected, "the shell switch must reach the actor's first-selection connect");
        // The shell switch is a runtime operation, never a manager connect.
        Assert.Equal(0, accessor.ConnectCalls);
    }

    /// <summary>
    /// COMPACTION on a model-less shell is a DEFINED OPERATIONAL result: the runtime itself refuses because it
    /// has no agent, so the facade reports the pre-facade 400 carrying the runtime's own message — NOT an
    /// invented 503 and NOT a silent success.
    /// </summary>
    [Fact]
    public async Task DisconnectedShell_Compaction_ReturnsTheOperationalBadRequest_NotA503()
    {
        var pair = new ComposerComposerPair(CreateShellComposer(), CreateComposer());
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.DisconnectedShell, Handle(pair.Runtime))),
        };

        var facade = Facade(accessor, pair);

        var full = await facade.CompactAsync(Member, Ct);
        var partial = await facade.CompactPartialAsync(Member, 50, Ct);
        foreach (var (success, kind, error) in new[]
        {
            (full.Success, full.Kind, full.Error),
            (partial.Success, partial.Kind, partial.Error),
        })
        {
            Assert.False(success);
            Assert.Equal(FacadeErrorKind.BadRequest, kind);
            Assert.Equal("Composer not connected. Call ConnectAsync first.", error);
            Assert.NotEqual(FacadeErrorKind.ServiceUnavailable, kind);
        }

        Assert.Equal(0, accessor.ConnectCalls);
    }

    // ── Deferred ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A READ never connects: a deferred runtime answers with shell metadata.</summary>
    [Fact]
    public async Task Deferred_Read_ReturnsShellMetadata_WithoutConnecting()
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Deferred, Handle(pair.Runtime))),
        };

        var facade = Facade(accessor, pair);

        var current = await facade.GetCurrentModelAsync(Member, Ct);
        Assert.True(current.Success);
        Assert.Null(current.Value!.Model); // a never-connected runtime has no stats: shell metadata

        var models = await facade.GetModelsAsync(Member, Ct);
        Assert.True(models.Success, models.Error ?? "models read failed");
        Assert.Equal([Model, OtherModel], models.Value!.Models);

        Assert.Equal(0, accessor.ConnectCalls);
    }

    /// <summary>
    /// A MUTATION on a deferred runtime attempts the operation-driven connect. When the manager reports
    /// CONNECTED the mutation proceeds on the now-live runtime.
    /// </summary>
    [Fact]
    public async Task Deferred_Mutation_ConnectsOnce_ThenProceeds()
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Deferred, Handle(pair.Runtime))),
            // The connect really connects the runtime and reports the reconciled Connected status.
            Connect = async _ =>
            {
                await pair.Runtime.ConnectAsync(Ct);
                return ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(pair.Runtime)));
            },
        };

        var result = await Facade(accessor, pair).SwitchModelAsync(Member, OtherModel, "medium", Ct);

        Assert.True(result.Success, result.Error ?? "deferred mutation failed");
        Assert.Equal(1, accessor.ConnectCalls);
        Assert.True(pair.Runtime.IsConnected);
        Assert.Equal(OtherModel, pair.Runtime.GetStats()?.Model);
    }

    /// <summary>
    /// If the runtime is STILL deferred after the operation's own connect attempt, the mutation answers the
    /// actionable <see cref="FacadeErrorKind.ServiceUnavailable"/> — exactly one attempt, never a loop.
    /// </summary>
    [Fact]
    public async Task Deferred_Mutation_StillDeferred_ReturnsActionableServiceUnavailable()
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Deferred, Handle(pair.Runtime))),
            Connect = _ => Task.FromResult(ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Deferred, Handle(pair.Runtime)))),
        };

        var facade = Facade(accessor, pair);

        var switched = await facade.SwitchModelAsync(Member, OtherModel, "medium", Ct);
        var compacted = await facade.CompactAsync(Member, Ct);
        foreach (var (success, kind, error) in new[]
        {
            (switched.Success, switched.Kind, switched.Error),
            (compacted.Success, compacted.Kind, compacted.Error),
        })
        {
            Assert.False(success);
            Assert.Equal(FacadeErrorKind.ServiceUnavailable, kind);
            Assert.Equal(ComposerRuntimeFacade.RuntimeDeferredError, error);
            // Actionable and secret-free: it names what the caller can do, no account/provider internals.
            Assert.Contains("retry", error!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", error!, StringComparison.OrdinalIgnoreCase);
        }

        // ONE connect attempt per operation (two operations ⇒ two attempts), never a retry loop.
        Assert.Equal(2, accessor.ConnectCalls);
        Assert.Equal(Model, pair.Runtime.GetStats()?.Model ?? Model); // untouched: still not connected
        Assert.False(pair.Runtime.IsConnected);
    }

    // ── Failed ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A FAILED runtime answers <see cref="FacadeErrorKind.ServiceUnavailable"/> with a SANITIZED message: the
    /// fault text (which may name internal provider state) stays server-side.
    /// </summary>
    [Fact]
    public async Task Failed_ReturnsServiceUnavailable_WithASanitizedMessage()
    {
        var pair = NewPair();
        const string secretish = "provider 500: https://internal.example/token-abc";
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Faulted(new InvalidOperationException(secretish))),
        };

        var facade = Facade(accessor, pair);

        var current = await facade.GetCurrentModelAsync(Member, Ct);
        var switched = await facade.SwitchModelAsync(Member, OtherModel, "medium", Ct);
        var compacted = await facade.CompactAsync(Member, Ct);
        var partial = await facade.CompactPartialAsync(Member, 50, Ct);
        foreach (var (success, kind, error) in new[]
        {
            (current.Success, current.Kind, current.Error),
            (switched.Success, switched.Kind, switched.Error),
            (compacted.Success, compacted.Kind, compacted.Error),
            (partial.Success, partial.Kind, partial.Error),
        })
        {
            Assert.False(success);
            Assert.Equal(FacadeErrorKind.ServiceUnavailable, kind);
            Assert.Equal(ComposerRuntimeFacade.RuntimeUnavailableError, error);
            Assert.DoesNotContain("internal.example", error!, StringComparison.Ordinal);
            Assert.DoesNotContain("token", error!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secretish, error!, StringComparison.Ordinal);
        }

        Assert.Equal(0, accessor.ConnectCalls);
    }

    // ── Removed ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A REMOVED handle is re-obtained from the manager ONCE (the accessor is never disposed — only the manager
    /// tears a runtime down), so a removed/recreated runtime is picked up transparently.
    /// </summary>
    [Fact]
    public async Task Removed_IsRequeredOnce_AndTheFreshRuntimeServesTheOperation()
    {
        var pair = NewPair();
        var acquisitions = 0;
        var accessor = new StubAccessor
        {
            Acquire = (_, _) =>
            {
                // First selection sees the stale handle, the second the recreated runtime.
                if (Interlocked.Increment(ref acquisitions) == 1)
                    return ComposerRuntimeAccess.Granted(ComposerRuntimeResult.RemovedResult());

                return ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(pair.Runtime)));
            },
        };

        var result = await Facade(accessor, pair).SwitchModelAsync(Member, OtherModel, "high", Ct);

        Assert.True(result.Success, result.Error ?? "requery failed");
        Assert.Equal(2, acquisitions);
        Assert.Equal(OtherModel, pair.Runtime.GetStats()?.Model);
    }

    /// <summary>A SECOND <see cref="ComposerRuntimeStatus.Removed"/> is a conflict with a safe retry message.</summary>
    [Fact]
    public async Task RemovedTwice_ReturnsConflict_WithARetryMessage()
    {
        var pair = NewPair();
        var acquisitions = 0;
        var accessor = new StubAccessor
        {
            Acquire = (_, _) =>
            {
                Interlocked.Increment(ref acquisitions);
                return ComposerRuntimeAccess.Granted(ComposerRuntimeResult.RemovedResult());
            },
        };

        var facade = Facade(accessor, pair);

        var read = await facade.GetCurrentModelAsync(Member, Ct);
        var compact = await facade.CompactAsync(Member, Ct);
        foreach (var (success, kind, error) in new[]
        {
            (read.Success, read.Kind, read.Error),
            (compact.Success, compact.Kind, compact.Error),
        })
        {
            Assert.False(success);
            Assert.Equal(FacadeErrorKind.Conflict, kind);
            Assert.Equal(ComposerRuntimeFacade.RuntimeRemovedError, error);
            Assert.Contains("Retry", error!, StringComparison.Ordinal);
        }

        // ONE requery per operation: the facade never loops on a removal.
        Assert.Equal(4, acquisitions);
    }

    // ── Refused ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A VIEWER (or any refused identity) gets <see cref="FacadeErrorKind.Forbidden"/> and NO content, on every
    /// operation — and the manager is never asked for a runtime at all.
    /// </summary>
    [Theory]
    [InlineData("current")]
    [InlineData("models")]
    [InlineData("switch")]
    [InlineData("compact")]
    [InlineData("compact-partial")]
    public async Task Unauthorized_ReturnsForbidden_OnEveryOperation_WithoutAskingTheManager(string operation)
    {
        var pair = NewPair();
        var accessor = new StubAccessor { Acquire = (_, _) => ComposerRuntimeAccess.Denied() };

        var facade = Facade(accessor, pair);
        var result = operation switch
        {
            "current" => (await facade.GetCurrentModelAsync(Member, Ct)).Kind,
            "models" => (await facade.GetModelsAsync(Member, Ct)).Kind,
            "switch" => (await facade.SwitchModelAsync(Member, OtherModel, "high", Ct)).Kind,
            "compact" => (await facade.CompactAsync(Member, Ct)).Kind,
            "compact-partial" => (await facade.CompactPartialAsync(Member, 50, Ct)).Kind,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

        Assert.Equal(FacadeErrorKind.Forbidden, result);
        // EXACTLY ONE authorization question per operation, and never a connect attempt.
        Assert.Equal(1, accessor.ResultAsks);
        Assert.Equal(0, accessor.ConnectCalls);
        // The legacy fallback Composer was never used for an unauthorized caller.
        Assert.Equal(Model, pair.Legacy.StartupDefaultModel);
        Assert.False(pair.Legacy.IsConnected);
    }

    /// <summary>
    /// THE OPEN-MODE FALLBACK IS EXPLICIT AND WORKS: with authentication disabled the synthetic local operator
    /// has no persisted account, the manager refuses it, and the operation is served by the LEGACY singleton
    /// Composer. The positive control for the refusal rows below.
    /// </summary>
    [Fact]
    public async Task OpenModeLocalOperatorRefusal_FallsBackToTheLegacyComposer()
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            AuthEnabled = false,
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.LocalOperator)),
        };

        var result = await Facade(accessor, pair).SwitchModelAsync(Member, OtherModel, "high", Ct);

        Assert.True(result.Success, result.Error ?? "open-mode fallback failed");
        Assert.Equal(OtherModel, pair.Legacy.GetStats()?.Model); // the LEGACY composer served it
        Assert.False(pair.Runtime.IsConnected); // the per-user runtime was not used at all
        Assert.Null(pair.Runtime.GetStats()?.Model);
    }

    /// <summary>
    /// AN AUTHENTICATED REFUSAL NEVER FALLS BACK: the SAME refusal in authentication-enabled mode is a 403,
    /// and the legacy Composer is untouched. Without this pair the fallback could silently rescue a refused
    /// account into the original admin's runtime.
    /// </summary>
    [Fact]
    public async Task AuthenticatedRefusal_NeverFallsBackToTheLegacyComposer()
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            AuthEnabled = true,
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.LocalOperator)),
        };

        var result = await Facade(accessor, pair).SwitchModelAsync(Member, OtherModel, "high", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(Model, pair.Legacy.StartupDefaultModel); // untouched
        Assert.False(pair.Legacy.IsConnected);
    }

    /// <summary>
    /// EVEN IN OPEN MODE, only the LOCAL-OPERATOR refusal falls back: any other refusal is a 403. The gate is
    /// the refusal TYPE, not the mode alone.
    /// </summary>
    [Theory]
    [InlineData(ComposerRuntimeRefusal.DisabledAccount)]
    [InlineData(ComposerRuntimeRefusal.UndefinedRole)]
    [InlineData(ComposerRuntimeRefusal.UnknownAccount)]
    [InlineData(ComposerRuntimeRefusal.ShuttingDown)]
    public async Task OpenMode_WithANonLocalOperatorRefusal_IsForbidden_AndDoesNotFallBack(
        ComposerRuntimeRefusal refusal)
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            AuthEnabled = false,
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(ComposerRuntimeResult.Refused(refusal)),
        };

        var result = await Facade(accessor, pair).SwitchModelAsync(Member, OtherModel, "high", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(Model, pair.Legacy.StartupDefaultModel);
        Assert.False(pair.Legacy.IsConnected);
    }

    // ── Caller cancellation always propagates ───────────────────────────────────────────────────

    /// <summary>
    /// CALLER CANCELLATION IS NEVER MAPPED: a cancelled selection propagates out of the facade unchanged, so a
    /// cancelled request can never be reported as 403/503/409/400.
    /// </summary>
    [Fact]
    public async Task CallerCancellationDuringSelection_Propagates_AndIsNeverMapped()
    {
        var pair = NewPair();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var accessor = new StubAccessor
        {
            Acquire = (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(pair.Runtime)));
            },
        };

        var facade = Facade(accessor, pair);

        foreach (var operation in new Func<Task>[]
        {
            () => facade.GetCurrentModelAsync(Member, cts.Token),
            () => facade.SwitchModelAsync(Member, OtherModel, "high", cts.Token),
            () => facade.CompactAsync(Member, cts.Token),
            () => facade.CompactPartialAsync(Member, 50, cts.Token),
        })
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
        }
    }

    /// <summary>
    /// CALLER CANCELLATION DURING THE OPERATION ITSELF PROPAGATES TOO: a selection cancelled at the real
    /// runtime (the actor classifies the cancellation and rejects the reply) surfaces unchanged out of the
    /// facade — it is never converted into a 400 by the operation shaping, nor into a recovery status.
    /// </summary>
    [Fact]
    public async Task CallerCancellationDuringAConnectedOperation_Propagates()
    {
        var pair = NewPair();
        await pair.Runtime.ConnectAsync(Ct);
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(pair.Runtime))),
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Facade(accessor, pair).SwitchModelAsync(Member, OtherModel, "high", cts.Token));

        // The cancelled selection changed nothing.
        Assert.Equal(Model, pair.Runtime.GetStats()?.Model);
    }

    // ── Identity path ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE PER-REQUEST PATH: the facade hands the caller's <see cref="ClaimsPrincipal"/> straight to the
    /// accessor — no circuit <see cref="AuthenticationState"/> and no HttpContext is involved anywhere on the
    /// facade/accessor boundary.
    /// </summary>
    [Fact]
    public async Task EveryOperation_ForwardsTheRequestPrincipal_ToTheAccessor()
    {
        var pair = NewPair();
        var seen = new List<ClaimsPrincipal?>();
        var accessor = new StubAccessor
        {
            Acquire = (principal, _) =>
            {
                seen.Add(principal);
                return ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(pair.Runtime)));
            },
        };

        await pair.Runtime.ConnectAsync(Ct);
        var facade = Facade(accessor, pair);

        await facade.GetCurrentModelAsync(Member, Ct);
        await facade.GetModelsAsync(Member, Ct);
        await facade.SwitchModelAsync(Member, OtherModel, "high", Ct);
        await facade.CompactAsync(Member, Ct);
        await facade.CompactPartialAsync(Member, 50, Ct);

        Assert.Equal(5, seen.Count);
        Assert.All(seen, principal => Assert.Same(Member, principal));
    }

    /// <summary>
    /// THE FACADE NEVER REACHES FOR AN HttpContext: it holds only the accessor, the legacy facade and a logger,
    /// so no request-scoped interactive state can be captured by it.
    /// </summary>
    [Fact]
    public void Facade_HoldsOnlyTheAccessorTheLegacyFacadeAndALogger()
    {
        var fieldTypes = typeof(ComposerRuntimeFacade)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Select(f => f.FieldType)
            .ToArray();

        Assert.Contains(typeof(IComposerRuntimeAccessor), fieldTypes);
        Assert.Contains(typeof(ComposerFacade), fieldTypes);
        Assert.DoesNotContain(typeof(Microsoft.AspNetCore.Http.HttpContext), fieldTypes);
        Assert.DoesNotContain(typeof(ComposerRuntimeManager), fieldTypes);
        Assert.DoesNotContain(typeof(CircuitUserPrincipalAccessor), fieldTypes);
        Assert.DoesNotContain(typeof(ActingUserPrincipal), fieldTypes);
    }

    public void Dispose()
    {
        // Every composer this fixture created is disposed here; the facade never disposes a runtime.
        foreach (var composer in _composers)
        {
            try { composer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception) { /* best effort: the manager owns production disposal */ }
        }

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

    /// <summary>
    /// A substitute runtime access seam: it records what the facade asked for and answers with the
    /// <see cref="ComposerRuntimeAccess"/> the row under test prescribes.
    /// </summary>
    private sealed class StubAccessor : IComposerRuntimeAccessor
    {
        private int _resultAsks;

        public bool AuthEnabled { get; init; } = true;

        /// <summary>The authorization answer for a request-scoped acquisition (the caller's token is honoured).</summary>
        public Func<ClaimsPrincipal?, CancellationToken, ComposerRuntimeAccess> Acquire { get; init; } =
            (_, _) => ComposerRuntimeAccess.Denied();

        /// <summary>The answer for the operation-driven connect (asynchronous: a real connect really runs).</summary>
        public Func<CancellationToken, Task<ComposerRuntimeAccess>>? Connect { get; init; }

        /// <summary>How many times the facade asked the manager for a runtime.</summary>
        public int ResultAsks => Volatile.Read(ref _resultAsks);

        /// <summary>How many times the facade attempted the operation-driven connect.</summary>
        public int ConnectCalls { get; private set; }

        public Task<ComposerRuntimeAccess> AcquireForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            Interlocked.Increment(ref _resultAsks);
            return Task.FromResult(Acquire(actingUser, ct));
        }

        public async Task<ComposerRuntimeAccess> ConnectForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            ConnectCalls++;
            return Connect is null
                ? ComposerRuntimeAccess.Granted(ComposerRuntimeResult.RemovedResult())
                : await Connect(ct);
        }

        public Task<ComposerRuntimeAccess> AcquireForCircuitAsync(
            Task<AuthenticationState>? authenticationState, CancellationToken ct)
        {
            Interlocked.Increment(ref _resultAsks);
            return Task.FromResult(Acquire(null, ct));
        }

        public async Task<ComposerRuntimeAccess> ConnectForCircuitAsync(
            Task<AuthenticationState>? authenticationState, CancellationToken ct)
        {
            ConnectCalls++;
            return Connect is null
                ? ComposerRuntimeAccess.Granted(ComposerRuntimeResult.RemovedResult())
                : await Connect(ct);
        }
    }

}
