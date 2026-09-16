using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Runtime.CompilerServices;

using SharpCoder;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for the lazy LLM client creation and fallible disposal in <see cref="SharpCoderRunner"/>.
/// <para>
/// Covers: <see cref="SharpCoderRunner.ConnectAsync"/> and
/// <see cref="SharpCoderRunner.ResetSessionAsync"/> create NO client;
/// <see cref="SharpCoderRunner.SendPromptAsync"/> creates it lazily on the first prompt;
/// <see cref="SharpCoderRunner.ResetSessionAsync"/> disposes + nulls the prior client
/// deterministically (null-then-dispose so a throwing dispose cannot leave a half-set field);
/// disposal is idempotent; a disposal exception propagates; a subsequent
/// <see cref="SharpCoderRunner.SendPromptAsync"/> re-creates the client cleanly.
/// </para>
/// </summary>
public sealed class SharpCoderRunnerLazyClientTests
{
    // ── Reflection helpers ─────────────────────────────────────────────────────

    private static readonly FieldInfo ChatClientField =
        typeof(SharpCoderRunner).GetField("_chatClient", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_chatClient field not found.");

    private static readonly FieldInfo PendingModelField =
        typeof(SharpCoderRunner).GetField("_pendingModel", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_pendingModel field not found.");

    /// <summary>
    /// The runner's tester-report field. It is observed DIRECTLY — not through a later
    /// <c>SetCustomAgent</c>-mediated tool render — because <c>SetCustomAgent</c> clears this SAME
    /// field, so any observation taken after one cannot attribute the clear to
    /// <see cref="SharpCoderRunner.ConnectAsync"/>.
    /// </summary>
    private static readonly FieldInfo TesterReportField =
        typeof(SharpCoderRunner).GetField("_testerReport", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_testerReport field not found.");

    private static IChatClient? GetChatClient(SharpCoderRunner runner) =>
        (IChatClient?)ChatClientField.GetValue(runner);

    private static string? GetPendingModel(SharpCoderRunner runner) =>
        (string?)PendingModelField.GetValue(runner);

    private static string? GetTesterReport(SharpCoderRunner runner) =>
        (string?)TesterReportField.GetValue(runner);

    // ── Stub chat client ───────────────────────────────────────────────────────

    /// <summary>
    /// A stub client that returns a single assistant message and tracks disposal.
    /// </summary>
    private sealed class StubClient : IChatClient
    {
        internal int DisposeCount;
        internal bool WasDisposed => DisposeCount > 0;

        public ChatClientMetadata Metadata => new("stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            };
            return Task.FromResult(resp);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => StreamAsync(ct);

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    /// <summary>A client whose Dispose throws — used to test null-then-dispose semantics.</summary>
    private sealed class ThrowingDisposeClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("throw", null, "throw-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            };
            return Task.FromResult(resp);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => StreamAsync(ct);

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => throw new InvalidOperationException("dispose boom");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string CreateTempWorkDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazy-client-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    // ===========================================================================
    // Lazy client creation: ConnectAsync and ResetSessionAsync create no client
    // ===========================================================================

    [Fact]
    public async Task ConnectAsync_CreatesNoClient()
    {
        // Use the parameterless constructor (production path) — no client injected
        var runner = new SharpCoderRunner("/config-repo");

        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Null(GetChatClient(runner));
    }

    [Fact]
    public async Task ResetSessionAsync_CreatesNoClient()
    {
        var runner = new SharpCoderRunner("/config-repo");

        await runner.ResetSessionAsync("test-model", null, TestContext.Current.CancellationToken);

        Assert.Null(GetChatClient(runner));
    }

    [Fact]
    public async Task ResetSessionAsync_RecordsPendingModel()
    {
        var runner = new SharpCoderRunner("/config-repo");

        await runner.ResetSessionAsync("my-model", null, TestContext.Current.CancellationToken);

        Assert.Equal("my-model", GetPendingModel(runner));
    }

    [Fact]
    public async Task ResetSessionAsync_NullModel_PendingModelIsNull()
    {
        var runner = new SharpCoderRunner("/config-repo");

        await runner.ResetSessionAsync(null, null, TestContext.Current.CancellationToken);

        Assert.Null(GetPendingModel(runner));
    }

    // ===========================================================================
    // SendPromptAsync creates the client lazily on the first prompt
    // ===========================================================================

    [Fact]
    public async Task SendPromptAsync_CreatesClientLazily_OnFirstPrompt()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var stub = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");
            runner.ClientCreationSeam = _ => stub;

            // Before SendPromptAsync, no client
            Assert.Null(GetChatClient(runner));

            await runner.SendPromptAsync("do something", workDir, TestContext.Current.CancellationToken);

            // After SendPromptAsync, the client was created
            Assert.NotNull(GetChatClient(runner));
            Assert.Same(stub, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPromptAsync_AfterReset_CreatesFreshClient()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var firstStub = new StubClient();
            var secondStub = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");

            // First prompt: creates first client
            runner.ClientCreationSeam = _ => firstStub;
            await runner.SendPromptAsync("first prompt", workDir, TestContext.Current.CancellationToken);
            Assert.Same(firstStub, GetChatClient(runner));

            // Reset: disposes + nulls the client
            await runner.ResetSessionAsync("new-model", null, TestContext.Current.CancellationToken);
            Assert.Null(GetChatClient(runner));
            Assert.True(firstStub.WasDisposed);

            // Second prompt: creates a fresh client
            runner.ClientCreationSeam = _ => secondStub;
            await runner.SendPromptAsync("second prompt", workDir, TestContext.Current.CancellationToken);
            Assert.Same(secondStub, GetChatClient(runner));
            Assert.NotSame(firstStub, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ===========================================================================
    // Fallible disposal: null-then-dispose, idempotent, throw propagation, clean re-creation
    // ===========================================================================

    [Fact]
    public async Task ResetSessionAsync_NullThenDispose_ThrowingDisposeLeavesFieldNull()
    {
        var throwingClient = new ThrowingDisposeClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => throwingClient;

        // Get a client into the field via SendPromptAsync
        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);
            Assert.NotNull(GetChatClient(runner));

            // Reset: null-then-dispose. The dispose throws, but the field must already be null.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.ResetSessionAsync("new-model", null, TestContext.Current.CancellationToken));

            // The field is null even though dispose threw — no half-set client
            Assert.Null(GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResetSessionAsync_DisposalIsIdempotent()
    {
        var stub = new StubClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => stub;

        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);
            Assert.Same(stub, GetChatClient(runner));

            // First reset: disposes + nulls
            await runner.ResetSessionAsync(null, null, TestContext.Current.CancellationToken);
            Assert.Null(GetChatClient(runner));
            Assert.Equal(1, stub.DisposeCount);

            // Second reset: nothing to dispose, no throw, no extra dispose call
            await runner.ResetSessionAsync(null, null, TestContext.Current.CancellationToken);
            Assert.Null(GetChatClient(runner));
            // Dispose was called only once (the first reset)
            Assert.Equal(1, stub.DisposeCount);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResetSessionAsync_DisposalException_Propagates()
    {
        var throwingClient = new ThrowingDisposeClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => throwingClient;

        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.ResetSessionAsync(null, null, TestContext.Current.CancellationToken));
            Assert.Contains("dispose boom", ex.Message);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPromptAsync_AfterDisposalThrow_RecreatesClientCleanly()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var throwingClient = new ThrowingDisposeClient();
            var cleanClient = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");
            runner.ClientCreationSeam = _ => throwingClient;

            // First prompt: creates the throwing client
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);
            Assert.Same(throwingClient, GetChatClient(runner));

            // Reset: dispose throws, field is nulled
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.ResetSessionAsync("new-model", null, TestContext.Current.CancellationToken));
            Assert.Null(GetChatClient(runner));

            // Switch the seam to a clean client and send again — must re-create cleanly
            runner.ClientCreationSeam = _ => cleanClient;
            await runner.SendPromptAsync("second prompt", workDir, TestContext.Current.CancellationToken);

            Assert.Same(cleanClient, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ===========================================================================
    // DisposeAsync: null-then-dispose + idempotent
    // ===========================================================================

    [Fact]
    public async Task DisposeAsync_NullThenDispose_ThrowingDisposeLeavesFieldNull()
    {
        var throwingClient = new ThrowingDisposeClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => throwingClient;

        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);
            Assert.NotNull(GetChatClient(runner));

            // DisposeAsync: null-then-dispose. The dispose throws.
            // Note: DisposeAsync returns ValueTask.CompletedTask AFTER calling Dispose,
            // so the exception propagates synchronously through the Dispose() call.
            Assert.Throws<InvalidOperationException>(() => runner.DisposeAsync().AsTask().GetAwaiter().GetResult());

            // The field is null even though dispose threw
            Assert.Null(GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var stub = new StubClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => stub;

        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);

            await runner.DisposeAsync();
            Assert.Equal(1, stub.DisposeCount);

            // Second dispose: nothing to dispose
            await runner.DisposeAsync();
            Assert.Equal(1, stub.DisposeCount);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenNoClient_NoThrow()
    {
        var runner = new SharpCoderRunner("/config-repo");

        // No client was ever created
        await runner.DisposeAsync();
        Assert.Null(GetChatClient(runner));
    }

    // ===========================================================================
    // Config provisioner integration: SendPromptAsync invokes provisioner before client creation
    // ===========================================================================

    [Fact]
    public async Task SendPromptAsync_InvokesProvisionerBeforeClientCreation()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var stub = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");

            var provisionerCalls = 0;
            string? capturedModel = null;
            runner.SetConfigProvisioner((model, ct) =>
            {
                Interlocked.Increment(ref provisionerCalls);
                capturedModel = model;
                return Task.CompletedTask;
            });
            runner.ClientCreationSeam = _ =>
            {
                // The provisioner must have run BEFORE the client is created
                Assert.True(Volatile.Read(ref provisionerCalls) > 0,
                    "Provisioner must run before client creation");
                return stub;
            };

            await runner.ResetSessionAsync("task-model-xyz", null, TestContext.Current.CancellationToken);
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);

            Assert.Equal(1, provisionerCalls);
            Assert.Equal("task-model-xyz", capturedModel);
            Assert.Same(stub, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPromptAsync_ProvisionerNull_CreatesClientDirectly()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var stub = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");
            // No provisioner set (null) — client created directly
            runner.ClientCreationSeam = _ => stub;

            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);

            Assert.Same(stub, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ===========================================================================
    // ConnectAsync: quiescent connection preparation
    //
    // Everything here is asserted through the REAL agent-options seam
    // (OnAgentOptionsCreated / the tools it exposes), so the contract is pinned on what the
    // production prompt turn actually receives — not on field reflection alone.
    // ===========================================================================

    /// <summary>
    /// CONNECTION PREPARATION IS A ROLE/PROMPT RESET, NOT A PER-ASSIGNMENT ONE.
    /// <para>
    /// The runner carries the PREVIOUS connection's <c>UpdateAgents</c> role and guidance
    /// (<c>"STALE-CONNECTION-GUIDANCE"</c>). A connection start with NO update must prepare from the
    /// constructor defaults: the ACTUAL <c>AgentOptions.SystemPrompt</c> handed to the production
    /// prompt turn is the default role's prompt and carries NONE of the stale guidance. A LATER
    /// <c>UpdateAgents</c> on the new connection IS honored — so the reset cannot be moved into the
    /// per-assignment path, which would erase it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConnectAsync_RestoresConnectionStartDefaults_AndALaterUpdateIsHonored()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner("/config-repo");
            runner.ClientCreationSeam = _ => new StubClient();

            const string StaleGuidance = "STALE-CONNECTION-GUIDANCE";
            const string FreshGuidance = "FRESH-CONNECTION-GUIDANCE";

            // The PREVIOUS connection's UpdateAgents state, still on the runner.
            runner.SetCustomAgent(WorkerRole.Coder, StaleGuidance);

            AgentOptions? firstOptions = null;
            runner.OnAgentOptionsCreated = options => firstOptions = options;

            // CONNECTION START with no update at all.
            await runner.ConnectAsync(TestContext.Current.CancellationToken);
            await runner.SendPromptAsync("first prompt", workDir, TestContext.Current.CancellationToken);

            var first = Assert.IsType<AgentOptions>(firstOptions);
            // The ACTUAL prompt the agent turn would use: the DEFAULT role's prompt, built with the
            // DEFAULT (null) guidance — the same thing a freshly constructed runner would produce.
            Assert.Equal(
                SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Unspecified, null),
                first.SystemPrompt);
            Assert.DoesNotContain(StaleGuidance, first.SystemPrompt, StringComparison.Ordinal);

            // A LATER UpdateAgents on the NEW connection is HONORED — the reset is a
            // connection-start preparation, never a per-assignment one.
            runner.SetCustomAgent(WorkerRole.Coder, FreshGuidance);
            AgentOptions? secondOptions = null;
            runner.OnAgentOptionsCreated = options => secondOptions = options;
            await runner.SendPromptAsync("second prompt", workDir, TestContext.Current.CancellationToken);

            var second = Assert.IsType<AgentOptions>(secondOptions);
            Assert.Equal(
                SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, FreshGuidance),
                second.SystemPrompt);
            Assert.Contains(FreshGuidance, second.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(StaleGuidance, second.SystemPrompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// CONNECTION PREPARATION CLEARS A STALE TESTER REPORT — attributed to
    /// <see cref="SharpCoderRunner.ConnectAsync"/> ITSELF.
    /// <para>
    /// THE ATTRIBUTION PROBLEM THIS SOLVES. <c>SetCustomAgent</c> clears the SAME field, so an
    /// observation taken only after a post-<c>ConnectAsync</c> <c>SetCustomAgent</c> stays green even
    /// with the clear removed from <c>ConnectAsync</c>. The decisive observation is therefore taken
    /// IMMEDIATELY after <c>ConnectAsync</c> and BEFORE anything else touches the runner: removing
    /// <c>_testerReport = null</c> from <c>ConnectAsync</c> fails THIS assertion by name.
    /// </para>
    /// <para>
    /// The BEFORE-CONTROL is retained and strengthened: the report is first observed present BOTH on
    /// the field AND through the <c>get_test_report</c> tool the production reviewer turn actually
    /// receives (non-vacuity — the recorded report really is live), and the end-to-end render after
    /// preparation is still asserted, so the behavior remains pinned through the production tool too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ClearsStaleTesterReport_SurfacedThroughTheProductionTool()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            const string StaleReport = "STALE-TESTER-REPORT";
            var runner = new SharpCoderRunner("/config-repo");
            runner.ClientCreationSeam = _ => new StubClient();

            // The previous connection's reviewer state: role + a recorded tester report.
            runner.SetCustomAgent(WorkerRole.Reviewer, "reviewer guidance");
            runner.SetTesterReport(StaleReport);

            // BEFORE-CONTROL (non-vacuity): the report is live on the field AND really is exposed by
            // the production tool, so the post-preparation observations below are not vacuous.
            Assert.Equal(StaleReport, GetTesterReport(runner));

            IList<AITool>? toolsBefore = null;
            runner.OnAgentOptionsCreated = options => toolsBefore = options.CustomTools;
            await runner.SendPromptAsync("review", workDir, TestContext.Current.CancellationToken);

            Assert.Contains(
                StaleReport,
                await InvokeGetTestReportAsync(toolsBefore!),
                StringComparison.Ordinal);
            Assert.Equal(StaleReport, GetTesterReport(runner));

            // ── THE DECISIVE, ISOLATED OBSERVATION ────────────────────────────
            // CONNECTION START, and NOTHING else: no SetCustomAgent, no prompt, no reset. The field is
            // read immediately afterwards, so the clear can only be attributed to ConnectAsync.
            await runner.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.Null(GetTesterReport(runner));

            // ...and the END-TO-END render still matches: the connection's own UpdateAgents sets the
            // role again, so the tool is available but no longer carries the stale report.
            runner.SetCustomAgent(WorkerRole.Reviewer, "reviewer guidance");

            IList<AITool>? toolsAfter = null;
            runner.OnAgentOptionsCreated = options => toolsAfter = options.CustomTools;
            await runner.SendPromptAsync("review again", workDir, TestContext.Current.CancellationToken);

            var report = await InvokeGetTestReportAsync(toolsAfter!);
            Assert.DoesNotContain(StaleReport, report, StringComparison.Ordinal);
            Assert.Contains("No test report available", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// CONNECTION PREPARATION CREATES NO LLM CLIENT and never invokes the client-creation seam: the
    /// existing lazy creation contract is unchanged, so preparation can never start provisioning
    /// transport on its own.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_DoesNotInvokeTheClientCreationSeam()
    {
        var runner = new SharpCoderRunner("/config-repo");
        var seamCalls = 0;
        runner.ClientCreationSeam = _ =>
        {
            Interlocked.Increment(ref seamCalls);
            return new StubClient();
        };

        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref seamCalls));
        Assert.Null(GetChatClient(runner));
    }

    /// <summary>
    /// CONNECTION PREPARATION NEVER RESURRECTS A DISPOSED RUNNER: it neither re-creates the client
    /// lifecycle gate nor re-enables prompting. After final disposal a connection start returns
    /// normally, yet the disposed runner still behaves as disposed — a prompt turn still fails with
    /// the same .NET disposal category, and no client is created.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_OnDisposedRunner_DoesNotResurrectIt()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner("/config-repo");
            runner.ClientCreationSeam = _ => new StubClient();
            await runner.ConnectAsync(TestContext.Current.CancellationToken);

            await runner.DisposeAsync();

            // Preparation itself does not throw and does not create anything...
            await runner.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.Null(GetChatClient(runner));

            // ...and the runner is STILL disposed: the next prompt turn fails with the existing
            // disposal category instead of silently re-creating the gate.
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken));
            Assert.Null(GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>Invokes the production <c>get_test_report</c> tool from a captured tool set.</summary>
    private static async Task<string> InvokeGetTestReportAsync(IList<AITool> tools)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(
            tools.Single(t => t is AIFunction f && f.Name == "get_test_report"));
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(), TestContext.Current.CancellationToken);
        return result?.ToString() ?? string.Empty;
    }
}