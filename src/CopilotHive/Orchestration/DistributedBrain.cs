using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CopilotHive.Metrics;
using CopilotHive.Services;
using CopilotHive.Telemetry;
using CopilotHive.Workers;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace CopilotHive.Orchestration;

/// <summary>
/// LLM-powered brain that runs inside the orchestrator container.
/// The Brain has two jobs: plan iteration phases and craft worker prompts.
/// Workers report structured verdicts; the state machine drives sequencing.
/// </summary>
public sealed class DistributedBrain : IDistributedBrain, IAsyncDisposable
{
    private readonly int _port;
    private readonly ILogger<DistributedBrain> _logger;
    private readonly MetricsTracker? _metricsTracker;
    private CopilotClient? _copilotClient;
    private readonly ConcurrentDictionary<string, CopilotSession> _sessions = new();

    private readonly CustomAgentConfig _orchestratorAgent;
    private readonly List<AIFunction> _brainTools;

    /// <summary>
    /// Serialises all Brain LLM calls so that <see cref="_lastToolCallResult"/> is
    /// never overwritten by a concurrent goal's tool lambda.
    /// </summary>
    private readonly SemaphoreSlim _brainCallGate = new(1, 1);

    /// <summary>
    /// Stores the last tool call result captured by the Brain tool lambdas.
    /// Written by tool lambdas (from SDK thread), read by <see cref="SendToSessionCoreAsync"/>
    /// after <see cref="SessionIdleEvent"/> fires.
    /// Access is serialised by <see cref="_brainCallGate"/>.
    /// </summary>
    private volatile BrainToolCallResult? _lastToolCallResult;

    private const string SystemPrompt = """
        You are the CopilotHive Orchestrator Brain — a product owner and project manager.
        You have two jobs:
        1. Plan iteration phases using the report_iteration_plan tool
        2. Craft clear, specific prompts for workers when asked

        RULES:
        - Do NOT run shell commands, tools, or file operations
        - When planning iterations, always call report_iteration_plan
        - When crafting prompts, respond with ONLY the prompt text — no tool calls, no JSON, no markdown formatting
        - Never include git checkout/branch/switch/push commands in prompts — infrastructure handles branching
        - Never include framework-specific build/test commands — workers use /build and /test skills
        """;

    /// <summary>
    /// Permission handler that approves all tool calls.
    /// Built-in tools (shell, read, write, url) are already blocked by --deny-tool CLI flags
    /// in entrypoint.sh. Custom tools (report_iteration_plan) need approval to execute.
    /// </summary>
    private static readonly PermissionRequestHandler ApproveBrainTools =
        (_, _) => Task.FromResult(new PermissionRequestResult
        {
            Kind = PermissionRequestResultKind.Approved,
        });

    /// <summary>
    /// Initialises a new <see cref="DistributedBrain"/> that connects to the Copilot CLI on the given port.
    /// </summary>
    /// <param name="port">TCP port the Copilot CLI is listening on.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="metricsTracker">Optional tracker used to include historical metrics in prompts.</param>
    /// <param name="agentsManager">Optional manager used to load the orchestrator's AGENTS.md.</param>
    public DistributedBrain(int port, ILogger<DistributedBrain> logger,
        MetricsTracker? metricsTracker = null, Agents.AgentsManager? agentsManager = null)
    {
        _port = port;
        _logger = logger;
        _metricsTracker = metricsTracker;

        _brainTools = BuildBrainTools();

        // Build the orchestrator custom agent — Tools = null allows all tools.
        // Custom tools are registered on SessionConfig.Tools (AIFunction list).
        // Built-in tools are blocked by --deny-tool CLI flags in entrypoint.sh.
        // Setting an explicit tool name list here causes the CLI to look up names in its
        // built-in registry, which fails for custom tools with a JS TypeError.
        var orchestratorInstructions = agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
        _orchestratorAgent = new CustomAgentConfig
        {
            Name = "orchestrator",
            DisplayName = "CopilotHive Orchestrator",
            Description = "LLM-powered product owner and project manager — plans iterations and crafts prompts",
            Prompt = string.IsNullOrWhiteSpace(orchestratorInstructions)
                ? SystemPrompt
                : $"{SystemPrompt}\n\n{orchestratorInstructions}",
            Tools = null,
            Infer = false,
        };
    }

    /// <summary>
    /// Connects to the Copilot CLI, retrying up to <see cref="Constants.DistributedBrainMaxRetries"/> times with a 5-second backoff.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _copilotClient = new CopilotClient(new CopilotClientOptionsWithTelemetry
        {
            CliUrl = $"localhost:{_port}",
            AutoStart = false,
            Telemetry = new TelemetryConfig
            {
                FilePath = "/app/state/otel-brain.jsonl",
                ExporterType = "file",
                SourceName = "copilothive-brain",
                CaptureContent = true
            }
        });

        // Retry connection to Copilot CLI
        Exception? lastException = null;
        for (var attempt = 1; attempt <= Constants.DistributedBrainMaxRetries; attempt++)
        {
            try
            {
                await _copilotClient.StartAsync();
                _logger.LogInformation("Brain connected to Copilot on port {Port} (attempt {Attempt})", _port, attempt);
                return;
            }
            catch (Exception ex) when (attempt < Constants.DistributedBrainMaxRetries)
            {
                lastException = ex;
                _logger.LogDebug("Brain connection attempt {Attempt}/{Max} failed: {Message}", attempt, Constants.DistributedBrainMaxRetries, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Constants.RetryDelaySeconds), ct);
            }
        }

        throw new InvalidOperationException(
            $"Brain failed to connect to Copilot on port {_port} after {Constants.DistributedBrainMaxRetries} attempts: {lastException?.Message}");
    }

    /// <summary>
    /// Builds the standard SessionConfig for Brain sessions with InfiniteSessions enabled
    /// to prevent context window exhaustion on complex goals.
    /// </summary>
    private SessionConfig BuildBrainSessionConfig() => new()
    {
        Streaming = false,
        OnPermissionRequest = ApproveBrainTools,
        CustomAgents = [_orchestratorAgent],
        Tools = _brainTools,
        InfiniteSessions = new InfiniteSessionConfig
        {
            Enabled = true,
            BackgroundCompactionThreshold = 0.80,
            BufferExhaustionThreshold = 0.95,
        },
    };

    /// <summary>
    /// Builds the AIFunction tools that the Brain LLM can call.
    /// Only <c>report_iteration_plan</c> — prompt crafting uses plain text responses.
    /// </summary>
    private List<AIFunction> BuildBrainTools()
    {
        var validPhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "coding", "testing", "docwriting", "review", "improve", "merging" };

        return
        [
            AIFunctionFactory.Create(
                ([Description("Ordered phase names, e.g. [\"coding\",\"testing\",\"docwriting\",\"review\",\"merging\"]")] string[] phases,
                 [Description("JSON-encoded dict of phase name to instruction, e.g. {\"coding\":\"focus on...\",\"review\":\"check...\"}")] string phase_instructions,
                 [Description("Why you chose this iteration plan")] string reason) =>
                {
                    var invalidPhases = phases?.Where(p => !validPhases.Contains(p)).ToList() ?? [];
                    var error = Shared.ToolValidation.Check(
                        (phases is { Length: > 0 }, "phases must be a non-empty array"),
                        (invalidPhases.Count == 0,
                            $"invalid phase names: {string.Join(", ", invalidPhases)}. Valid: {string.Join(", ", validPhases)}"),
                        (!string.IsNullOrEmpty(reason), "reason is required"));
                    if (error is not null) return error;

                    _lastToolCallResult = new BrainToolCallResult("report_iteration_plan", new Dictionary<string, object?>
                    {
                        ["phases"] = phases, ["phase_instructions"] = phase_instructions, ["reason"] = reason,
                    });
                    return "Iteration plan recorded.";
                },
                "report_iteration_plan",
                "Report your iteration plan — which phases to run and in what order."),
        ];
    }

    /// <summary>Result captured from a Brain tool call.</summary>
    internal sealed record BrainToolCallResult(string ToolName, Dictionary<string, object?> Arguments);

    /// <summary>
    /// Gets or creates a dedicated Copilot session for a goal.
    /// Each session maintains its own conversation history natively.
    /// </summary>
    private async Task<CopilotSession> GetOrCreateSessionAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        EnsureConnected();

        if (_sessions.TryGetValue(pipeline.GoalId, out var existing))
            return existing;

        var session = await _copilotClient!.CreateSessionAsync(BuildBrainSessionConfig());

        // Pre-select the orchestrator agent so the role-specific prompt is active
        // from the first message — without this the runtime relies on inference.
        try
        {
            await session.Rpc.Agent.SelectAsync(_orchestratorAgent.Name);
            _logger.LogDebug("Pre-selected orchestrator agent via RPC for goal {GoalId}", pipeline.GoalId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pre-select orchestrator agent for goal {GoalId}", pipeline.GoalId);
        }

        if (_sessions.TryAdd(pipeline.GoalId, session))
        {
            // Prime the new session with the system prompt and goal context
            await SendToSessionAsync(session, $"""
                {SystemPrompt}

                You are now working on goal '{pipeline.GoalId}':
                {pipeline.Description}

                Acknowledge briefly and await instructions.
                """, ct);

            _logger.LogInformation("Created new Copilot session for goal {GoalId}", pipeline.GoalId);
            return session;
        }

        // Another thread beat us — dispose ours and use theirs
        await session.DisposeAsync();
        return _sessions[pipeline.GoalId];
    }

    /// <summary>Removes and disposes the session for a completed/failed goal.</summary>
    /// <param name="goalId">Identifier of the goal whose session should be removed.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task CleanupGoalSessionAsync(string goalId)
    {
        if (_sessions.TryRemove(goalId, out var session))
        {
            await session.DisposeAsync();
            _logger.LogInformation("Cleaned up Copilot session for goal {GoalId}", goalId);
        }
    }

    /// <summary>
    /// Re-creates a session and replays persisted conversation history into it.
    /// Used on restart to restore Brain context for active goals.
    /// </summary>
    /// <param name="pipeline">The goal pipeline whose session should be re-primed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task ReprimeSessionAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        EnsureConnected();

        // Remove any stale session
        if (_sessions.TryRemove(pipeline.GoalId, out var stale))
            await stale.DisposeAsync();

        var session = await _copilotClient!.CreateSessionAsync(BuildBrainSessionConfig());

        // Replay the conversation: alternate user/assistant messages
        var entries = pipeline.Conversation;
        if (entries.Count > 0)
        {
            // Send the system prompt + summary of prior conversation as a single priming message
            var summary = string.Join("\n\n", entries.Select(e => $"[{e.Role}]: {e.Content}"));
            await SendToSessionAsync(session, $"""
                {SystemPrompt}

                You are resuming work on goal '{pipeline.GoalId}':
                {pipeline.Description}

                Here is the conversation so far (summarized from {entries.Count} messages):
                {Truncate(summary, Constants.TruncationFull)}

                Continue from where we left off. The current phase is {pipeline.Phase},
                iteration {pipeline.Iteration}. Acknowledge briefly and await instructions.
                """, ct);
        }
        else
        {
            // No conversation — just prime with system prompt
            await SendToSessionAsync(session, $"""
                {SystemPrompt}

                You are resuming work on goal '{pipeline.GoalId}':
                {pipeline.Description}

                This is a resumed session. Current phase: {pipeline.Phase}, iteration {pipeline.Iteration}.
                Acknowledge briefly and await instructions.
                """, ct);
        }

        _sessions[pipeline.GoalId] = session;
        _logger.LogInformation("Re-primed Copilot session for goal {GoalId} with {Count} conversation entries",
            pipeline.GoalId, entries.Count);
    }

    /// <summary>
    /// Asks the Brain to plan which phases should run during the current iteration.
    /// </summary>
    /// <param name="pipeline">Current goal pipeline state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="IterationPlan"/> with the ordered list of phases to execute.</returns>
    public async Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        var metricsContext = pipeline.Iteration > 1
            ? $"""
              Previous iteration metrics:
              - Tests: {pipeline.Metrics.PassedTests}/{pipeline.Metrics.TotalTests} passed
              - Coverage: {pipeline.Metrics.CoveragePercent}%
              - Review retries: {pipeline.ReviewRetries}
              - Test retries: {pipeline.TestRetries}
              - Issues: {string.Join(", ", pipeline.Metrics.Issues)}
              """
            : "This is the first iteration — no previous metrics.";

        var historyContext = BuildMetricsHistoryContext();

        var failureContext = pipeline.Phase == GoalPhase.Failed || pipeline.TestRetries > 0 || pipeline.ReviewRetries > 0
            ? $"""
              Failure context:
              - Current phase: {pipeline.Phase}
              - Review retries so far: {pipeline.ReviewRetries}
              - Test retries so far: {pipeline.TestRetries}
              This is a retry — consider which phases need re-running and which can be skipped.
              """
            : "";

        var conversationSummary = pipeline.Conversation.Count > 0
            ? $"Conversation history ({pipeline.Conversation.Count} messages): " +
              Truncate(string.Join(" | ", pipeline.Conversation.Select(e => $"[{e.Role}] {e.Content}")), Constants.TruncationConversationSummary)
            : "";

        var prompt = $$"""
            Plan the workflow for iteration {{pipeline.Iteration}} of goal: {{pipeline.Description}}

            {{metricsContext}}
            {{historyContext}}
            {{failureContext}}
            {{conversationSummary}}

            Decide the ordered phases for this iteration. Consider:
            - Is this a documentation-only change? (coder edits, then docwriter — may skip testing)
            - Is this a retry after failure? (what phases need re-running)
            - What does the metrics history suggest?
            IMPORTANT: Always include the docwriting phase — the doc-writer updates the CHANGELOG,
            README, and XML doc comments. Even internal changes need changelog entries.
            Include the improve phase to let the improver refine agents.md guidance based on
            how the iteration went — especially when steps needed retries or produced issues.

            Available phases: coding, testing, docwriting, review, improve, merging

            Call the `report_iteration_plan` tool with:
            - phases: ordered list of phase names
            - phase_instructions: JSON object with per-phase instructions (e.g. {"coding": "focus on...", "review": "check..."})
            - reason: why this plan
            """;

        try
        {
            var session = await GetOrCreateSessionAsync(pipeline, ct);
            var (response, toolCall) = await Shared.CopilotRetryPolicy.ExecuteAsync(
                () => SendToSessionCoreAsync(session, prompt, ct),
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain iteration plan call failed (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                },
                ct);

            pipeline.Conversation.Add(new ConversationEntry("user", prompt));
            pipeline.Conversation.Add(new ConversationEntry("assistant", response));

            if (toolCall is not { ToolName: "report_iteration_plan" })
                throw new InvalidOperationException(
                    $"Brain did not call report_iteration_plan for {pipeline.GoalId}. Tool: {toolCall?.ToolName ?? "none"}");

            var plan = BuildIterationPlanFromToolCall(toolCall);

            if (plan is { Phases.Count: > 0 })
            {
                _logger.LogInformation(
                    "Brain planned iteration {Iteration} for goal {GoalId}: [{Phases}] — {Reason}",
                    pipeline.Iteration, pipeline.GoalId,
                    string.Join(", ", plan.Phases), plan.Reason ?? "no reason");
                return plan;
            }

            _logger.LogWarning("Failed to parse iteration plan from Brain response: {Response}",
                Truncate(response, Constants.TruncationShort));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain iteration planning failed for goal {GoalId}", pipeline.GoalId);
            pipeline.Conversation.Add(new ConversationEntry("system", $"Error: {ex.Message}"));
        }

        _logger.LogInformation("Using default iteration plan for goal {GoalId}", pipeline.GoalId);
        return IterationPlan.Default();
    }

    internal static IterationPlan MapIterationPlan(IterationPlanDto dto)
    {
        var phases = new List<GoalPhase>();
        foreach (var name in dto.Phases)
        {
            if (Enum.TryParse<GoalPhase>(name, ignoreCase: true, out var phase)
                && phase is not (GoalPhase.Planning or GoalPhase.Done or GoalPhase.Failed))
            {
                phases.Add(phase);
            }
        }

        var instructions = new Dictionary<GoalPhase, string>();
        if (dto.PhaseInstructions is not null)
        {
            foreach (var (key, value) in dto.PhaseInstructions)
            {
                if (Enum.TryParse<GoalPhase>(key, ignoreCase: true, out var phase) && value is not null)
                    instructions[phase] = value;
            }
        }

        return new IterationPlan
        {
            Phases = phases,
            PhaseInstructions = instructions,
            Reason = dto.Reason,
        };
    }

    /// <summary>
    /// Builds an <see cref="IterationPlan"/> from a <c>report_iteration_plan</c> tool call result.
    /// </summary>
    internal static IterationPlan BuildIterationPlanFromToolCall(BrainToolCallResult toolCall)
    {
        var args = toolCall.Arguments;

        var phaseNames = new List<string>();
        if (args.TryGetValue("phases", out var phasesVal) && phasesVal is not null)
        {
            if (phasesVal is string[] arr)
                phaseNames = arr.ToList();
            else if (phasesVal is JsonElement je && je.ValueKind == JsonValueKind.Array)
                phaseNames = je.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            else if (phasesVal is IEnumerable<object> list)
                phaseNames = list.Select(x => x?.ToString() ?? "").ToList();
        }

        Dictionary<string, string>? phaseInstructions = null;
        var piStr = GetStringArg(args, "phase_instructions");
        if (!string.IsNullOrEmpty(piStr))
        {
            try
            {
                phaseInstructions = JsonSerializer.Deserialize<Dictionary<string, string>>(piStr, ProtocolJson.Options);
            }
            catch (JsonException)
            {
                // Best-effort: ignore unparsable phase instructions
            }
        }

        var dto = new IterationPlanDto
        {
            Phases = phaseNames,
            PhaseInstructions = phaseInstructions,
            Reason = GetStringArg(args, "reason"),
        };

        return MapIterationPlan(dto);
    }

    /// <summary>DTO for deserializing the Brain's iteration plan JSON response.</summary>
    internal sealed record IterationPlanDto
    {
        /// <summary>Ordered list of phase names the Brain wants to execute this iteration.</summary>
        public List<string> Phases { get; init; } = [];
        /// <summary>Per-phase instructions keyed by phase name, providing context for each phase.</summary>
        public Dictionary<string, string>? PhaseInstructions { get; init; }
        /// <summary>The Brain's reasoning for choosing this iteration plan.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Asks the Brain to craft a prompt for the specified phase's worker.
    /// Sends a message to the Brain session and returns the assistant's plain text response.
    /// </summary>
    /// <param name="pipeline">Current goal pipeline state.</param>
    /// <param name="phase">The phase whose worker needs a prompt.</param>
    /// <param name="additionalContext">Optional extra context to include in the prompt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The crafted prompt string.</returns>
    public async Task<string> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        var roleName = phase.ToWorkerRole().ToRoleName();
        var historyContext = BuildMetricsHistoryContext(3);

        var phaseInstructions = "";
        if (pipeline.Plan?.PhaseInstructions.TryGetValue(phase, out var instructions) == true)
            phaseInstructions = $"\nPhase instructions from the plan:\n{instructions}";

        var roleInstruction = phase switch
        {
            GoalPhase.Coding => """
                - For coders: Tell them to start implementing immediately — read the relevant files, make code changes, use /build skill, use /test skill, and commit with `git add -A && git commit`. NEVER include git checkout, git branch, or git push commands. NEVER include dotnet/npm/cargo commands — only reference /build and /test skills.
                """,
            GoalPhase.Review => """
                - For reviewers: tell them to run `git diff origin/<base-branch>...HEAD` to review ALL changes. The base branch and feature branch are provided in the workspace context. The `origin/` prefix is required because the clone only has remote tracking refs. Produce a REVIEW_REPORT.
                """,
            GoalPhase.Testing => """
                - For testers: tell them to build, run the test skill, write integration tests, produce a TEST_REPORT
                """,
            GoalPhase.DocWriting => """
                - For docwriters: tell them to update README, CHANGELOG, and XML doc comments based on the code changes on the branch, build to verify, and commit. Produce a DOC_REPORT.
                """,
            GoalPhase.Improve => """
                - For improvers: tell them to analyze iteration results and update *.agents.md files directly using file tools. Do NOT tell them to run git commands — the infrastructure commits and pushes automatically.
                """,
            _ => throw new InvalidOperationException($"Unhandled phase in CraftPromptAsync: '{phase}'"),
        };

        var prompt = $$"""
            Craft a prompt for the {{roleName}} worker.

            Goal: {{pipeline.Description}}
            Iteration: {{pipeline.Iteration}}
            Current phase: {{phase}}
            {{phaseInstructions}}
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}
            {{(historyContext.Length > 0 ? $"\n{historyContext}" : "")}}

            The worker has access to project skills (e.g. /build, /test) that describe how to build and test this project.
            Tell the worker to use those skills instead of hardcoding framework-specific commands.

            Rules for the prompt you craft:
            - The branch is already checked out by the infrastructure — do NOT mention branch names
            - NEVER include git checkout, git branch, git switch, or git push commands — the infrastructure handles all branching
            - NEVER include framework-specific build/test commands (dotnet build, npm test, etc.) — tell workers to use /build and /test skills
            {{roleInstruction}}
            - Include any context from previous phases that would help the worker

            Respond with ONLY the prompt text — no tool calls, no JSON, no markdown wrapping.
            """;

        try
        {
            var session = await GetOrCreateSessionAsync(pipeline, ct);

            var craftedPrompt = await Shared.CopilotRetryPolicy.ExecuteAsync(
                async () =>
                {
                    _logger.LogDebug("Brain craft-prompt request for {GoalId} (phase={Phase}):\n{Prompt}",
                        pipeline.GoalId, phase, Truncate(prompt, Constants.TruncationVerbose));

                    var (response, _) = await SendToSessionCoreAsync(session, prompt, ct);

                    _logger.LogDebug("Brain craft-prompt response for {GoalId}:\n{Response}",
                        pipeline.GoalId, Truncate(response, Constants.TruncationVerbose));

                    pipeline.Conversation.Add(new ConversationEntry("user", prompt));
                    pipeline.Conversation.Add(new ConversationEntry("assistant", response));

                    if (string.IsNullOrWhiteSpace(response))
                        throw new InvalidOperationException(
                            $"Brain returned empty prompt for {pipeline.GoalId} phase {phase}");

                    return response;
                },
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain craft-prompt failed for {GoalId} (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        pipeline.GoalId, attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                    pipeline.Conversation.Add(new ConversationEntry("system", $"Error on attempt {attempt}: {ex.Message}"));
                },
                ct);

            return craftedPrompt;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain failed to craft prompt for {GoalId} phase {Phase} — using fallback",
                pipeline.GoalId, phase);
            pipeline.Conversation.Add(new ConversationEntry("system", $"CraftPrompt error: {ex.Message}"));
            return $"Work on: {pipeline.Description}";
        }
    }

    internal static string? GetStringArg(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) ? val?.ToString() : null;

    internal static List<string>? GetStringArrayArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null)
            return null;

        if (val is string[] arr)
            return arr.ToList();

        if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
            return je.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

        if (val is IEnumerable<object> list)
            return list.Select(x => x?.ToString() ?? "").ToList();

        return null;
    }

    /// <summary>
    /// Sends a prompt to a Copilot session with exponential backoff retry.
    /// Used for priming, status updates, and other non-tool-call calls.
    /// </summary>
    private async Task<string> SendToSessionAsync(CopilotSession session, string prompt, CancellationToken ct)
    {
        var (text, _) = await Shared.CopilotRetryPolicy.ExecuteAsync(
            () => SendToSessionCoreAsync(session, prompt, ct),
            onRetry: (attempt, delay, ex) =>
            {
                _logger.LogWarning(
                    "Brain Copilot call failed (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                    attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
            },
            ct);
        return text;
    }

    private async Task<(string Text, BrainToolCallResult? ToolCall)> SendToSessionCoreAsync(
        CopilotSession session, string prompt, CancellationToken ct)
    {
        await _brainCallGate.WaitAsync(ct);
        try
        {
            var done = new TaskCompletionSource<(string, BrainToolCallResult?)>();
            var response = "";

            // Clear any previous tool call result before sending
            _lastToolCallResult = null;

            using var subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        response = msg.Data.Content;
                        _logger.LogDebug("{Source} AssistantMessage ({Length} chars)", "Brain-SDK", response.Length);
                        break;
                    case AssistantUsageEvent usage:
                        _logger.LogDebug(
                            "{Source} Usage: model={Model} in={InputTokens} out={OutputTokens} cost={Cost:F4} duration={Duration:F0}ms",
                            "Brain-SDK", usage.Data.Model, usage.Data.InputTokens, usage.Data.OutputTokens,
                            usage.Data.Cost, usage.Data.Duration);
                        FileTracer.WriteUsage(usage.Data, "/app/state/traces-brain.jsonl", "brain");
                        break;
                    case SessionIdleEvent:
                        _logger.LogDebug("{Source} SessionIdle", "Brain-SDK");
                        done.TrySetResult((response, _lastToolCallResult));
                        break;
                    case SessionErrorEvent err:
                        _logger.LogWarning("{Source} SessionError: {Message}", "Brain-SDK", err.Data.Message);
                        done.TrySetException(new InvalidOperationException(err.Data.Message));
                        break;
                    case AssistantTurnStartEvent:
                    case AssistantTurnEndEvent:
                    case AssistantReasoningEvent:
                    case SessionUsageInfoEvent:
                    case PendingMessagesModifiedEvent:
                    case UserMessageEvent:
                    case SubagentSelectedEvent:
                    case SessionInfoEvent:
                        break;
                    case SubagentStartedEvent:
                        _logger.LogDebug("{Source} SubagentStarted", "Brain-SDK");
                        break;
                    case ToolExecutionStartEvent toolStart:
                        _logger.LogDebug("{Source} ToolStart: {ToolName} (id={ToolCallId})",
                            "Brain-SDK", toolStart.Data?.ToolName ?? "unknown", toolStart.Data?.ToolCallId);
                        break;
                    case ToolExecutionCompleteEvent toolEnd:
                        _logger.LogDebug("{Source} ToolComplete: {ToolCallId} success={Success} error={Error}",
                            "Brain-SDK", toolEnd.Data?.ToolCallId, toolEnd.Data?.Success, toolEnd.Data?.Error?.Message);
                        break;
                    default:
                        _logger.LogDebug("{Source} {EventType}", "Brain-SDK", evt.GetType().Name);
                        break;
                }
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(Constants.TaskTimeoutMinutes));
            using var ctReg = cts.Token.Register(() => done.TrySetCanceled());

            await session.SendAsync(new MessageOptions { Prompt = prompt });
            return await done.Task;
        }
        finally
        {
            _brainCallGate.Release();
        }
    }

    /// <summary>
    /// Applies the first-non-null wins rule for model tier: sets <see cref="GoalPipeline.LatestModelTier"/>
    /// only when it has not been explicitly set yet (empty string) and <paramref name="modelTier"/> is non-null.
    /// Normalises the value to <c>"premium"</c> or <c>"standard"</c>.
    /// </summary>
    /// <param name="pipeline">The current goal pipeline whose tier may be updated.</param>
    /// <param name="modelTier">The model tier string returned by the Brain LLM, or <c>null</c> if absent.</param>
    public static void ApplyModelTierIfNotSet(GoalPipeline pipeline, string? modelTier)
    {
        if (pipeline.LatestModelTier != ModelTier.Default || modelTier is null)
            return;

        pipeline.LatestModelTier = ModelTierExtensions.ParseModelTier(modelTier);
    }

    /// <summary>
    /// Builds a concise metrics history summary from the last N iterations for inclusion in prompts.
    /// </summary>
    private string BuildMetricsHistoryContext(int maxIterations = 5)
    {
        if (_metricsTracker is null || _metricsTracker.History.Count == 0)
            return "";

        var history = _metricsTracker.History;
        var recent = history.Skip(Math.Max(0, history.Count - maxIterations)).ToList();
        var sb = new StringBuilder();

        sb.AppendLine("Metrics History (last " + recent.Count + " iterations):");
        foreach (var m in recent)
        {
            var issues = m.Issues.Count > 0 ? $", issues: [{string.Join(", ", m.Issues)}]" : "";
            sb.AppendLine($"  - Iteration {m.Iteration}: {m.Verdict}, " +
                $"{m.PassedTests}/{m.TotalTests} tests, {m.CoveragePercent:F1}% coverage{issues}");
        }

        // Trend analysis across the window
        if (recent.Count >= 2)
        {
            var first = recent[0];
            var last = recent[^1];
            var coverageDelta = last.CoveragePercent - first.CoveragePercent;
            var testDelta = last.TotalTests - first.TotalTests;

            var coverageTrend = coverageDelta switch
            {
                > 1.0 => $"improving (+{coverageDelta:F1}% over {recent.Count} iterations)",
                < -1.0 => $"degrading ({coverageDelta:F1}% over {recent.Count} iterations)",
                _ => "stable",
            };

            sb.AppendLine($"Trend: Coverage {coverageTrend}, " +
                $"test count {(testDelta > 0 ? "growing" : testDelta < 0 ? "shrinking" : "stable")}");
        }

        // Delta vs immediately previous iteration
        if (history.Count >= 2)
        {
            var comparison = _metricsTracker.CompareWithPrevious(history[^1]);
            if (comparison is not null)
                sb.AppendLine($"Delta vs previous: {comparison}");
        }

        return sb.ToString();
    }

    private void EnsureConnected()
    {
        if (_copilotClient is null)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");
    }

    internal static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>Disposes all active Copilot sessions and stops the underlying client.</summary>
    /// <returns>A value task that represents the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        // Dispose all goal sessions
        foreach (var (goalId, session) in _sessions)
        {
            try { await session.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose Copilot session for goal {GoalId}", goalId); }
        }
        _sessions.Clear();

        if (_copilotClient is not null)
        {
            await _copilotClient.StopAsync();
            _copilotClient = null;
        }

        _brainCallGate.Dispose();
    }
}
