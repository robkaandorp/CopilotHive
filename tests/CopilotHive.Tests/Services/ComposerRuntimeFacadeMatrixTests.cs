using System.Security.Claims;

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
/// THE COMPLETE RECOVERY MAPPING MATRIX of <see cref="ComposerRuntimeFacade"/>: EVERY
/// <see cref="ComposerRuntimeStatus"/> the manager can settle on, crossed with EVERY operation
/// class (the two reads, the switch, and the two compactions).
/// <para>
/// The rows that already existed in <c>ComposerRuntimeFacadeRecoveryTests</c> are kept there; this
/// class completes the CROSS-PRODUCT so that no (outcome × operation) cell is unasserted. Each cell
/// asserts the exact <see cref="FacadeErrorKind"/> and — for the recovery failures — the exact
/// sanitized message, so a cell cannot be satisfied by a merely-plausible kind.
/// </para>
/// <para>
/// The <see cref="Composer"/> behind each handle is a REAL one, so a cell that claims "the operation
/// proceeded" is observed on a real runtime (a real model switch, a real compaction refusal).
/// </para>
/// </summary>
public sealed class ComposerRuntimeFacadeMatrixTests : IAsyncLifetime, IDisposable
{
    private const string Model = "matrix-model";
    private const string OtherModel = "matrix-other-model";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly List<string> _tempDirs = [];
    private readonly List<Composer> _composers = [];

    private int _runtimeUserId;

    public ComposerRuntimeFacadeMatrixTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The seeded MEMBER identity a cookie would produce (an activated account).</summary>
    private static ClaimsPrincipal Member { get; } = FacadeAuthorizationHarness.Principal("matrix-member");

    public async ValueTask InitializeAsync()
    {
        var account = await _auth.SeedAsync("matrix-member", UserRole.Member);
        _runtimeUserId = account.Id;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── The operation cells ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE OPERATION CLASSES: the two reads, the switch and the two compactions. Every cell of the
    /// matrix below runs over ALL of them.
    /// </summary>
    public enum Operation
    {
        ReadCurrentModel,
        ReadModels,
        SwitchModel,
        Compact,
        CompactPartial,
    }

    /// <summary>Runs one operation against <paramref name="facade"/> and reports its outcome.</summary>
    private static async Task<(bool Success, FacadeErrorKind Kind, string? Error)> RunAsync(
        IComposerFacade facade, Operation operation)
    {
        switch (operation)
        {
            case Operation.ReadCurrentModel:
            {
                var result = await facade.GetCurrentModelAsync(Member, Ct);
                return (result.Success, result.Kind, result.Error);
            }
            case Operation.ReadModels:
            {
                var result = await facade.GetModelsAsync(Member, Ct);
                return (result.Success, result.Kind, result.Error);
            }
            case Operation.SwitchModel:
            {
                var result = await facade.SwitchModelAsync(Member, OtherModel, "medium", Ct);
                return (result.Success, result.Kind, result.Error);
            }
            case Operation.Compact:
            {
                var result = await facade.CompactAsync(Member, Ct);
                return (result.Success, result.Kind, result.Error);
            }
            case Operation.CompactPartial:
            {
                var result = await facade.CompactPartialAsync(Member, 50, Ct);
                return (result.Success, result.Kind, result.Error);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation.");
        }
    }

    /// <summary>Every operation of the matrix, in a stable order.</summary>
    private static readonly Operation[] AllOperations =
    [
        Operation.ReadCurrentModel,
        Operation.ReadModels,
        Operation.SwitchModel,
        Operation.Compact,
        Operation.CompactPartial,
    ];

    // ── FAILED × every operation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A FAILED RUNTIME answers <see cref="FacadeErrorKind.ServiceUnavailable"/> on EVERY operation
    /// class — including the catalog read, which has no other failure kind — with the fixed
    /// sanitized message. The fault text stays server-side.
    /// </summary>
    [Theory]
    [InlineData(Operation.ReadCurrentModel)]
    [InlineData(Operation.ReadModels)]
    [InlineData(Operation.SwitchModel)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task Failed_AnswersServiceUnavailable_OnEveryOperation(Operation operation)
    {
        var pair = NewPair();
        const string secretish = "provider 500: https://internal.example/token-abc";
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Faulted(new InvalidOperationException(secretish))),
        };

        var (success, kind, error) = await RunAsync(Facade(accessor, pair), operation);

        Assert.False(success);
        Assert.Equal(FacadeErrorKind.ServiceUnavailable, kind);
        Assert.Equal(ComposerRuntimeFacade.RuntimeUnavailableError, error);
        Assert.DoesNotContain("internal.example", error!, StringComparison.Ordinal);
        Assert.DoesNotContain("token", error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretish, error!, StringComparison.Ordinal);
        // A failure never attempts a connect and never touches the legacy Composer.
        Assert.Equal(0, accessor.ConnectCalls);
        Assert.False(pair.Legacy.IsConnected);
    }

    // ── DEFERRED × every operation ────────────────────────────────────────────────────────────

    /// <summary>
    /// A DEFERRED RUNTIME answers the two READS with shell metadata (a read never connects), and
    /// every MUTATION first attempts the operation-driven connect and then answers
    /// <see cref="FacadeErrorKind.ServiceUnavailable"/> with the actionable, secret-free message
    /// when the connect is still deferred.
    /// </summary>
    [Fact]
    public async Task Deferred_ReadsAnswerShellMetadata_AndMutationsAnswerActionableServiceUnavailable()
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

        foreach (var operation in new[] { Operation.ReadCurrentModel, Operation.ReadModels })
        {
            var (success, kind, error) = await RunAsync(facade, operation);
            Assert.True(success, $"{operation} must answer shell metadata");
            Assert.Equal(FacadeErrorKind.None, kind);
            Assert.Null(error);
        }

        // The reads never connect.
        Assert.Equal(0, accessor.ConnectCalls);

        foreach (var operation in new[] { Operation.SwitchModel, Operation.Compact, Operation.CompactPartial })
        {
            var (success, kind, error) = await RunAsync(facade, operation);
            Assert.False(success);
            Assert.Equal(FacadeErrorKind.ServiceUnavailable, kind);
            Assert.Equal(ComposerRuntimeFacade.RuntimeDeferredError, error);
            Assert.Contains("retry", error!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", error!, StringComparison.OrdinalIgnoreCase);
        }

        // EXACTLY ONE connect attempt per mutation — three mutations, three attempts, no retry loop.
        Assert.Equal(3, accessor.ConnectCalls);
    }

    // ── REMOVED × every operation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A REMOVED HANDLE IS RE-QUERIED ONCE per operation: the first selection reports the stale
    /// handle and the second reports the recreated runtime, so the operation proceeds on the fresh
    /// runtime. Exactly one requery per operation — the facade never loops on a removal.
    /// </summary>
    [Theory]
    [InlineData(Operation.ReadCurrentModel)]
    [InlineData(Operation.ReadModels)]
    [InlineData(Operation.SwitchModel)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task Removed_IsRequeriedExactlyOnce_PerOperation(Operation operation)
    {
        var pair = NewPair();
        // The recreated runtime is a LIVE one: the operation must be served by it, so the
        // compaction cells are not satisfied by a "not connected" operational failure.
        await pair.Runtime.ConnectAsync(Ct);
        var acquisitions = 0;
        var accessor = new StubAccessor
        {
            Acquire = (_, _) =>
            {
                if (Interlocked.Increment(ref acquisitions) == 1)
                    return ComposerRuntimeAccess.Granted(ComposerRuntimeResult.RemovedResult());

                return ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(pair.Runtime)));
            },
        };

        var (success, kind, error) = await RunAsync(Facade(accessor, pair), operation);

        Assert.True(success, error ?? $"{operation} must be served by the recreated runtime");
        Assert.Equal(FacadeErrorKind.None, kind);
        Assert.Equal(2, acquisitions);
        Assert.Equal(0, accessor.ConnectCalls);
    }

    /// <summary>
    /// A SECOND <see cref="ComposerRuntimeStatus.Removed"/> answers <see cref="FacadeErrorKind.Conflict"/>
    /// with the safe retry/requery message on EVERY operation class — never a 400, never a 503 and
    /// never a silent success.
    /// </summary>
    [Theory]
    [InlineData(Operation.ReadCurrentModel)]
    [InlineData(Operation.ReadModels)]
    [InlineData(Operation.SwitchModel)]
    [InlineData(Operation.Compact)]
    [InlineData(Operation.CompactPartial)]
    public async Task RemovedTwice_AnswersConflict_WithTheRetryMessage_OnEveryOperation(Operation operation)
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(ComposerRuntimeResult.RemovedResult()),
        };

        var (success, kind, error) = await RunAsync(Facade(accessor, pair), operation);

        Assert.False(success);
        Assert.Equal(FacadeErrorKind.Conflict, kind);
        Assert.Equal(ComposerRuntimeFacade.RuntimeRemovedError, error);
        Assert.Contains("Retry", error!, StringComparison.Ordinal);
        // One requery per operation: TWO selections in total, never a loop.
        Assert.Equal(2, accessor.ResultAsks);
    }

    // ── CONNECTED × every operation ────────────────────────────────────────────────────────────

    /// <summary>
    /// A CONNECTED RUNTIME SERVES EVERY OPERATION on the SELECTED runtime itself — the reads
    /// report its values, the switch really changes its model, and the compactions run against it.
    /// The legacy Composer is never touched.
    /// </summary>
    [Fact]
    public async Task Connected_ServesEveryOperation_OnTheSelectedRuntime()
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

        var switched = await facade.SwitchModelAsync(Member, OtherModel, "medium", Ct);
        Assert.True(switched.Success, switched.Error ?? "switch failed");
        Assert.Equal(OtherModel, pair.Runtime.GetStats()?.Model);

        // The compactions run against the CONNECTED runtime: they are a SUCCESSFUL operation
        // (there is simply nothing to compact), never a recovery failure.
        foreach (var operation in new[] { Operation.Compact, Operation.CompactPartial })
        {
            var (success, kind, error) = await RunAsync(facade, operation);
            Assert.True(success, $"{operation} on a connected runtime must succeed");
            Assert.Equal(FacadeErrorKind.None, kind);
            Assert.Null(error);
        }

        // A live runtime is never re-connected.
        Assert.Equal(0, accessor.ConnectCalls);
        Assert.False(pair.Legacy.IsConnected);
    }

    /// <summary>
    /// THE OPERATIONAL BadRequest PATH IS UNCHANGED ON THE RECOVERY FACADE: a switch without a model
    /// is the facade's own validation and answers <see cref="FacadeErrorKind.BadRequest"/> — it is
    /// never remapped into a recovery status.
    /// </summary>
    [Fact]
    public async Task Connected_SwitchWithoutAModel_KeepsTheOperationalBadRequest()
    {
        var pair = NewPair();
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(pair.Runtime))),
        };

        await pair.Runtime.ConnectAsync(Ct);
        var result = await Facade(accessor, pair).SwitchModelAsync(Member, null, "medium", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("The 'model' query parameter is required.", result.Error);
        Assert.NotEqual(FacadeErrorKind.ServiceUnavailable, result.Kind);
        Assert.NotEqual(FacadeErrorKind.Conflict, result.Kind);
    }

    // ── DISCONNECTED SHELL × every operation ──────────────────────────────────────────────────

    /// <summary>
    /// A MODEL-LESS SHELL answers the READS with the frozen null-model contract and the configured
    /// catalog, and the COMPACTIONS with the DEFINED OPERATIONAL answer — the runtime's own
    /// "not connected" message as a <see cref="FacadeErrorKind.BadRequest"/>, never an invented 503.
    /// </summary>
    [Fact]
    public async Task DisconnectedShell_ReadsAnswerShellMetadata_AndCompactionKeepsTheOperationalBadRequest()
    {
        var pair = new ComposerComposerPair(CreateShellComposer(), CreateComposer());
        var accessor = new StubAccessor
        {
            Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.DisconnectedShell, Handle(pair.Runtime))),
        };

        var facade = Facade(accessor, pair);

        var current = await facade.GetCurrentModelAsync(Member, Ct);
        Assert.True(current.Success);
        Assert.Null(current.Value!.Model);

        var models = await facade.GetModelsAsync(Member, Ct);
        Assert.True(models.Success, models.Error ?? "models read failed");
        Assert.Equal([Model, OtherModel], models.Value!.Models);

        foreach (var operation in new[] { Operation.Compact, Operation.CompactPartial })
        {
            var (success, kind, error) = await RunAsync(facade, operation);
            Assert.False(success);
            Assert.Equal(FacadeErrorKind.BadRequest, kind);
            Assert.Equal("Composer not connected. Call ConnectAsync first.", error);
            Assert.NotEqual(FacadeErrorKind.ServiceUnavailable, kind);
        }

        Assert.Equal(0, accessor.ConnectCalls);
    }

    // ── The matrix itself ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE MATRIX, AS A TABLE: every (outcome × operation) cell has a DEFINED answer, and the
    /// table below is asserted cell by cell against the real facade. The table is the SPEC; this
    /// test proves the implementation matches it.
    /// </summary>
    [Fact]
    public async Task EveryOutcomeOperationCell_HasTheDefinedAnswer()
    {
        // (status, isMutation) → the expected kind for a READ and for a MUTATION.
        //
        // Two cells deserve their own note:
        //  * DISCONNECTED SHELL + MUTATION is the DEFINED OPERATIONAL answer (BadRequest), not a
        //    recovery status: a model-less shell is not a service outage, so no 503 is invented.
        //    The switch is the exception within that cell — it PROCEEDS against the shell so the
        //    actor's first selection may connect it.
        //  * DEFERRED + READ answers the shell metadata (a read never connects), while DEFERRED +
        //    MUTATION answers the actionable ServiceUnavailable after its own connect attempt.
        var expected = new Dictionary<(ComposerRuntimeStatus Status, bool IsMutation), (FacadeErrorKind Read, FacadeErrorKind Mutation)>
        {
            [(ComposerRuntimeStatus.Connected, false)] = (FacadeErrorKind.None, FacadeErrorKind.None),
            [(ComposerRuntimeStatus.DisconnectedShell, false)] = (FacadeErrorKind.None, FacadeErrorKind.BadRequest),
            [(ComposerRuntimeStatus.Deferred, false)] = (FacadeErrorKind.None, FacadeErrorKind.ServiceUnavailable),
            [(ComposerRuntimeStatus.Failed, false)] = (FacadeErrorKind.ServiceUnavailable, FacadeErrorKind.ServiceUnavailable),
            [(ComposerRuntimeStatus.Removed, false)] = (FacadeErrorKind.Conflict, FacadeErrorKind.Conflict),
            [(ComposerRuntimeStatus.Refused, false)] = (FacadeErrorKind.Forbidden, FacadeErrorKind.Forbidden),
        };

        foreach (var ((status, _), kinds) in expected)
        {
            foreach (var operation in AllOperations)
            {
                var isMutation = operation is Operation.SwitchModel or Operation.Compact or Operation.CompactPartial;
                var pair = NewPair();
                if (status == ComposerRuntimeStatus.DisconnectedShell)
                {
                    // The shell cells need a REAL model-less shell: the switch PROCEEDS against it,
                    // while the compactions observe the runtime's own operational refusal.
                    pair = new ComposerComposerPair(CreateShellComposer(), CreateComposer());
                }
                else if (status == ComposerRuntimeStatus.Connected)
                {
                    // The connected cells need a REAL live runtime, so a compaction is served as a
                    // successful operation rather than an operational "not connected" failure.
                    await pair.Runtime.ConnectAsync(Ct);
                }

                var accessor = new StubAccessor
                {
                    Acquire = (_, _) => ComposerRuntimeAccess.Granted(
                        status == ComposerRuntimeStatus.Refused
                            ? ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.UnknownAccount)
                            : ComposerRuntimeResult.Granted(status, Handle(pair.Runtime))),
                    Connect = _ => Task.FromResult(ComposerRuntimeAccess.Granted(
                        status == ComposerRuntimeStatus.Refused
                            ? ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.UnknownAccount)
                            : ComposerRuntimeResult.Granted(status, Handle(pair.Runtime)))),
                };

                var (success, kind, _) = await RunAsync(Facade(accessor, pair), operation);

                var wanted = isMutation ? kinds.Mutation : kinds.Read;

                if (status == ComposerRuntimeStatus.DisconnectedShell && operation == Operation.SwitchModel)
                {
                    // THE SWITCH PROCEEDS against the shell so the actor's first-selection path may
                    // connect it — it is the one mutation that is NOT a failure on a shell.
                    Assert.True(success);
                    Assert.Equal(FacadeErrorKind.None, kind);
                    continue;
                }

                Assert.Equal(wanted, kind);
                Assert.Equal(wanted == FacadeErrorKind.None, success);
            }
        }
    }

    // ── Fixture ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A real Composer over an isolated state directory with its own model catalog.</summary>
    private Composer CreateComposer(params string[] models)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-matrix-{Guid.NewGuid():N}");
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
    private Composer CreateShellComposer()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-matrix-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        var composer = new Composer(
            null,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: [Model, OtherModel],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        _composers.Add(composer);
        return composer;
    }

    /// <summary>
    /// A real per-user runtime HANDLE around <paramref name="composer"/>. The handle is what the
    /// facade receives from the accessor; the manager owns disposal in production, so these handles
    /// wrap composers this fixture owns and disposes.
    /// </summary>
    private ComposerUserRuntime Handle(Composer composer)
    {
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

    /// <summary>The production legacy facade over <paramref name="composer"/> (the open-mode fallback target).</summary>
    private ComposerFacade LegacyOver(Composer composer) =>
        new(composer, _auth.Resolver(HiveAuthMode.Open), _auth.Policy, NullLogger<ComposerFacade>.Instance);

    private ComposerRuntimeFacade Facade(IComposerRuntimeAccessor accessor, ComposerComposerPair pair) =>
        new(accessor, LegacyOver(pair.Legacy), NullLogger<ComposerRuntimeFacade>.Instance);

    /// <summary>The runtime Composer plus the distinct LEGACY Composer used as the fallback target.</summary>
    private sealed record ComposerComposerPair(Composer Runtime, Composer Legacy);

    private ComposerComposerPair NewPair() => new(CreateComposer(), CreateComposer());

    public void Dispose()
    {
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

        /// <summary>The authorization answer for a request-scoped acquisition.</summary>
        public Func<ClaimsPrincipal?, CancellationToken, ComposerRuntimeAccess> Acquire { get; init; } =
            (_, _) => ComposerRuntimeAccess.Denied();

        /// <summary>The answer for the operation-driven connect.</summary>
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
