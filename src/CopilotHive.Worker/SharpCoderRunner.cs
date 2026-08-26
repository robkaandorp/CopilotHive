#pragma warning disable CS1591
#pragma warning disable OPENAI001 // ResponsesClient.AsIChatClient is experimental
using CopilotHive.Services;
using CopilotHive.Shared;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;
using SharpCoder.Providers;
using SharpCoder.SubAgents;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CopilotHive.Worker;

public sealed class SharpCoderRunner : IAgentRunner
{
    private readonly WorkerLogger _log = new("SharpCoder");
    private readonly bool _verboseLogging = Environment.GetEnvironmentVariable("VERBOSE_LOGGING") == "true";
    private readonly Func<string?, IChatClient>? _clientFactory;
    private IChatClient? _chatClient;
    private string _currentModel = "(default)";
    private ReasoningEffort? _currentReasoning;

    /// <summary>
    /// The model the next lazily-created client must use, recorded by <see cref="ResetSessionAsync"/>.
    /// </summary>
    private string? _pendingModel;

    /// <summary>
    /// Runs orchestrator provisioning before a client is created. Invoked UNCONDITIONALLY before
    /// every first client creation. <c>null</c> disables provisioning (unit tests, and any
    /// deployment where the worker is configured entirely by the operator).
    /// </summary>
    private Func<string?, CancellationToken, Task>? _configProvisioner;

    /// <summary>
    /// Serializes the ENTIRE LLM client lifecycle — lazy creation, reset/dispose and final
    /// disposal — so two overlapping task assignments can never race it.
    /// <para>
    /// The race this closes: <c>WorkerService</c> sends <c>WorkerReady</c> from a completing
    /// task's <c>finally</c> and the cancel handler sends a second one, so the orchestrator can
    /// deliver a new assignment while the previous task is still unwinding. Without this gate,
    /// two tasks could both observe a null <c>_chatClient</c> and each construct one (leaking a
    /// client), or one could dispose the client while the other was still using it.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _clientLifecycleGate = new(1, 1);

    /// <summary>Set to 1 by the first <see cref="DisposeAsync"/>, making repeat calls no-ops.</summary>
    private int _disposed;

    private readonly string _configRepoDir;

    /// <summary>
    /// Initializes a new <see cref="SharpCoderRunner"/>. Call <see cref="ConnectAsync"/>
    /// before invoking <see cref="SendPromptAsync"/> to create the chat client.
    /// </summary>
    /// <param name="configRepoDir">Directory containing the agent instruction repository. Defaults to <c>/config-repo</c>.</param>
    public SharpCoderRunner(string configRepoDir = "/config-repo")
    {
        _configRepoDir = configRepoDir;
    }

    /// <summary>
    /// Internal constructor for unit testing: injects a pre-created <see cref="IChatClient"/>
    /// and model name, bypassing <see cref="SharpCoder.Providers.ChatClientFactory"/> so that tests
    /// can run without real LLM credentials.
    /// </summary>
    /// <param name="chatClient">The chat client to use for agent execution.</param>
    /// <param name="model">The model identifier to record in log output.</param>
    /// <param name="configRepoDir">Directory containing the agent instruction repository. Defaults to <c>/config-repo</c>.</param>
    internal SharpCoderRunner(IChatClient chatClient, string model, string configRepoDir = "/config-repo")
    {
        _configRepoDir = configRepoDir;
        _chatClient = chatClient;
        _currentModel = model;
        _clientFactory = _ => chatClient;
    }

    private IToolCallBridge? _toolBridge;
    private string? _currentTaskId;
    private string? _currentGoalId;
    private WorkerRole _currentRole;
    private string? _customAgentSystemPrompt;
    private int _maxContextTokens = 150_000;
    private string? _compactionModel;
    private int? _compactionMaxTokens;
    private IReadOnlyList<SubAgentModelDto> _subAgentModels = [];

    // Test seams — nullable, set by tests only
    internal Func<string?, IChatClient>? ClientCreationSeam;
    internal Action<AgentOptions>? OnAgentOptionsCreated;
    internal Action<CodingAgent>? OnAgentCreated;

    /// <summary>Current agent session; set via <see cref="SetSession"/> before <see cref="SendPromptAsync"/>.</summary>
    private AgentSession? _session;

    private TestResultReport? _lastTestReport;
    private WorkerReport? _lastWorkerReport;
    private string? _testerReport;

    public TestResultReport? LastTestReport => _lastTestReport;
    public WorkerReport? LastWorkerReport => _lastWorkerReport;

    public void ClearTestReport() => _lastTestReport = null;
    public void ClearWorkerReport() => _lastWorkerReport = null;
    public void SetTesterReport(string? report) => _testerReport = report;

    public void SetToolBridge(IToolCallBridge? bridge) => _toolBridge = bridge;
    public void SetCurrentTaskId(string? taskId) => _currentTaskId = taskId;
    public void SetCurrentGoalId(string? goalId) => _currentGoalId = goalId;

    public void SetCustomAgent(WorkerRole role, string agentsMdContent)
    {
        _currentRole = role;
        _customAgentSystemPrompt = agentsMdContent;
        _testerReport = null;
    }

    /// <inheritdoc/>
    public void SetMaxContextTokens(int maxTokens) =>
        _maxContextTokens = maxTokens > 0 ? maxTokens : 150_000;

    /// <inheritdoc/>
    public void SetCompactionModel(string? model) => _compactionModel = model;

    /// <inheritdoc/>
    public void SetCompactionMaxTokens(int? maxTokens) => _compactionMaxTokens = maxTokens;

    /// <inheritdoc/>
    public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) => _subAgentModels = models ?? [];

    /// <inheritdoc/>
    public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) =>
        _configProvisioner = provisioner;

    /// <summary>
    /// Builds the <see cref="SubAgentOptions"/> for the configured model catalog, or
    /// <c>null</c> when the catalog is empty (sub-agents disabled).
    /// </summary>
    internal SubAgentOptions? BuildSubAgentOptions()
    {
        if (_subAgentModels.Count == 0)
            return null;

        var subOpts = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 2,
            DefaultTimeout = TimeSpan.FromMinutes(5),
            MaxTimeout = TimeSpan.FromMinutes(15),
            MaxSummaryChars = 8_000,
        };

        foreach (var m in _subAgentModels)
        {
            if (!string.IsNullOrWhiteSpace(m.Id))
                subOpts.AvailableModels.Add(new SubAgentModelInfo(m.Id, m.Description, m.ContextWindow, supportsVision: m.SupportsVision));
        }

        // ClientFactory delegates to the injectable seam, falling back to CreateChatClient.
        // Factory-created clients are owned and disposed by SubAgentManager — do NOT track them here.
        subOpts.ClientFactory = modelId =>
            _clientFactory?.Invoke(modelId)
            ?? ClientCreationSeam?.Invoke(modelId)
            ?? CreateChatClient(modelId);

        return subOpts;
    }

    /// <summary>
    /// Builds the full system prompt for the given <paramref name="role"/> by combining the
    /// hardcoded role prompt with any learned heuristics from <paramref name="agentsMdContent"/>.
    /// </summary>
    /// <param name="role">The worker role whose hardcoded prompt to use.</param>
    /// <param name="agentsMdContent">Optional AGENTS.md content to append as learned heuristics.</param>
    /// <returns>
    /// The combined system prompt string. If <paramref name="agentsMdContent"/> is non-empty,
    /// it is appended after a <c>\n\n# Learned Heuristics\n\n</c> separator.
    /// </returns>
    internal static string BuildRoleSystemPrompt(WorkerRole role, string? agentsMdContent)
    {
        const string SharedPreamble = """
            INFRASTRUCTURE RULES (these are enforced by the system and cannot be overridden):
            - NEVER run `git push` — the infrastructure handles pushing automatically.
            - NEVER run `git checkout`, `git branch`, or `git switch` — the infrastructure handles branching.
            - When the goal description is ambiguous, files-to-change seem incomplete, or acceptance criteria conflict, call `request_clarification` instead of guessing.
            - Call `report_progress` at each meaningful step (e.g. "Reading files", "Building", "Tests passing", "Committing") so the user can follow your progress in real time.
            - Call `report_narrative` at the end of your work, before calling your report tool (report_code_changes, report_test_results, report_review_verdict, report_doc_changes). Write 2-5 sentences about what you tried, what worked, what you struggled with, and why. This helps the system learn and improve.
            - Call `raise_issue` when you notice code quality problems, bugs, suggestions, concerns, or workflow issues that are out of scope for the current goal. Do not fix them yourself unless they directly block the goal.
            """;

        var roleSpecific = role switch
        {
            WorkerRole.Coder => $"""
                {SharedPreamble}

                # Coder

                You are a software developer. **Implement changes by editing files** — not describing them.
                Every task requires you to edit files, build, test, and commit.

                A text-only response without file edits is a **failure**.

                ## Reporting Your Changes (MANDATORY)

                After edits, builds, tests, and commits, you MUST call the `report_code_changes` tool with:
                - `verdict`: "PASS" if you successfully implemented and committed, "FAIL" if you could not
                - `filesModified`: array of files you changed (e.g. ["src/module.ext", "tests/moduleTests.ext"])
                - `summary`: put EVERYTHING relevant here — what you implemented, files changed and why,
                  decisions made, build/test status, any issues encountered. This is the sole output the
                  system reads; your text response is ignored.

                After calling the tool, respond with a single word only: `done` (or `fail` if verdict is FAIL).
                """,

            WorkerRole.Tester => $"""
                {SharedPreamble}

                # Tester

                You are a QA engineer responsible for comprehensive testing of the codebase. You go
                beyond unit tests — you verify that the system actually works as a whole.

                ## Acceptance Criteria Verification

                Beyond running build and tests, verify that the code changes actually address the goal's
                requirements. If the goal specifies structural changes (e.g. new UI layout, new API endpoints,
                new files) and those changes are absent or incomplete, report this in your summary and set
                verdict to FAIL. Passing existing tests is necessary but not sufficient — the goal's
                acceptance criteria must be met.

                Use `get_goal (no argument)` to fetch the full description if needed.

                ## Reporting Your Results (MANDATORY)

                After all testing, you MUST call the `report_test_results` tool with:
                - `verdict`: "PASS" or "FAIL"
                - `totalTests`: total number of tests run
                - `passedTests`: number that passed
                - `failedTests`: number that failed
                - `coveragePercent`: coverage percentage, or -1 if not measured
                - `buildSuccess`: true if the build succeeded
                - `issues`: array of issue descriptions (empty if none)
                - `summary`: put EVERYTHING relevant here — test counts, any failures with names and error
                  messages, build status, coverage, observations. This is the sole output the system reads;
                  your text response is ignored.

                NEVER report PASS if any test is failing.
                After calling the tool, respond with a single word only: `pass` or `fail`.
                """,

            WorkerRole.Reviewer => $"""
                {SharedPreamble}

                # Reviewer

                You are a senior code reviewer. Review diffs for correctness, quality, and convention
                adherence. Focus on bugs, security, logic errors, and maintainability — not style.

                Do NOT modify code or run `git push`.

                ## Acceptance Criteria Verification (MANDATORY)

                You MUST read the full goal description (use `get_goal (no argument)` to fetch it if needed).
                and verify that EVERY acceptance criterion is satisfied by the changes in the diff. If the diff
                is technically correct but only implements a fraction of the goal's requirements, that is a
                **[CRITICAL]** issue — you MUST REQUEST_CHANGES. Do not accept the brain's or coder's framing
                of "iteration scope" or "focused change" as a reason to skip acceptance criteria. The goal
                description is the sole source of truth for what must be delivered.

                ## Reporting Your Verdict (MANDATORY)

                After reviewing, you MUST call the `report_review_verdict` tool with:
                - `verdict`: "APPROVE" or "REQUEST_CHANGES"
                - `issues`: array of issue descriptions (prefix each with [CRITICAL], [MAJOR], or [MINOR])
                - `summary`: put EVERYTHING relevant here — your overall verdict with reasoning, each issue
                  with severity/location/description, and what was done well. This is the sole output the
                  system reads; your text response is ignored.

                - **APPROVE**: Code correct, ready for testing. Zero critical issues.
                - **REQUEST_CHANGES**: Critical or major issues must be fixed first.
                - **CRITICAL**: Bugs, security, data loss, missing files. Must fix.
                - **MAJOR**: Missing error handling, missing tests, API violations. Should fix.
                - **MINOR**: Naming, refactoring suggestions, doc gaps. Nice-to-have.
                After calling the tool, respond with a single word only: `approved` or `changes`.
                """,

            WorkerRole.DocWriter => $"""
                {SharedPreamble}

                # Doc Writer

                You are a technical documentation specialist. Your job is to update project documentation
                to reflect code changes made on the current feature branch.

                Do NOT edit source code files. Do NOT write or modify test code. Do NOT run tests or build.

                ## Reporting Your Changes (MANDATORY)

                After your work, you MUST call the `report_doc_changes` tool with:
                - `verdict`: "PASS" if you successfully updated documentation, "FAIL" if you could not
                - `filesUpdated`: array of files you changed (e.g. ["CHANGELOG.md", "README.md"])
                - `summary`: put EVERYTHING relevant here — which files were updated and what changed in
                  each, changelog entries added, decisions about scope. This is the sole output the system
                  reads; your text response is ignored.

                After calling the tool, respond with a single word only: `done` (or `fail` if verdict is FAIL).
                """,

            WorkerRole.Improver => $"""
                {SharedPreamble}

                # Improver

                You are an expert at analysing software development iteration outcomes and improving
                agent instructions to produce better results in the next iteration.

                You have direct access to the `agents/` folder containing `*.agents.md` files.
                Use the file tools (view, edit) to read and modify these files directly.
                You **cannot** run shell commands — file reading and editing only.
                If you delegate work to sub-agents, they can request file-write access by passing
                `enable_file_writes=true` in the `start_sub_agent` call. Your file-write capability
                is enabled, so sub-agents that request it will be granted write access. Only bash
                is disabled for you and your sub-agents. Do not ask the orchestrator to apply file
                edits on your behalf — use your own file tools or delegate to a sub-agent with
                `enable_file_writes=true`.

                The updated agents.md file MUST NOT exceed 4000 characters. Count characters before finalising.

                Do NOT add "Iteration History" or changelog-style entries to agents.md files. agents.md files contain
                guidance rules and quality standards, not logs of past iterations. Extract actionable lessons from the
                iteration and add them as guidance rules (e.g., "Always check X before Y", "When doing Z, prefer approach
                A over B"). If a lesson was already captured in a previous iteration, do not duplicate it.

                Guidance rules added to agents.md files must be GENERAL, broadly-applicable software-development patterns
                and coding-style conventions that apply across the codebase, across goals, and across different files and
                contexts. Positive examples include "Always verify a resource is released in every code path",
                "Prefer try/finally over manual cleanup", and "Use a shared helper instead of duplicating logic". Negative
                examples that must NOT be added include "never do X in ServiceY.cs", "when handling paste state for Z", or
                "the TCS-block in ClassB". Those are too narrow: they are tied to a specific file, class, method, past
                iteration, or incident and do not generalize. Existing general rules are fine; do not rewrite them into one-off
                incident advice.

                **Never remove or weaken safety constraints** — do not remove instructions about git workflow,
                test requirements or output format compliance.

                Only edit `*.agents.md` files — do not create new files, rename files, or touch anything
                outside the agents/ folder.
                """,

            WorkerRole.Unspecified => SharedPreamble,

            _ => throw new InvalidOperationException($"No hardcoded system prompt defined for WorkerRole '{role}'."),
        };

        if (string.IsNullOrWhiteSpace(agentsMdContent))
            return roleSpecific;

        return roleSpecific + "\n\n# Learned Heuristics\n\n" + agentsMdContent;
    }

    /// <inheritdoc/>
    public void SetSession(object? session) => _session = session as AgentSession;

    /// <inheritdoc/>
    public object? GetSession() => _session;

    /// <inheritdoc/>
    public int GetContextUsagePercent()
    {
        if (_session == null) return 0;

        var contextDenominator = (double)_maxContextTokens;
        var tokens = _session.LastKnownContextTokens > 0
            ? _session.LastKnownContextTokens
            : _session.EstimatedContextTokens;
        return (int)Math.Min(100, (tokens * 100.0) / contextDenominator);
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        // Deliberately creates NO client. Worker containers hold no LLM credentials of their
        // own: the credentials are provisioned by the orchestrator, and that provisioning
        // fetch runs immediately before the FIRST client creation, which happens lazily in
        // SendPromptAsync. Creating a client here would run before provisioning and before the
        // operator has necessarily signed in.
        _log.Info("SharpCoderRunner ready — the LLM client is created lazily on first prompt.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tears down the current LLM client and session so the next <see cref="SendPromptAsync"/>
    /// creates a fresh client for <paramref name="model"/>.
    /// <para>
    /// The client field is set to <c>null</c> BEFORE <see cref="IDisposable.Dispose"/> is called,
    /// so a throwing disposal can never leave the field pointing at a half-disposed client that a
    /// later task would inherit. Disposal is therefore idempotent: a second call has nothing left
    /// to dispose. A disposal exception PROPAGATES — this is a hard teardown and a failure here is
    /// a fault, not something to swallow.
    /// </para>
    /// <para>
    /// The whole detach-and-dispose runs under <see cref="_clientLifecycleGate"/>, which also
    /// guards lazy creation in <see cref="SendPromptAsync"/>. That makes it impossible for a
    /// second task to observe or create a client while this task's dispose is in flight.
    /// The gate is released in a <c>finally</c> so a propagating disposal never strands it.
    /// </para>
    /// </summary>
    /// <param name="model">The model for the next client, or <c>null</c> for the SDK default.</param>
    /// <param name="reasoningEffort">The explicitly transported reasoning effort, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
    {
        await _clientLifecycleGate.WaitAsync(ct);
        try
        {
            // Reasoning effort is transported explicitly by the orchestrator; it is never derived
            // from the model name.
            _currentReasoning = reasoningEffort;
            _pendingModel = model;

            _log.Info($"Resetting session. Requested model: {model ?? "default"}" +
                (_currentReasoning.HasValue ? $", reasoning={_currentReasoning.Value}" : ", reasoning=(none)"));

            _session = null;

            // Null the field FIRST, then dispose the detached reference.
            var previous = _chatClient;
            _chatClient = null;
            previous?.Dispose();
        }
        finally
        {
            _clientLifecycleGate.Release();
        }
    }

    /// <summary>
    /// Creates the LLM client lazily, running orchestrator provisioning UNCONDITIONALLY
    /// immediately beforehand (never only when a credential looks absent), so a token that
    /// became available after the worker registered is picked up.
    /// <para>
    /// The caller MUST hold <see cref="_clientLifecycleGate"/>. <paramref name="model"/> is read
    /// once by the caller under that gate and passed in, so provisioning and construction can
    /// never straddle an intervening reset and provision model A while constructing model B.
    /// </para>
    /// </summary>
    private async Task<IChatClient> CreateClientLazilyAsync(string? model, CancellationToken ct)
    {
        if (_configProvisioner is not null)
            await _configProvisioner(model, ct);

        return _clientFactory?.Invoke(model)
            ?? ClientCreationSeam?.Invoke(model)
            ?? CreateChatClient(model);
    }

    /// <summary>
    /// Acquires the client for this prompt under <see cref="_clientLifecycleGate"/>, creating it
    /// lazily on first use.
    /// <para>
    /// The gate is deliberately NOT released here. It is held for the ENTIRE duration of the
    /// turn and released by the caller's <c>finally</c>, so <see cref="ResetSessionAsync"/> and
    /// <see cref="DisposeAsync"/> cannot null and dispose the client while a turn still holds
    /// the returned reference. Releasing at acquisition time (as an earlier revision did) left
    /// the returned reference unprotected for the whole of the agent run.
    /// </para>
    /// </summary>
    /// <returns>The client to use for this turn. The caller owns the gate until it releases it.</returns>
    private async Task<IChatClient> AcquireClientLeaseAsync(CancellationToken ct)
    {
        await _clientLifecycleGate.WaitAsync(ct);
        try
        {
            // Read the pending model under the gate, so provisioning and construction agree.
            _chatClient ??= await CreateClientLazilyAsync(_pendingModel, ct);
            return _chatClient;
        }
        catch
        {
            // Creation failed: release the lease here, because the caller never receives a
            // reference and therefore has no finally to run.
            _clientLifecycleGate.Release();
            throw;
        }
    }

    public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
    {
        // Lazy first creation, serialized: a fresh task never inherits another task's disposed
        // client, and two overlapping assignments can never both construct one. The lease is
        // held until the result has left the runner, so reset/dispose cannot overlap actual use.
        var chatClient = await AcquireClientLeaseAsync(ct);
        try
        {
            return await RunPromptTurnAsync(chatClient, prompt, workDir, ct);
        }
        finally
        {
            _clientLifecycleGate.Release();
        }
    }

    /// <summary>
    /// Runs one full agent turn against <paramref name="chatClient"/>. The caller holds the
    /// client lifecycle lease for the whole call, so the client cannot be disposed underneath it.
    /// </summary>
    private async Task<string> RunPromptTurnAsync(
        IChatClient chatClient, string prompt, string workDir, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        _log.Info($"Executing task as {_currentRole} with model {_currentModel}. WorkDir: {workDir}");

        var options = new AgentOptions
        {
            WorkDirectory = workDir,
            MaxSteps = 500,
            MaxContextTokens = _maxContextTokens,
            SystemPrompt = BuildRoleSystemPrompt(_currentRole, _customAgentSystemPrompt),
            CustomTools = BuildCustomTools(ct),
            EnableBash = _currentRole != WorkerRole.Improver,
            EnableFileWrites = _currentRole != WorkerRole.Reviewer,
            ReasoningEffort = _currentReasoning,
            ShowToolCallsInStream = true,
        };

        var subAgentOptions = BuildSubAgentOptions();
        if (subAgentOptions != null)
            options.SubAgents = subAgentOptions;

        if (!string.IsNullOrEmpty(_compactionModel))
            options.CompactionClient = ChatClientFactory.Create(_compactionModel);

        if (_compactionMaxTokens.HasValue)
            options.CompactionMaxTokens = _compactionMaxTokens.Value;

        OnAgentOptionsCreated?.Invoke(options);

        // Write pre-execution diagnostics so we can inspect inputs even if the LLM call hangs or is killed
        WriteDiagnosticsFile(null, prompt, TimeSpan.Zero, options, "pre");

        // Use the leased reference. The caller holds _clientLifecycleGate for this whole turn,
        // so ResetSessionAsync/DisposeAsync cannot null and dispose this client underneath us.
        await using var agent = new CodingAgent(chatClient, options);

        OnAgentCreated?.Invoke(agent);

        // Ensure session exists before streaming
        _session ??= AgentSession.Create(Guid.NewGuid().ToString("N"));

        // Drain the streaming response to update LastKnownContextTokens after each LLM turn
        var result = await DrainStreamingAsync(agent, _session, prompt, ct);

        stopwatch.Stop();
        var elapsedSecs = stopwatch.Elapsed.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _log.Info($"Task finished in {elapsedSecs}s (status={result.Status}, toolCalls={result.ToolCallCount})");

        // Log diagnostics when available
        if (result.Diagnostics is { } diag)
        {
            _log.Info($"Diagnostics: systemPrompt={diag.SystemPrompt.Length} chars, userMessage={diag.UserMessage.Length} chars, " +
                      $"historyMessages={diag.SessionHistoryCount}, totalMessages={diag.TotalMessageCount}");
            _log.Info($"Diagnostics: tools=[{string.Join(", ", diag.ToolNames)}], bash={diag.EnableBash}, " +
                      $"fileWrites={diag.EnableFileWrites}, skills={diag.SkillsEnabled}, autoWorkspace={diag.AutoLoadedWorkspaceInstructions}");
        }

        // Write post-execution diagnostics with full result
        WriteDiagnosticsFile(result, prompt, stopwatch.Elapsed, options, "post");

        _log.Info($"AgentResult: status={result.Status}, toolCalls={result.ToolCallCount}, model={result.ModelId}, finish={result.FinishReason}");
        if (result.Usage != null)
        {
            _log.Info($"Context: inputTokens={result.Usage.InputTokenCount}, outputTokens={result.Usage.OutputTokenCount}, totalTokens={result.Usage.TotalTokenCount}");
        }
        if (result.Messages != null)
        {
            _log.Info($"AgentResult: {result.Messages.Count} messages total");
            foreach (var msg in result.Messages)
            {
                _log.Info($"  [{msg.Role}] {SummarizeMessage(msg)}");
            }
        }

        if (result.Status != "Success")
        {
            _log.Error($"Agent finished with non-success status: {result.Status} - {result.Message}");
        }

        return result.Message;
    }

    /// <summary>
    /// Drains the streaming response from the agent, extracting the final AgentResult.
    /// This method ensures LastKnownContextTokens is updated after every LLM turn.
    /// </summary>
    private static async Task<AgentResult> DrainStreamingAsync(
        CodingAgent agent, AgentSession session, string prompt, CancellationToken ct)
    {
        AgentResult? result = null;
        await foreach (var update in agent.ExecuteStreamingAsync(session, prompt, ct))
        {
            if (update.Kind == StreamingUpdateKind.Completed)
            {
                result = update.Result;
            }
            // Discard TextDelta — no output is streamed in worker context
        }

        if (result == null)
            throw new InvalidOperationException("Streaming execution completed without a final AgentResult.");

        return result;
    }

    /// <summary>
    /// Disposes the owned LLM client. The field is nulled BEFORE disposal so a throwing
    /// <see cref="IDisposable.Dispose"/> can never leave a reference to a half-disposed client
    /// behind, which also makes repeated disposal safe.
    /// <para>
    /// Runs under <see cref="_clientLifecycleGate"/> so final teardown cannot overlap a task's
    /// lazy creation or a reset. The client disposal PROPAGATES (callers rely on that), so the
    /// gate is released in a <c>finally</c>. Repeat calls are no-ops: the gate itself is only
    /// disposed once, after the client disposal has been attempted.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Idempotent: a second call must not fault on the already-disposed gate.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _clientLifecycleGate.WaitAsync();
        try
        {
            var previous = _chatClient;
            _chatClient = null;
            previous?.Dispose();
        }
        finally
        {
            _clientLifecycleGate.Release();
            _clientLifecycleGate.Dispose();
        }
    }

    private static string SummarizeMessage(ChatMessage msg)
    {
        const int ArgValueMaxLength = 100;
        const int PreviewMaxLength = 200;

        var functionCall = msg.Contents?.OfType<FunctionCallContent>().FirstOrDefault();
        if (functionCall != null)
        {
            var firstArg = functionCall.Arguments?.FirstOrDefault();
            if (firstArg.HasValue)
            {
                var argValue = firstArg.Value.Value?.ToString() ?? string.Empty;
                if (argValue.Length > ArgValueMaxLength)
                    argValue = argValue.Substring(0, ArgValueMaxLength);
                return $"tool:{functionCall.Name}({firstArg.Value.Key}=\"{argValue}\")";
            }
            return $"tool:{functionCall.Name}()";
        }

        var functionResult = msg.Contents?.OfType<FunctionResultContent>().FirstOrDefault();
        if (functionResult != null)
        {
            var raw = functionResult.Result?.ToString() ?? string.Empty;
            return $"result:{functionResult.CallId} \u2192 {SummarizeToolResult(raw)}";
        }

        var text = msg.Text;
        if (text != null && text.Length > PreviewMaxLength)
            text = text.Substring(0, PreviewMaxLength);
        return text ?? string.Empty;
    }

    /// <summary>
    /// Produces a compact one-line summary of a tool result instead of dumping raw content.
    /// </summary>
    private static string SummarizeToolResult(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "(empty)";

        var lines = raw.Split('\n');
        var lineCount = lines.Length;
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(raw);

        // Short single-line results can be shown inline
        if (lineCount == 1 && raw.Length <= 120)
            return $"\"{raw}\"";

        return $"{byteCount} bytes, {lineCount} lines";
    }

    private static readonly string DiagnosticsDir =
        Environment.GetEnvironmentVariable("DIAGNOSTICS_DIR") ?? Path.Combine(Path.GetTempPath(), "copilothive-diagnostics");

    private void WriteDiagnosticsFile(AgentResult? result, string userPrompt, TimeSpan elapsed, AgentOptions options, string phase)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsDir);

            var taskId = _currentTaskId ?? "unknown";
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = $"{timestamp}_{taskId}_{phase}.json";
            var filePath = Path.Combine(DiagnosticsDir, fileName);

            var toolNames = options.CustomTools
                .Select(t => t is AIFunction f ? f.Name : t.GetType().Name)
                .ToList();

            // For pre-execution: use options directly; for post: use diagnostics from result
            var diag = result?.Diagnostics;
            var doc = new
            {
                phase,
                taskId,
                role = _currentRole.ToString(),
                model = _currentModel,
                reasoning = _currentReasoning?.ToString(),
                timestamp = DateTimeOffset.UtcNow,
                elapsedSeconds = elapsed.TotalSeconds,
                status = result?.Status,
                toolCallCount = result?.ToolCallCount,
                finishReason = result?.FinishReason?.ToString(),
                usage = result?.Usage is { } u ? new
                {
                    inputTokens = u.InputTokenCount,
                    outputTokens = u.OutputTokenCount,
                    totalTokens = u.TotalTokenCount
                } : null,
                session = new
                {
                    sessionHistoryCount = diag?.SessionHistoryCount ?? 0,
                    totalMessageCount = diag?.TotalMessageCount ?? 0,
                    maxSteps = options.MaxSteps,
                    enableBash = options.EnableBash,
                    enableFileWrites = options.EnableFileWrites,
                    autoLoadedWorkspaceInstructions = options.AutoLoadWorkspaceInstructions,
                    skillsEnabled = options.EnableSkills,
                    reasoningEffort = options.ReasoningEffort?.ToString(),
                    workDirectory = options.WorkDirectory,
                    customToolNames = toolNames,
                    allToolNames = diag?.ToolNames ?? (IReadOnlyList<string>)toolNames
                },
                systemPrompt = diag?.SystemPrompt ?? options.SystemPrompt ?? "(not yet assembled)",
                userMessage = userPrompt,
                agentResponse = result?.Message
            };

            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(filePath, json);
            _log.Info($"Diagnostics ({phase}) written to {filePath} ({json.Length} bytes)");
        }
        catch (Exception ex)
        {
            // Sanitized: the diagnostics document embeds the assembled prompt and the agent
            // result, so a serialization failure can quote provisioned content back in its
            // message. Only the exception classification is logged.
            _log.Error($"Failed to write diagnostics file [{SafeExceptionLog.Describe(ex)}]");
        }
    }

    private IChatClient CreateChatClient(string? modelOverride = null)
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel(modelOverride);
        _currentModel = model ?? "(default)";
        _log.Info($"Creating chat client: provider={provider}, model={_currentModel}" +
            (_currentReasoning.HasValue ? $", reasoning={_currentReasoning.Value}" : ""));
        return ChatClientFactory.Create(modelOverride);
    }

    /// <summary>
    /// Builds the custom tool set for this turn.
    /// <para>
    /// <paramref name="ct"/> is the ASSIGNMENT'S token and MUST be forwarded to every bridge
    /// call. The bridge registers <c>ct.Register(() =&gt; tcs.TrySetCanceled())</c> on each pending
    /// tool request, so a tool that is waiting for a <c>ToolResponse</c> is only released when a
    /// live token is cancelled. Passing <see cref="CancellationToken.None"/> here (as an earlier
    /// revision did) permanently detached those waits: cancelling the assignment could not
    /// release a pending <c>request_clarification</c> / <c>get_goal</c> / <c>raise_issue</c>, so
    /// the drain in <c>WorkerService.ProcessMessagesAsync</c> blocked forever while this runner
    /// still held the full-turn client lease — deadlocking teardown and preventing disposal.
    /// </para>
    /// </summary>
    /// <param name="ct">The assignment's cancellation token, forwarded to every bridge call.</param>
    private IList<AITool> BuildCustomTools(CancellationToken ct)
    {
        var tools = new List<AITool>();

        if (_toolBridge != null)
        {
            tools.Add(AIFunctionFactory.Create(
                async ([Description("Short status summary")] string status,
                       [Description("Detailed progress explanation")] string details) =>
                {
                    if (string.IsNullOrEmpty(_currentTaskId)) return "Error: Task ID not set.";
                    _log.Info($"Tool call: report_progress({status})");
                    await _toolBridge.ReportProgressAsync(_currentTaskId, status, details, ct);
                    return "Progress reported.";
                },
                "report_progress",
                "Report current progress to the orchestrator."
            ));

            tools.Add(AIFunctionFactory.Create(
                async ([Description("2-5 sentence narrative of what you tried, what worked, what you struggled with, and why")] string narrative) =>
                {
                    if (string.IsNullOrEmpty(_currentTaskId)) return "Error: Task ID not set.";
                    _log.Info("Tool call: report_narrative()");
                    await _toolBridge.ReportNarrativeAsync(_currentTaskId, narrative, ct);
                    return "Narrative recorded.";
                },
                "report_narrative",
                "Report a narrative summary of your work experience to help the system learn and improve."
            ));

            tools.Add(AIFunctionFactory.Create(
                async ([Description("The question to ask the orchestrator")] string question) =>
                {
                    if (string.IsNullOrEmpty(_currentTaskId)) return "Error: Task ID not set.";
                    _log.Info($"Tool call: request_clarification({question})");
                    var response = await _toolBridge.RequestClarificationAsync(_currentTaskId, question, ct);
                    return response;
                },
                "request_clarification",
                "Ask the orchestrator for clarification when the goal description is ambiguous, files-to-change seem incomplete, or acceptance criteria conflict. Do NOT silently work around ambiguities — ask first."
            ));

            tools.Add(AIFunctionFactory.Create(
                async () =>
                {
                    if (string.IsNullOrEmpty(_currentTaskId)) return "Error: Task ID not set.";
                    if (string.IsNullOrEmpty(_currentGoalId)) return "Error: Goal ID not set.";
                    _log.Info($"Tool call: get_goal()");
                    var response = await _toolBridge.GetGoalAsync(_currentTaskId, _currentGoalId, ct);
                    return response;
                },
                "get_goal",
                "Fetch the full goal description and acceptance criteria directly from the orchestrator."
            ));

            tools.Add(AIFunctionFactory.Create(
                async ([Description("Issue type: code_quality, bug, suggestion, concern, workflow")] string type,
                       [Description("Short title summarizing the issue")] string title,
                       [Description("Detailed description of the issue")] string description,
                       [Description("Severity: low, medium, high (default: low)")] string? severity = null) =>
                {
                    if (string.IsNullOrEmpty(_currentTaskId)) return "Error: Task ID not set.";
                    _log.Info($"Tool call: raise_issue({type}: {title})");
                    var response = await _toolBridge.RaiseIssueAsync(_currentTaskId, type, title, description, severity ?? "low", ct);
                    return response;
                },
                "raise_issue",
                "Raise an issue for things you notice that are out of scope for the current goal: code quality problems, bugs in existing code, suggestions, concerns, or workflow issues."
            ));
        }

        if (_currentRole == WorkerRole.Tester)
            tools.Add(BuildTestResultsTool());

        if (_currentRole == WorkerRole.Reviewer)
        {
            tools.Add(BuildReviewVerdictTool());
            tools.Add(AIFunctionFactory.Create(
                () =>
                {
                    _log.Info("Tool call: get_test_report()");
                    return string.IsNullOrWhiteSpace(_testerReport)
                        ? "No test report available — the testing phase was not part of this iteration's plan, or no results were recorded."
                        : _testerReport;
                },
                "get_test_report",
                "Retrieve the tester's structured report for this iteration, including build success, test counts, and verdict. Call this to verify build/test acceptance criteria."));
        }

        if (_currentRole == WorkerRole.Coder)
            tools.Add(BuildCodeChangesTool());

        if (_currentRole == WorkerRole.DocWriter)
            tools.Add(BuildDocChangesTool());

        if (_currentRole == WorkerRole.Improver)
            tools.Add(BuildFileSizesTool());

        return tools;
    }

    private AITool BuildTestResultsTool() => AIFunctionFactory.Create(
        ([Description("PASS or FAIL")] string verdict,
         [Description("Total number of tests")] int totalTests,
         [Description("Number of tests that passed")] int passedTests,
         [Description("Number of tests that failed")] int failedTests,
         [Description("Code coverage percentage (0-100), or -1 if not available")] double coveragePercent,
         [Description("Build succeeded (true/false)")] bool buildSuccess,
         [Description("List of issues found, empty if none")] string[] issues,
         [Description("Summary of test results, issues found, and any relevant context")] string summary) =>
        {
            var parsed = TaskVerdictExtensions.ParseTaskVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is TaskVerdict.Pass or TaskVerdict.Fail, "verdict must be exactly 'PASS' or 'FAIL'"),
                (totalTests >= 0, "totalTests must be >= 0"),
                (passedTests >= 0, "passedTests must be >= 0"),
                (failedTests >= 0, "failedTests must be >= 0"),
                (passedTests + failedTests <= totalTests,
                    $"passedTests ({passedTests}) + failedTests ({failedTests}) must not exceed totalTests ({totalTests})"),
                (coveragePercent is >= -1 and <= 100,
                    $"coveragePercent must be -1 (unavailable) or 0-100, got {coveragePercent}"));
            if (error != null) return error;

            _log.Info($"Tool call: report_test_results(verdict={verdict}, total={totalTests}, passed={passedTests}, failed={failedTests}, coverage={coveragePercent})");
            _lastTestReport = new TestResultReport
            {
                Verdict = parsed!.Value,
                TotalTests = totalTests,
                PassedTests = passedTests,
                FailedTests = failedTests,
                CoveragePercent = coveragePercent >= 0 ? coveragePercent : null,
                BuildSuccess = buildSuccess,
                Issues = issues.ToList(),
                Summary = summary,
            };
            return "Test results recorded.";
        },
        "report_test_results",
        "Report structured test results. REQUIRED for testers after running tests.");

    private AITool BuildReviewVerdictTool() => AIFunctionFactory.Create(
        ([Description("APPROVE or REQUEST_CHANGES")] string verdict,
         [Description("List of issues found, empty if none")] string[] issues,
         [Description("Overall review summary")] string summary) =>
        {
            var parsed = ReviewVerdictExtensions.ParseReviewVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is not null, "verdict must be exactly 'APPROVE' or 'REQUEST_CHANGES'"));
            if (error != null) return error;

            _log.Info($"Tool call: report_review_verdict(verdict={verdict}, issues={issues.Length})");
            _lastWorkerReport = new WorkerReport
            {
                ReviewVerdict = parsed!.Value,
                Issues = issues.ToList(),
                Summary = summary,
            };
            return "Review verdict recorded.";
        },
        "report_review_verdict",
        "Report your code review verdict. REQUIRED for reviewers after completing the review.");

    private AITool BuildCodeChangesTool() => AIFunctionFactory.Create(
        ([Description("PASS or FAIL")] string verdict,
         [Description("List of files modified")] string[] filesModified,
         [Description("Summary of changes made")] string summary) =>
        {
            var parsed = TaskVerdictExtensions.ParseTaskVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is TaskVerdict.Pass or TaskVerdict.Fail, "verdict must be exactly 'PASS' or 'FAIL'"));
            if (error != null) return error;

            _log.Info($"Tool call: report_code_changes(verdict={verdict}, files={filesModified.Length})");
            _lastWorkerReport = new WorkerReport
            {
                TaskVerdict = parsed!.Value,
                FilesChanged = filesModified.ToList(),
                Summary = summary,
            };
            return "Code changes recorded.";
        },
        "report_code_changes",
        "Report your code changes. REQUIRED for coders after implementing and committing.");

    private AITool BuildDocChangesTool() => AIFunctionFactory.Create(
        ([Description("PASS or FAIL")] string verdict,
         [Description("List of documentation files updated")] string[] filesUpdated,
         [Description("Summary of documentation changes")] string summary) =>
        {
            var parsed = TaskVerdictExtensions.ParseTaskVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is TaskVerdict.Pass or TaskVerdict.Fail, "verdict must be exactly 'PASS' or 'FAIL'"));
            if (error != null) return error;

            _log.Info($"Tool call: report_doc_changes(verdict={verdict}, files={filesUpdated.Length})");
            _lastWorkerReport = new WorkerReport
            {
                TaskVerdict = parsed!.Value,
                FilesChanged = filesUpdated.ToList(),
                Summary = summary,
            };
            return "Documentation changes recorded.";
        },
        "report_doc_changes",
        "Report your documentation changes. REQUIRED for doc-writers after updating docs.");

    private AITool BuildFileSizesTool() => AIFunctionFactory.Create(
        ([Description("Glob pattern to match files, e.g. '*.md' or '**/*.agents.md'. Leave empty for all files.")] string pattern) =>
        {
            _log.Info($"Tool call: get_file_sizes(pattern={pattern})");
            try
            {
                var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
                var searchOption = searchPattern.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var normalizedPattern = searchPattern.Replace("**/", "");

                var files = Directory.GetFiles(Path.Combine(_configRepoDir, "agents"), normalizedPattern, searchOption);
                if (files.Length == 0)
                    return "No files matched the pattern.";

                var lines = files.Select(f =>
                {
                    var info = new FileInfo(f);
                    var content = File.ReadAllText(f);
                    return $"{Path.GetFileName(f)}: {content.Length} chars, {info.Length} bytes";
                });
                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        },
        "get_file_sizes",
        "Get character and byte counts for files in the agents directory. Use before editing to check against the 4000-character limit.");
}
