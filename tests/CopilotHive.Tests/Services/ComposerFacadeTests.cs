using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="ComposerFacade"/> — the facade the Composer endpoints and the
/// <c>ComposerChat</c> component use instead of touching <see cref="Composer"/> (or an
/// <c>HttpClient</c>) directly.
/// </summary>
/// <remarks>
/// The load-bearing contracts pinned down here:
/// <list type="bullet">
///   <item>With a NULL Composer every method returns the SAME explicit failure result —
///   never a throw and never a fabricated payload.</item>
///   <item>An unconfigured/disconnected Composer reports its current model as a SUCCESSFUL
///   result carrying <c>null</c> — a normal state, not an error.</item>
///   <item>Exception semantics mirror the pre-facade handlers: the switch maps only
///   <see cref="ArgumentException"/>; the compactions have a catch-all; the two reads catch
///   nothing.</item>
/// </list>
/// </remarks>
public sealed class ComposerFacadeTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly List<string> _tempDirs = [];
    private readonly List<CopilotHiveDbContext> _extraContexts = [];

    public ComposerFacadeTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    /// <summary>Creates a real (never connected) Composer over an isolated state directory.</summary>
    /// <param name="availableModels">The catalog the Composer exposes.</param>
    private Composer CreateComposer(string[]? availableModels = null)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-facade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        return new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: availableModels ?? ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    private static ComposerFacade CreateFacade(Composer? composer)
        => new(composer, NullLogger<ComposerFacade>.Instance);

    /// <summary>Returns the Composer's <see cref="SharpCoder.AgentSession"/> via reflection.</summary>
    private static SharpCoder.AgentSession GetSessionOf(Composer composer)
    {
        var agentService = (ComposerAgentService)typeof(Composer)
            .GetField("_agentService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(composer)!;
        return (SharpCoder.AgentSession)agentService.GetType()
            .GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(agentService)!;
    }

    /// <summary>
    /// Creates a Composer, connects it, and (optionally) populates enough history for a real
    /// compaction. The chat client returns a summary response so the compactor succeeds.
    /// </summary>
    private async Task<Composer> CreateComposerWithConnectedAgent(bool populateMessages = true)
    {
        var dbContext2 = CopilotHiveDbContext.CreateInMemory();
        _extraContexts.Add(dbContext2);
        var store2 = new GoalStore(dbContext2, NullLogger<GoalStore>.Instance);
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-facade-{Guid.NewGuid():N}");
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
            store2,
            stateDir: stateDir,
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => mockClient.Object);

        await composer.ConnectAsync(TestContext.Current.CancellationToken);

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
    /// Creates a Composer whose chat-client factory throws a NON-<see cref="ArgumentException"/>
    /// for a model other than the startup default — a real creation failure at the switch's
    /// commit boundary.
    /// </summary>
    private Composer CreateComposerWithThrowingClientFactory()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-facade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        return new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: modelId => modelId == "claude-opus"
                ? throw new InvalidOperationException("factory exploded for claude-opus")
                : new Mock<IChatClient>().Object);
    }

    // ── Null-Composer contract: EVERY method reports NotConfigured ──────────

    [Fact]
    public async Task NullComposer_GetCurrentModel_ReportsNotConfigured()
    {
        var result = await CreateFacade(null).GetCurrentModelAsync();

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal("Composer is not available.", result.Error);
        Assert.Equal(FacadeErrorKind.NotConfigured, result.Kind);
    }

    [Fact]
    public void NullComposer_GetModels_ReportsNotConfigured()
    {
        var result = CreateFacade(null).GetModels();

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal("Composer is not available.", result.Error);
        Assert.Equal(FacadeErrorKind.NotConfigured, result.Kind);
    }

    /// <summary>
    /// The null-Composer check runs BEFORE argument validation: even a request whose arguments
    /// would be rejected as a <see cref="FacadeErrorKind.BadRequest"/> against a real Composer
    /// (blank model / blank reasoning) must still report the null-Composer NotConfigured result
    /// with the exact message. A validating-first implementation — which would answer BadRequest
    /// for these inputs — fails this test.
    /// </summary>
    [Theory]
    [InlineData(null, "medium")]
    [InlineData("", "medium")]
    [InlineData("   ", "medium")]
    [InlineData("claude-opus", null)]
    [InlineData("claude-opus", "")]
    [InlineData("claude-opus", "   ")]
    [InlineData(null, null)]
    public async Task NullComposer_SwitchModel_ChecksComposerBeforeValidatingArguments(string? model, string? reasoning)
    {
        var result = await CreateFacade(null).SwitchModelAsync(model, reasoning);

        Assert.False(result.Success);
        Assert.Equal("Composer is not available.", result.Error);
        Assert.Equal(FacadeErrorKind.NotConfigured, result.Kind);
        Assert.DoesNotContain("required", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullComposer_Compact_ReportsNotConfigured()
    {
        var result = await CreateFacade(null).CompactAsync();

        Assert.False(result.Success);
        Assert.Equal("Composer is not available.", result.Error);
        Assert.Equal(FacadeErrorKind.NotConfigured, result.Kind);
    }

    [Fact]
    public async Task NullComposer_CompactPartial_ReportsNotConfigured()
    {
        var result = await CreateFacade(null).CompactPartialAsync(50);

        Assert.False(result.Success);
        Assert.Equal("Composer is not available.", result.Error);
        Assert.Equal(FacadeErrorKind.NotConfigured, result.Kind);
    }

    // ── Current model: a null model is a SUCCESSFUL result ──────────────────

    /// <summary>
    /// A Composer that was never connected has no stats, so the current model is <c>null</c> —
    /// and that is reported as a SUCCESS, never as a failure and never as a fabricated catalog
    /// entry. This is the contract the ComposerChat page distinguishes from a real failure.
    /// </summary>
    [Fact]
    public async Task GetCurrentModel_NotConnected_SucceedsWithNullModel()
    {
        var facade = CreateFacade(CreateComposer());

        var result = await facade.GetCurrentModelAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Null(result.Value!.Model);
        Assert.Null(result.Error);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
    }

    /// <summary>
    /// A CONNECTED Composer reports its actual active model on a successful result — the
    /// value comes from <see cref="Composer.GetStats"/>, never from the catalog.
    /// </summary>
    [Fact]
    public async Task GetCurrentModel_Configured_ReturnsTheActiveModel()
    {
        var composer = CreateComposer();
        await using (composer)
        {
            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            var result = await CreateFacade(composer).GetCurrentModelAsync();

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.Equal("claude-sonnet-4", result.Value!.Model);
            Assert.Null(result.Error);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
        }
    }

    // ── Models: the Composer catalog is the sole authority ──────────────────

    [Fact]
    public void GetModels_ReturnsComposerCatalogVerbatim()
    {
        var composer = CreateComposer(["claude-sonnet-4", "claude-opus"]);
        var facade = CreateFacade(composer);

        var result = facade.GetModels();

        Assert.True(result.Success);
        Assert.Equal(composer.AvailableModels, result.Value!.Models);
        Assert.Equal(composer.ReasoningEffort, result.Value.ReasoningEffort);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
    }

    // ── Switch: argument validation → BadRequest ────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SwitchModel_MissingModel_IsBadRequest(string? model)
    {
        var facade = CreateFacade(CreateComposer());

        var result = await facade.SwitchModelAsync(model, "medium");

        Assert.False(result.Success);
        Assert.Equal("The 'model' query parameter is required.", result.Error);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SwitchModel_MissingReasoning_IsBadRequest(string? reasoning)
    {
        var facade = CreateFacade(CreateComposer());

        var result = await facade.SwitchModelAsync("claude-opus", reasoning);

        Assert.False(result.Success);
        Assert.Equal("The 'reasoning' query parameter is required.", result.Error);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
    }

    [Fact]
    public async Task SwitchModel_UnparsableReasoning_IsBadRequest()
    {
        var facade = CreateFacade(CreateComposer());

        var result = await facade.SwitchModelAsync("claude-opus", "turbo");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// Validation is against the Composer's normalised catalog — the same list
    /// <see cref="IComposerFacade.GetModels"/> reports — and the rejection message names the
    /// available models.
    /// </summary>
    [Fact]
    public async Task SwitchModel_ModelNotInCatalog_IsBadRequestNamingTheCatalog()
    {
        var facade = CreateFacade(CreateComposer(["claude-sonnet-4", "claude-opus"]));

        var result = await facade.SwitchModelAsync("gpt-5", "medium");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Contains("gpt-5", result.Error!, StringComparison.Ordinal);
        Assert.Contains("claude-opus", result.Error!, StringComparison.Ordinal);
    }

    // ── Compaction: the handlers' catch-all maps to BadRequest ──────────────

    /// <summary>
    /// A never-connected Composer throws out of <c>CompactSessionAsync</c>; the facade's
    /// catch-all turns that into a BadRequest result carrying the exception message (exactly
    /// what the pre-facade endpoint returned as HTTP 400).
    /// </summary>
    [Fact]
    public async Task Compact_NotConnected_IsBadRequestCarryingTheExceptionMessage()
    {
        var facade = CreateFacade(CreateComposer());

        var result = await facade.CompactAsync();

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Contains("not connected", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompactPartial_NotConnected_IsBadRequestCarryingTheExceptionMessage()
    {
        var facade = CreateFacade(CreateComposer());

        var result = await facade.CompactPartialAsync(50);

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Contains("not connected", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Switch: success (the model actually applied) ─────────────────────────

    /// <summary>
    /// A successful switch reports the model and reasoning effort the Composer ACTUALLY
    /// applied — read back from the running Composer, not echoed from the request.
    /// </summary>
    [Fact]
    public async Task SwitchModel_ToCatalogModel_SucceedsWithAppliedValues()
    {
        var composer = CreateComposer();
        await using (composer)
        {
            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            var result = await CreateFacade(composer).SwitchModelAsync("claude-opus", "medium");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.Equal("claude-opus", result.Value!.Model);
            Assert.Equal(ReasoningEffort.Medium, result.Value.ReasoningEffort);
            Assert.Null(result.Error);
            Assert.Equal(FacadeErrorKind.None, result.Kind);

            // The Composer itself really switched — the facade result is not fabricated.
            Assert.Equal("claude-opus", composer.GetStats()?.Model);
        }
    }

    /// <summary>
    /// Model matching against the catalog is CASE-INSENSITIVE: a differently-cased spelling
    /// of a listed model is accepted (a case-sensitive Contains would reject it with a
    /// BadRequest naming the catalog). The applied model is reported verbatim.
    /// </summary>
    [Fact]
    public async Task SwitchModel_CaseInsensitiveModel_MatchesTheCatalog()
    {
        var composer = CreateComposer();
        await using (composer)
        {
            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            var result = await CreateFacade(composer).SwitchModelAsync("CLAUDE-OPUS", "high");

            Assert.True(result.Success, result.Error ?? "switch failed");
            Assert.NotNull(result.Value);
            Assert.Equal("CLAUDE-OPUS", result.Value!.Model);
            Assert.Equal(ReasoningEffort.High, result.Value.ReasoningEffort);
            Assert.Equal(FacadeErrorKind.None, result.Kind);

            // The Composer really applied the switch (matched case-insensitively).
            Assert.Equal("CLAUDE-OPUS", composer.GetStats()?.Model);
        }
    }

    // ── Compaction: success results carry the applied values ────────────────

    /// <summary>
    /// With a connected agent and enough history, <see cref="IComposerFacade.CompactAsync"/>
    /// returns a SUCCESS carrying <c>Compacted=true</c> and the post-compaction message count.
    /// </summary>
    [Fact]
    public async Task Compact_ConnectedWithEnoughMessages_SucceedsAndReportsTheCount()
    {
        var composer = await CreateComposerWithConnectedAgent();
        await using (composer)
        {
            var result = await CreateFacade(composer).CompactAsync();

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.True(result.Value!.Compacted);
            Assert.True(result.Value.MessageCount > 0, "The post-compaction message count must be reported");
            Assert.Null(result.Error);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
        }
    }

    /// <summary>
    /// With a connected agent but too few messages, compaction runs and reports
    /// <c>Compacted=false</c> — still a SUCCESSFUL facade result (nothing failed; there was
    /// simply nothing to compact).
    /// </summary>
    [Fact]
    public async Task CompactPartial_ConnectedWithTooFewMessages_SucceedsWithCompactedFalse()
    {
        var composer = await CreateComposerWithConnectedAgent(populateMessages: false);
        await using (composer)
        {
            var result = await CreateFacade(composer).CompactPartialAsync(50);

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.False(result.Value!.Compacted);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
        }
    }

    // ── Rethrow semantics: only the handlers' catches map; everything else throws ──

    /// <summary>
    /// The switch maps ONLY <see cref="ArgumentException"/>. A real client-factory failure at
    /// the commit boundary propagates as an EXCEPTION — it is never mapped to a result.
    /// </summary>
    [Fact]
    public async Task SwitchModel_ClientFactoryFailure_PropagatesAsException_NotAMappedResult()
    {
        var composer = CreateComposerWithThrowingClientFactory();
        await using (composer)
        {
            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            // The factory throws for the NEW model — a real creation failure at the switch's
            // commit boundary, not an ArgumentException.
            var facade = CreateFacade(composer);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => facade.SwitchModelAsync("claude-opus", "medium"));

            Assert.Contains("factory exploded", exception.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The current-model read catches NOTHING (matching the pre-facade handler): a
    /// <see cref="Composer.GetStats"/> failure surfaces as an exception, never as a mapped
    /// <c>FacadeResult</c>. The throw is induced by breaching the connected-without-model
    /// invariant via reflection — exactly the state <see cref="Composer.GetStats"/> guards.
    /// </summary>
    [Fact]
    public async Task GetCurrentModel_GetStatsThrows_SurfacesAsException_NotAMappedResult()
    {
        var composer = CreateComposer();
        await using (composer)
        {
            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            // Breach the invariant GetStats guards: agent present, model null.
            var agentService = (ComposerAgentService)typeof(Composer)
                .GetField("_agentService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(composer)!;
            agentService.GetType()
                .GetField("_model", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(agentService, null);

            var facade = CreateFacade(composer);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => facade.GetCurrentModelAsync());

            Assert.Contains("without a model", exception.Message, StringComparison.Ordinal);
        }
    }

    // ── No CancellationToken anywhere on the facade surface ─────────────────

    /// <summary>
    /// None of the pre-facade endpoints accepted a cancellation token, so no facade method may
    /// declare one — the adapters pass <see cref="CancellationToken.None"/> internally.
    /// </summary>
    [Fact]
    public void FacadeSurface_DeclaresNoCancellationTokenParameters()
    {
        foreach (var method in typeof(IComposerFacade).GetMethods())
        {
            Assert.DoesNotContain(
                method.GetParameters(),
                p => p.ParameterType == typeof(CancellationToken));
        }
    }

    /// <summary>
    /// Composer CONFIGURATION lives on <see cref="IConfigFacade"/> and must not be aliased onto
    /// this facade: the runtime facade exposes EXACTLY the five runtime operations.
    /// </summary>
    [Fact]
    public void FacadeSurface_ExposesExactlyTheFiveRuntimeOperations()
    {
        var names = typeof(IComposerFacade).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray();

        Assert.Equal(
            ["CompactAsync", "CompactPartialAsync", "GetCurrentModelAsync", "GetModels", "SwitchModelAsync"],
            names);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        foreach (var extra in _extraContexts)
            extra.Dispose();
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
