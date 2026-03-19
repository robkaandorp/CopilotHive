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
/// Uses the Copilot SDK with a separate session per goal so each goal
/// gets its own native conversation context managed by Copilot.
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
        You are the CopilotHive Orchestrator Brain — a product owner and project manager
        for a distributed multi-agent software development system. You make ALL tactical decisions:

        - You decide which workers to spawn and in what order
        - You craft the prompts sent to workers (coder, reviewer, tester, improver)
        - You interpret worker output to determine verdicts
        - You decide whether to retry, skip, or proceed to the next phase

        CRITICAL RULES:
        - Do NOT run any shell commands, tools, or file operations other than the reporting tools provided.
        - Do NOT explore the filesystem, install packages, or execute code.
        - You are a REASONING agent. Analyze input and report decisions via tool calls.
        - Always call the appropriate tool (report_plan, report_iteration_plan, report_interpretation) to report your decision.
        - Do NOT return raw JSON in your response — use the tools provided.
        """;

    /// <summary>
    /// Permission handler that denies ALL tool/command execution.
    /// The Brain is reasoning-only and must never run shell commands or file operations.
    /// </summary>
    private static readonly PermissionRequestHandler DenyAllPermissions =
        (_, _) => Task.FromResult(new PermissionRequestResult
        {
            Kind = PermissionRequestResultKind.DeniedByRules,
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
        var toolNames = _brainTools.Select(t => t.Name).ToList();

        // Build the orchestrator custom agent with Brain-specific tools
        var orchestratorInstructions = agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
        _orchestratorAgent = new CustomAgentConfig
        {
            Name = "orchestrator",
            DisplayName = "CopilotHive Orchestrator",
            Description = "LLM-powered product owner and project manager — reports decisions via tool calls",
            Prompt = string.IsNullOrWhiteSpace(orchestratorInstructions)
                ? SystemPrompt
                : $"{SystemPrompt}\n\n{orchestratorInstructions}",
            Tools = toolNames,
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
        OnPermissionRequest = DenyAllPermissions,
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
    /// Builds the AIFunction tools that the Brain LLM can call to report structured decisions.
    /// Each tool captures its arguments into <see cref="_lastToolCallResult"/> so the caller
    /// can read the structured result after <see cref="SessionIdleEvent"/> fires.
    /// </summary>
    private List<AIFunction> BuildBrainTools()
    {
        var validActions = new HashSet<string>(
            BrainActions.PlanningActions.Concat(BrainActions.NextStepActions).Distinct());
        var validPhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "coding", "testing", "docwriting", "review", "improve", "merging" };
        var validModelTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "standard", "premium" };

        return
        [
            AIFunctionFactory.Create(
                ([Description("Action: spawn_coder, spawn_tester, spawn_reviewer, spawn_doc_writer, spawn_improver, request_changes, retry, merge, done, skip")] string action,
                 [Description("The prompt to send to the worker, if spawning one")] string prompt,
                 [Description("Why you chose this action")] string reason,
                 [Description("Model tier: standard or premium")] string model_tier) =>
                {
                    var isSpawn = action?.StartsWith("spawn_", StringComparison.Ordinal) == true;
                    var error = Shared.ToolValidation.Check(
                        (!string.IsNullOrEmpty(action), "action is required"),
                        (action is not null && validActions.Contains(action),
                            $"action must be one of: {string.Join(", ", validActions)}"),
                        (!isSpawn || !string.IsNullOrWhiteSpace(prompt),
                            "prompt is required when spawning a worker"),
                        (!string.IsNullOrEmpty(reason), "reason is required"),
                        (string.IsNullOrEmpty(model_tier) || validModelTiers.Contains(model_tier),
                            "model_tier must be 'standard' or 'premium'"));
                    if (error is not null) return error;

                    _lastToolCallResult = new BrainToolCallResult("report_plan", new Dictionary<string, object?>
                    {
                        ["action"] = action, ["prompt"] = prompt, ["reason"] = reason, ["model_tier"] = model_tier,
                    });
                    return "Decision recorded.";
                },
                "report_plan",
                "Report your orchestration decision — which worker to spawn, what prompt to send, and why."),

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

            AIFunctionFactory.Create(
                ([Description("Verdict: PASS or FAIL")] string verdict,
                 [Description("Review verdict: APPROVE or REQUEST_CHANGES (for review phase only, empty string otherwise)")] string review_verdict,
                 [Description("List of issues found")] string[] issues,
                 [Description("Why this verdict — what went right or wrong")] string reason,
                 [Description("Model tier: standard or premium")] string model_tier) =>
                {
                    var error = Shared.ToolValidation.Check(
                        (!string.IsNullOrEmpty(verdict), "verdict is required"),
                        (verdict is "PASS" or "FAIL", "verdict must be exactly 'PASS' or 'FAIL'"),
                        (string.IsNullOrEmpty(review_verdict) || review_verdict is "APPROVE" or "REQUEST_CHANGES",
                            "review_verdict must be empty, 'APPROVE', or 'REQUEST_CHANGES'"),
                        (!string.IsNullOrEmpty(reason), "reason is required"),
                        (string.IsNullOrEmpty(model_tier) || validModelTiers.Contains(model_tier),
                            "model_tier must be 'standard' or 'premium'"));
                    if (error is not null) return error;

                    _lastToolCallResult = new BrainToolCallResult("report_interpretation", new Dictionary<string, object?>
                    {
                        ["verdict"] = verdict, ["review_verdict"] = review_verdict, ["issues"] = issues, ["reason"] = reason, ["model_tier"] = model_tier,
                    });
                    return "Interpretation recorded.";
                },
                "report_interpretation",
                "Report your interpretation of worker output — verdict, issues, reason, and review verdict if applicable."),
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
    /// Asks the Brain to plan the best approach for executing the given goal pipeline.
    /// </summary>
    /// <param name="pipeline">Current goal pipeline state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> containing the recommended first action.</returns>
    public async Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default)
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

        var prompt = $$"""
            Plan the workflow for this goal:
            {{pipeline.Description}}

            {{metricsContext}}
            {{historyContext}}

            Decide which phase to start with. Consider:
            - Is this a documentation-only change? (coder for edits, skip testing, docwriter updates docs)
            - Is this a code change? (needs coder → tester → docwriter → reviewer → merge)
            - Is there context from previous iterations?
            IMPORTANT: Always include the docwriting phase for any change — the doc-writer updates
            the CHANGELOG, README, and XML doc comments. Even internal services need changelog entries.

            Rules for the prompt you craft:
            - The branch is already checked out by the infrastructure — do NOT mention branch names
            - NEVER include git checkout, git branch, git switch, or git push commands — the infrastructure handles all branching
            - NEVER include framework-specific build/test commands (dotnet build, npm test, etc.) — tell workers to use /build and /test skills

            Call the `report_plan` tool with your decision.
            Valid actions: {{BrainActions.FormatForPrompt(BrainActions.PlanningActions)}}
            Model tier should be 'standard' unless a previous attempt FAILED and needs a stronger model.
            """;

        var decision = await AskAsync(pipeline, prompt, ct);
        ApplyModelTierIfNotSet(pipeline, decision?.ModelTier);
        return decision ?? new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnCoder,
            Reason = "Default: starting with coding phase",
        };
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

    private static IterationPlan MapIterationPlan(IterationPlanDto dto)
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
    private sealed record IterationPlanDto
    {
        /// <summary>Ordered list of phase names the Brain wants to execute this iteration.</summary>
        public List<string> Phases { get; init; } = [];
        /// <summary>Per-phase instructions keyed by phase name, providing context for each phase.</summary>
        public Dictionary<string, string>? PhaseInstructions { get; init; }
        /// <summary>The Brain's reasoning for choosing this iteration plan.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Asks the Brain to craft a prompt for the specified worker role.
    /// </summary>
    /// <param name="pipeline">Current goal pipeline state.</param>
    /// <param name="role">Role of the worker the prompt will be sent to.</param>
    /// <param name="additionalContext">Optional extra context to include in the prompt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The crafted prompt string.</returns>
    public async Task<string> CraftPromptAsync(
        GoalPipeline pipeline, WorkerRole role, string? additionalContext, CancellationToken ct = default)
    {
        var historyContext = BuildMetricsHistoryContext(3);

        var roleName = role.ToRoleName();
        var roleInstruction = role switch
        {
            WorkerRole.Coder => """
                - For coders: Tell them to start implementing immediately — read the relevant files, make code changes, use /build skill, use /test skill, and commit with `git add -A && git commit`. NEVER include git checkout, git branch, or git push commands. NEVER include dotnet/npm/cargo commands — only reference /build and /test skills.
                """,
            WorkerRole.Reviewer => """
                - For reviewers: tell them to run `git diff origin/<base-branch>...HEAD` to review ALL changes. The base branch and feature branch are provided in the workspace context. The `origin/` prefix is required because the clone only has remote tracking refs. Produce a REVIEW_REPORT.
                """,
            WorkerRole.Tester => """
                - For testers: tell them to build, run the test skill, write integration tests, produce a TEST_REPORT
                """,
            WorkerRole.DocWriter => """
                - For docwriters: tell them to update README, CHANGELOG, and XML doc comments based on the code changes on the branch, build to verify, and commit. Produce a DOC_REPORT.
                """,
            WorkerRole.Improver => """
                - For improvers: tell them to analyze iteration results and update *.agents.md files directly using file tools. Do NOT tell them to run git commands — the infrastructure commits and pushes automatically.
                """,
            _ => throw new InvalidOperationException($"Unhandled role in CraftPromptAsync: '{roleName}'"),
        };

        var prompt = $$"""
            Craft a prompt for the {{roleName}} worker.

            Goal: {{pipeline.Description}}
            Iteration: {{pipeline.Iteration}}
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

            Call the `report_plan` tool with action=spawn_{{roleName}} and the prompt you crafted.
            Model tier should be 'standard' unless a previous attempt FAILED and needs a stronger model.
            """;

        var decision = await AskAsync(pipeline, prompt, ct);
        ApplyModelTierIfNotSet(pipeline, decision?.ModelTier);
        return decision?.Prompt
            ?? throw new InvalidOperationException(
                $"Brain failed to craft prompt for '{roleName}' on goal '{pipeline.GoalId}'. " +
                "Decision was null or missing the 'prompt' field.");
    }

    /// <summary>
    /// Asks the Brain to interpret the output of a worker and return a verdict.
    /// </summary>
    /// <param name="pipeline">Current goal pipeline state.</param>
    /// <param name="phase">The goal phase that produced the output.</param>
    /// <param name="workerOutput">Raw text output from the worker.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> with the extracted verdict and metrics.</returns>
    public async Task<OrchestratorDecision> InterpretOutputAsync(
        GoalPipeline pipeline, GoalPhase phase, string workerOutput, CancellationToken ct = default)
    {
        var phaseName = phase.ToString();

        var modeInstruction = """
              IMPORTANT: You are in INTERPRETATION mode, NOT prompt-crafting mode.
              Do NOT suggest spawning any worker.
              Your ONLY job is to analyze the output below and return a verdict.

              """;

        var reviewInstruction = phase == GoalPhase.Review
            ? """

              For reviewers: set review_verdict to 'APPROVE' or 'REQUEST_CHANGES'.

              """
            : "";

        var prompt = $$"""
            {{modeInstruction}}Interpret this {{phaseName}}'s output.
            {{reviewInstruction}}
            === {{phaseName.ToUpperInvariant()}} OUTPUT (truncated) ===
            {{Truncate(workerOutput, Constants.TruncationVeryLong)}}
            === END OUTPUT ===

            Analyze the output carefully:
            - Did the worker succeed or fail at its task?
            - For reviewers: did they approve or request changes? What issues?
            - For docwriters: did they update CHANGELOG, README, or XML doc comments? Did they commit?
            - For coders: did they make code changes and commit? Check for git commit evidence.
            - Extract any specific issues mentioned

            Call the `report_interpretation` tool with the verdict (PASS or FAIL), any issues,
            a reason explaining what went right or wrong,
            and review_verdict (APPROVE or REQUEST_CHANGES) if this is a review phase.
            Model tier should be 'standard' unless a retry with a stronger model is needed.
            """;

        var result = await AskAsync(pipeline, prompt, ct)
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Verdict = "UNKNOWN",
                Reason = "Failed to interpret output",
            };

        ApplyModelTierIfNotSet(pipeline, result.ModelTier);
        return result;
    }

    /// <summary>
    /// Asks the Brain what the next step should be given the current pipeline phase and context.
    /// </summary>
    /// <param name="pipeline">Current goal pipeline state.</param>
    /// <param name="context">Textual context describing the current situation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> with the recommended next action.</returns>
    public async Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline, string context, CancellationToken ct = default)
    {
        var prompt = $$"""
            Current goal: {{pipeline.Description}}
            Current phase: {{pipeline.Phase}}
            {{context}}

            What should we do next?

            Call the `report_plan` tool with your decision.
            Valid actions: {{BrainActions.FormatForPrompt(BrainActions.NextStepActions)}}
            Model tier should be 'standard' unless a retry with a stronger model is needed.
            """;

        var decision = await AskAsync(pipeline, prompt, ct);
        ApplyModelTierIfNotSet(pipeline, decision?.ModelTier);
        return decision
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Reason = "Failed to decide — defaulting to done",
            };
    }

    /// <summary>
    /// Informs the Brain of something that happened so it can maintain conversation context.
    /// </summary>
    /// <param name="pipeline">Current goal pipeline state.</param>
    /// <param name="information">Human-readable status update to pass to the Brain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(pipeline, ct);
        await SendToSessionAsync(session,
            $"[STATUS UPDATE] {information}\n\nAcknowledge briefly.", ct);
    }

    private async Task<OrchestratorDecision?> AskAsync(GoalPipeline pipeline, string prompt, CancellationToken ct)
    {
        try
        {
            var session = await GetOrCreateSessionAsync(pipeline, ct);

            return await Shared.CopilotRetryPolicy.ExecuteAsync(
                async () =>
                {
                    _logger.LogDebug("Brain prompt for {GoalId}:\n{Prompt}", pipeline.GoalId, Truncate(prompt, Constants.TruncationVerbose));

                    var (response, toolCall) = await SendToSessionCoreAsync(session, prompt, ct);

                    _logger.LogDebug("Brain response for {GoalId}:\n{Response}", pipeline.GoalId, Truncate(response, Constants.TruncationVerbose));

                    pipeline.Conversation.Add(new ConversationEntry("user", prompt));
                    pipeline.Conversation.Add(new ConversationEntry("assistant", response));

                    if (toolCall is null)
                        throw new InvalidOperationException(
                            $"Brain did not use a tool call for {pipeline.GoalId}. Response: {Truncate(response, Constants.TruncationShort)}");

                    _logger.LogDebug("Brain used tool call '{Tool}' for {GoalId}", toolCall.ToolName, pipeline.GoalId);
                    return BuildDecisionFromToolCall(toolCall);
                },
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain query failed for {GoalId} (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        pipeline.GoalId, attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                    pipeline.Conversation.Add(new ConversationEntry("system", $"Error on attempt {attempt}: {ex.Message}"));
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain query failed for goal {GoalId} after all retries", pipeline.GoalId);
            pipeline.Conversation.Add(new ConversationEntry("system", $"Final error: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// Builds an <see cref="OrchestratorDecision"/> from a <c>report_plan</c> or
    /// <c>report_interpretation</c> tool call result.
    /// </summary>
    internal static OrchestratorDecision BuildDecisionFromToolCall(BrainToolCallResult toolCall)
    {
        var args = toolCall.Arguments;

        return toolCall.ToolName switch
        {
            "report_plan" => new OrchestratorDecision
            {
                Action = ParseAction(GetStringArg(args, "action")),
                Prompt = GetStringArg(args, "prompt"),
                Reason = GetStringArg(args, "reason"),
                ModelTier = GetStringArg(args, "model_tier"),
            },
            "report_interpretation" => new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Verdict = GetStringArg(args, "verdict"),
                ReviewVerdict = GetStringArg(args, "review_verdict") is { Length: > 0 } rv ? rv : null,
                Issues = GetStringArrayArg(args, "issues"),
                Reason = GetStringArg(args, "reason"),
                ModelTier = GetStringArg(args, "model_tier"),
            },
            _ => throw new InvalidOperationException($"Unexpected Brain tool call: '{toolCall.ToolName}'"),
        };
    }

    private static OrchestratorActionType ParseAction(string? action) =>
        action switch
        {
            "spawn_coder" => OrchestratorActionType.SpawnCoder,
            "spawn_tester" => OrchestratorActionType.SpawnTester,
            "spawn_reviewer" => OrchestratorActionType.SpawnReviewer,
            "spawn_doc_writer" => OrchestratorActionType.SpawnDocWriter,
            "spawn_improver" => OrchestratorActionType.SpawnImprover,
            "request_changes" => OrchestratorActionType.RequestChanges,
            "retry" => OrchestratorActionType.Retry,
            "merge" => OrchestratorActionType.Merge,
            "done" => OrchestratorActionType.Done,
            "skip" => OrchestratorActionType.Skip,
            _ => throw new InvalidOperationException($"Unknown Brain action: '{action}'"),
        };

    private static string? GetStringArg(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) ? val?.ToString() : null;

    private static List<string>? GetStringArrayArg(Dictionary<string, object?> args, string key)
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
    /// Used for priming, status updates, and other non-AskAsync calls.
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
                        Console.WriteLine($"[Brain-SDK] AssistantMessage ({response.Length} chars)");
                        Console.Out.Flush();
                        break;
                    case AssistantUsageEvent usage:
                        Console.WriteLine($"[Brain-SDK] Usage: model={usage.Data.Model} in={usage.Data.InputTokens} out={usage.Data.OutputTokens} cost={usage.Data.Cost:F4} duration={usage.Data.Duration:F0}ms");
                        Console.Out.Flush();
                        FileTracer.WriteUsage(usage.Data, "/app/state/traces-brain.jsonl", "brain");
                        break;
                    case SessionIdleEvent:
                        Console.WriteLine("[Brain-SDK] SessionIdle");
                        Console.Out.Flush();
                        done.TrySetResult((response, _lastToolCallResult));
                        break;
                    case SessionErrorEvent err:
                        Console.WriteLine($"[Brain-SDK] SessionError: {err.Data.Message}");
                        Console.Out.Flush();
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
                    case ToolExecutionStartEvent:
                    case ToolExecutionCompleteEvent:
                        break;
                    default:
                        Console.WriteLine($"[Brain-SDK] {evt.GetType().Name}");
                        Console.Out.Flush();
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
