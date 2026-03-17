using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CopilotHive.Metrics;
using CopilotHive.Services;
using CopilotHive.Telemetry;
using CopilotHive.Workers;
using GitHub.Copilot.SDK;

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

    private const string SystemPrompt = """
        You are the CopilotHive Orchestrator Brain — a product owner and project manager
        for a distributed multi-agent software development system. You make ALL tactical decisions:

        - You decide which workers to spawn and in what order
        - You craft the prompts sent to workers (coder, reviewer, tester, improver)
        - You interpret worker output to determine verdicts and extract metrics
        - You decide whether to retry, skip, or proceed to the next phase

        CRITICAL RULES:
        - Do NOT run any shell commands, tools, or file operations.
        - Do NOT explore the filesystem, install packages, or execute code.
        - You are a REASONING-ONLY agent. Analyze text input and produce JSON responses.
        - Always respond with valid JSON matching the requested schema.
        - Do NOT wrap the JSON in markdown code fences. Return ONLY the JSON object.
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

        // Build the orchestrator custom agent with no tools (reasoning-only)
        var orchestratorInstructions = agentsManager?.GetAgentsMd("orchestrator") ?? "";
        _orchestratorAgent = new CustomAgentConfig
        {
            Name = "orchestrator",
            DisplayName = "CopilotHive Orchestrator",
            Description = "LLM-powered product owner and project manager — reasoning only, no tools",
            Prompt = string.IsNullOrWhiteSpace(orchestratorInstructions)
                ? SystemPrompt
                : $"{SystemPrompt}\n\n{orchestratorInstructions}",
            Tools = [], // no tools — pure reasoning agent
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
        AvailableTools = [], // no tools for the orchestrator — pure reasoning
        InfiniteSessions = new InfiniteSessionConfig
        {
            Enabled = true,
            BackgroundCompactionThreshold = 0.80,
            BufferExhaustionThreshold = 0.95,
        },
    };

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

            Respond with JSON:
            {
              "action": "spawn_coder",
              "prompt": "<the prompt you want to send to the first worker>",
              "reason": "<why you chose this plan>",
              "model_tier": "standard or premium — default is standard; only use premium after a FAILED attempt"
            }

            Valid actions: {{BrainActions.FormatForPrompt(BrainActions.PlanningActions)}}
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

            Respond with JSON:
            {
              "phases": ["coding", "testing", "docwriting", "review", "merging"],
              "phase_instructions": {
                "coding": "specific instructions for the coder...",
                "review": "focus review on..."
              },
              "reason": "why this plan"
            }
            """;

        try
        {
            var session = await GetOrCreateSessionAsync(pipeline, ct);
            var response = await SendToSessionAsync(session, prompt, ct);

            pipeline.Conversation.Add(new ConversationEntry("user", prompt));
            pipeline.Conversation.Add(new ConversationEntry("assistant", response));

            var dto = ProtocolJson.ParseFromLlmResponse<IterationPlanDto>(response);
            if (dto?.Phases is { Count: > 0 })
            {
                var plan = MapIterationPlan(dto);
                if (plan.Phases.Count > 0)
                {
                    _logger.LogInformation(
                        "Brain planned iteration {Iteration} for goal {GoalId}: [{Phases}] — {Reason}",
                        pipeline.Iteration, pipeline.GoalId,
                        string.Join(", ", plan.Phases), plan.Reason ?? "no reason");
                    return plan;
                }
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
        var branch = pipeline.CoderBranch
            ?? throw new InvalidOperationException("CoderBranch must be set before crafting prompts");

        var roleName = role.ToRoleName();
        var roleInstruction = role switch
        {
            WorkerRole.Coder => $"""
                - For coders: Tell them to start implementing immediately — read the relevant files, make code changes, build, test, and commit. Do NOT include git branch or git push commands.
                """,
            WorkerRole.Reviewer => $"""
                - For reviewers: tell them to review the diff on branch "{branch}" against the base branch, produce a REVIEW_REPORT
                """,
            WorkerRole.Tester => """
                - For testers: tell them to build, run the test skill, write integration tests, produce a TEST_REPORT
                """,
            WorkerRole.DocWriter => """
                - For docwriters: tell them to update README, CHANGELOG, and XML doc comments based on the code changes on the branch, build to verify, and commit. Produce a DOC_REPORT.
                """,
            WorkerRole.Improver => """
                - For improvers: tell them to analyze iteration results and update *.agents.md files. Commit changes.
                """,
            _ => throw new InvalidOperationException($"Unhandled role in CraftPromptAsync: '{roleName}'"),
        };

        var prompt = $$"""
            Craft a prompt for the {{roleName}} worker.

            Goal: {{pipeline.Description}}
            Iteration: {{pipeline.Iteration}}
            Branch: {{branch}}
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}
            {{(historyContext.Length > 0 ? $"\n{historyContext}" : "")}}

            The worker has access to project skills (e.g. /build, /test) that describe how to build and test this project.
            Tell the worker to use those skills instead of hardcoding framework-specific commands.

            Rules for the prompt you craft:
            - CRITICAL: The branch name is EXACTLY "{{branch}}" — do NOT invent or change it
            - The worker infrastructure handles branch creation, checkout, and pushing — do NOT tell workers to create branches or push
            {{roleInstruction}}
            - Include any context from previous phases that would help the worker

            Respond with JSON:
            {
              "action": "spawn_{{roleName}}",
              "prompt": "<the complete prompt to send to the worker>",
              "reason": "<why you crafted the prompt this way>",
              "model_tier": "standard or premium — default is standard; only use premium after a FAILED attempt"
            }

            You may specify a model tier in your response. Add a 'model_tier' field with value 'standard' or 'premium'. Default is 'standard'. Only use 'premium' when: (1) a previous attempt FAILED and needs a stronger model, or (2) the task requires fixing complex bugs found in review. Do NOT use premium for first attempts or routine tasks — standard models are capable enough.
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
        var schema = phase switch
        {
            GoalPhase.Coding => """
                {
                  "verdict": "PASS or FAIL",
                  "issues": ["<issue1>", "<issue2>"],
                  "model_tier": "standard or premium — use premium for complex, high-stakes, or retry tasks"
                }
                """,
            GoalPhase.Testing => """
                {
                  "verdict": "PASS or FAIL",
                  "test_metrics": {
                    "build_success": true/false,
                    "total_tests": <number>,
                    "passed_tests": <number>,
                    "failed_tests": <number>,
                    "coverage_percent": <number>
                  },
                  "issues": ["<issue1>", "<issue2>"],
                  "model_tier": "standard or premium — use premium for complex, high-stakes, or retry tasks"
                }
                """,
            GoalPhase.Review => """
                {
                  "review_verdict": "APPROVE or REQUEST_CHANGES",
                  "issues": ["<issue1>", "<issue2>"],
                  "model_tier": "standard or premium — use premium for complex, high-stakes, or retry tasks"
                }
                """,
            GoalPhase.DocWriting => """
                {
                  "verdict": "PASS or FAIL",
                  "issues": ["<issue1>", "<issue2>"],
                  "model_tier": "standard or premium — use premium for complex, high-stakes, or retry tasks"
                }
                """,
            GoalPhase.Improve => """
                {
                  "verdict": "PASS or FAIL",
                  "issues": ["<issue1>", "<issue2>"],
                  "model_tier": "standard or premium — use premium for complex, high-stakes, or retry tasks"
                }
                """,
            _ => throw new InvalidOperationException($"Unhandled phase in InterpretOutputAsync: '{phase}'"),
        };

        var phaseName = phase.ToString();

        var modeInstruction = """
              IMPORTANT: You are in INTERPRETATION mode, NOT prompt-crafting mode.
              Do NOT return an "action" or "prompt" field. Do NOT suggest spawning any worker.
              Your ONLY job is to analyze the output below and return a verdict.

              """;

        var testerInstruction = phase == GoalPhase.Testing
            ? """

              CRITICAL TESTER INSTRUCTIONS:
              For tester output: You MUST extract test counts from the output. Look for patterns like
              'Passed: N, Failed: N, Total: N' or 'Passed! - Failed: 0, Passed: 322, Skipped: 0, Total: 322'
              or 'X passed, Y failed' or 'Tests run: N'. The `total_tests` and `passed_tests` fields inside
              `test_metrics` are REQUIRED when the worker phase is Testing — do NOT leave them null or zero
              if the output contains numeric results.

              """
            : "";

        var prompt = $$"""
            {{modeInstruction}}Interpret this {{phaseName}}'s output and extract structured data.
            {{testerInstruction}}
            === {{phaseName.ToUpperInvariant()}} OUTPUT (truncated) ===
            {{Truncate(workerOutput, Constants.TruncationVeryLong)}}
            === END OUTPUT ===

            Analyze the output carefully:
            - Did the worker succeed or fail at its task?
            - For testers: did all tests pass? What are the numbers? You MUST populate the test_metrics object with actual numbers from the output — look for test counts in tables, summaries, or test runner output.
            - For reviewers: did they approve or request changes? What issues?
            - For docwriters: did they update CHANGELOG, README, or XML doc comments? Did they produce a DOC_REPORT? Did they commit?
            - For coders: did they make code changes and commit? Check for git commit evidence.
            - Extract any specific issues mentioned

            Respond ONLY with this JSON (no other fields):
            {{schema}}
            """;

        var result = await AskAsync(pipeline, prompt, ct)
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Verdict = "UNKNOWN",
                Reason = "Failed to interpret output",
            };

        ApplyModelTierIfNotSet(pipeline, result.ModelTier);
        return ApplyTestMetricsFallback(result, phase, workerOutput, _logger);
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

            Respond with JSON:
            {
              "action": "<next action: {{BrainActions.FormatForPrompt(BrainActions.NextStepActions)}}>",
              "prompt": "<prompt for the worker, if spawning one>",
              "reason": "<why this is the right next step>",
              "model_tier": "standard or premium — use premium for complex, high-stakes, or retry tasks"
            }
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

            _logger.LogDebug("Brain prompt for {GoalId}:\n{Prompt}", pipeline.GoalId, Truncate(prompt, Constants.TruncationVerbose));

            var response = await SendToSessionAsync(session, prompt, ct);

            _logger.LogDebug("Brain response for {GoalId}:\n{Response}", pipeline.GoalId, Truncate(response, Constants.TruncationVerbose));

            // Keep audit log in the pipeline for debugging
            pipeline.Conversation.Add(new ConversationEntry("user", prompt));
            pipeline.Conversation.Add(new ConversationEntry("assistant", response));

            var parsed = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(response);
            if (parsed is null)
                _logger.LogWarning("Failed to parse Brain JSON response: {Response}", Truncate(response, Constants.TruncationShort));

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain LLM query failed for goal {GoalId}", pipeline.GoalId);
            pipeline.Conversation.Add(new ConversationEntry("system", $"Error: {ex.Message}"));
            return null;
        }
    }

    private static async Task<string> SendToSessionAsync(CopilotSession session, string prompt, CancellationToken ct)
    {
        var done = new TaskCompletionSource<string>();
        var response = "";

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
                    done.TrySetResult(response);
                    break;
                case SessionErrorEvent err:
                    Console.WriteLine($"[Brain-SDK] SessionError: {err.Data.Message}");
                    Console.Out.Flush();
                    done.TrySetException(new InvalidOperationException(err.Data.Message));
                    break;
                // Muted — Brain doesn't stream, these are just noise
                case AssistantTurnStartEvent:
                case AssistantTurnEndEvent:
                case AssistantReasoningEvent:
                case SessionUsageInfoEvent:
                case PendingMessagesModifiedEvent:
                case UserMessageEvent:
                case SubagentSelectedEvent:
                case SessionInfoEvent:
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

    /// <summary>
    /// Applies the first-non-null wins rule for model tier: sets <see cref="GoalPipeline.LatestModelTier"/>
    /// only when it has not been explicitly set yet (empty string) and <paramref name="modelTier"/> is non-null.
    /// Normalises the value to <c>"premium"</c> or <c>"standard"</c>.
    /// </summary>
    /// <param name="pipeline">The current goal pipeline whose tier may be updated.</param>
    /// <param name="modelTier">The model tier string returned by the Brain LLM, or <c>null</c> if absent.</param>
    public static void ApplyModelTierIfNotSet(GoalPipeline pipeline, string? modelTier)
    {
        if (!string.IsNullOrEmpty(pipeline.LatestModelTier) || modelTier is null)
            return;

        pipeline.LatestModelTier = modelTier.ToLowerInvariant() == "premium" ? "premium" : "standard";
    }

    /// <summary>
    /// Applies fallback test metrics parsing when the Brain failed to extract metrics for tester output.
    /// If the worker role is "tester" and the Brain returned null or zero test counts, parses the raw
    /// output with <see cref="FallbackParseTestMetrics"/> and merges the result into the decision.
    /// Brain values take priority unless they are null or zero, in which case fallback values win.
    /// </summary>
    internal static OrchestratorDecision ApplyTestMetricsFallback(
        OrchestratorDecision decision, GoalPhase phase, string rawOutput, ILogger logger)
    {
        if (phase != GoalPhase.Testing)
            return decision;

        if ((decision.TestMetrics?.TotalTests ?? 0) > 0 &&
            (decision.TestMetrics?.PassedTests ?? 0) > 0)
            return decision; // Brain extracted valid metrics — nothing to do

        var fallback = FallbackParseTestMetrics(rawOutput);
        if (fallback is null)
            return decision; // Fallback found nothing — nothing to do

        logger.LogWarning(
            "Brain failed to extract test_metrics for tester phase, running fallback parser.");

        // Merge: Brain values win when non-null and non-zero; fallback fills the gaps
        var existing = decision.TestMetrics;
        var merged = new ExtractedTestMetrics
        {
            PassedTests  = (existing?.PassedTests  ?? 0) > 0 ? existing!.PassedTests  : fallback.PassedTests,
            TotalTests   = (existing?.TotalTests   ?? 0) > 0 ? existing!.TotalTests   : fallback.TotalTests,
            FailedTests  = (existing?.FailedTests  ?? 0) > 0 ? existing!.FailedTests  : fallback.FailedTests,
            SkippedTests = (existing?.SkippedTests ?? 0) > 0 ? existing!.SkippedTests : fallback.SkippedTests,
            BuildSuccess  = existing?.BuildSuccess  ?? fallback.BuildSuccess,
            CoveragePercent = existing?.CoveragePercent ?? fallback.CoveragePercent,
        };

        return decision with { TestMetrics = merged };
    }

    /// <summary>
    /// Parses raw worker output for common dotnet test result patterns and returns
    /// a populated <see cref="ExtractedTestMetrics"/> object.
    /// Returns <c>null</c> if no recognisable test counts are found.
    /// Also called by <see cref="GoalDispatcher.FallbackParseTestMetrics"/> to keep the logic DRY.
    /// </summary>
    internal static ExtractedTestMetrics? FallbackParseTestMetrics(string output)
    {
        if (string.IsNullOrEmpty(output))
            return null;

        int? total = null, passed = null, failed = null, skipped = null;
        bool? buildSuccess = null;

        // xUnit runner summary: "Passed!  - Failed:     0, Passed:   322, Skipped:     0, Total:   322"
        var xunitMatch = Regex.Match(output,
            @"Passed!\s*-\s*Failed:\s+(\d+),\s*Passed:\s+(\d+),\s*Skipped:\s+(\d+),\s*Total:\s+(\d+)",
            RegexOptions.IgnoreCase);
        if (xunitMatch.Success)
        {
            failed  = int.Parse(xunitMatch.Groups[1].Value);
            passed  = int.Parse(xunitMatch.Groups[2].Value);
            skipped = int.Parse(xunitMatch.Groups[3].Value);
            total   = int.Parse(xunitMatch.Groups[4].Value);
        }

        // dotnet test summary (full): "Passed! - Failed: 0, Passed: 268, Skipped: 0, Total: 268"
        Match? dotnetMatch = null;
        if (!xunitMatch.Success)
        {
            dotnetMatch = Regex.Match(
                output,
                @"Failed:\s+(\d+),\s*Passed:\s+(\d+),\s*Skipped:\s+(\d+),\s*Total:\s+(\d+)",
                RegexOptions.IgnoreCase);
        }

        if (dotnetMatch is { Success: true })
        {
            failed  = int.Parse(dotnetMatch.Groups[1].Value);
            passed  = int.Parse(dotnetMatch.Groups[2].Value);
            skipped = int.Parse(dotnetMatch.Groups[3].Value);
            total   = int.Parse(dotnetMatch.Groups[4].Value);
        }
        else if (!xunitMatch.Success)
        {
            // Short summary: "Passed: 8, Failed: 0, Total: 8" (comma-separated, no Skipped field)
            // Use [ \t] (not \s) to avoid spanning multiple lines
            var shortMatch = Regex.Match(
                output,
                @"Passed:[ \t]*(\d+)[ \t]*,[ \t]*Failed:[ \t]*(\d+)[ \t]*,[ \t]*Total:[ \t]*(\d+)",
                RegexOptions.IgnoreCase);

            if (shortMatch.Success)
            {
                passed  = int.Parse(shortMatch.Groups[1].Value);
                failed  = int.Parse(shortMatch.Groups[2].Value);
                total   = int.Parse(shortMatch.Groups[3].Value);
            }
        }

        if (total is null)
        {
            // Markdown table rows and key-value lines
            int mdTotal = 0, mdPassed = 0, mdFailed = 0;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();

                var mdMatch = Regex.Match(trimmed, @"^\|\s*(\w+)\s*\|\s*(\d+)\s*\|");
                if (mdMatch.Success)
                {
                    var key = mdMatch.Groups[1].Value;
                    var val = int.Parse(mdMatch.Groups[2].Value);
                    if (key.Equals("Total",  StringComparison.OrdinalIgnoreCase)) mdTotal  = Math.Max(mdTotal,  val);
                    else if (key.Equals("Passed",  StringComparison.OrdinalIgnoreCase)) mdPassed = Math.Max(mdPassed, val);
                    else if (key.Equals("Failed",  StringComparison.OrdinalIgnoreCase)) mdFailed = Math.Max(mdFailed, val);
                    else if (key.Equals("Errors",  StringComparison.OrdinalIgnoreCase)) buildSuccess = val == 0;
                    continue;
                }

                // Key-value line format: "total_tests: 273"
                if (trimmed.StartsWith("unit_tests_total:",  StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("total_tests:",    StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("total:",          StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(trimmed[(trimmed.IndexOf(':') + 1)..].Trim(), out var t))
                        mdTotal = Math.Max(mdTotal, t);
                }
                else if (trimmed.StartsWith("unit_tests_passed:", StringComparison.OrdinalIgnoreCase)
                         || trimmed.StartsWith("passed_tests:",    StringComparison.OrdinalIgnoreCase)
                         || trimmed.StartsWith("passed:",          StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(trimmed[(trimmed.IndexOf(':') + 1)..].Trim(), out var p))
                        mdPassed = Math.Max(mdPassed, p);
                }
                else if (trimmed.StartsWith("failed_tests:", StringComparison.OrdinalIgnoreCase)
                         || trimmed.StartsWith("failed:",     StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(trimmed[(trimmed.IndexOf(':') + 1)..].Trim(), out var f))
                        mdFailed = Math.Max(mdFailed, f);
                }
            }

            if (mdTotal > 0)  total  = mdTotal;
            if (mdPassed > 0) passed = mdPassed;
            if (mdFailed > 0) failed = mdFailed;
        }

        // Parse build success from common patterns
        if (!buildSuccess.HasValue)
        {
            buildSuccess =
                output.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(output, @"\|\s*Errors\s*\|\s*0\s*\|")
                || Regex.IsMatch(output, @"✅\s*\*{0,2}Succeeded\*{0,2}", RegexOptions.IgnoreCase)
                || Regex.IsMatch(output, @"Build(?:\s+(?:Status|Result))?\s*:\s*(?:✅\s*)?PASS", RegexOptions.IgnoreCase)
                || Regex.IsMatch(output, @"\b0\s+error(?:s|\(s\))", RegexOptions.IgnoreCase)
                ? true
                : (bool?)null;
        }

        if (total is null && passed is null && failed is null && buildSuccess is null)
            return null;

        // Parse coverage percent from Coverlet text table TOTAL row or key-value lines.
        double? coveragePercent = null;

        // Pattern A — Coverlet text table TOTAL row: | TOTAL | 73.5% | 61.2% | 80.1% |
        var coverletTotalMatch = Regex.Match(
            output,
            @"\|\s*TOTAL\s*\|\s*(\d+\.?\d*)%",
            RegexOptions.IgnoreCase);
        if (coverletTotalMatch.Success
            && double.TryParse(coverletTotalMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var coverletPct))
        {
            coveragePercent = coverletPct;
        }

        // Pattern B — Key-value lines: "coverage_percent: 73.5" or "coverage: 73.5%"
        if (coveragePercent is null)
        {
            var kvMatch = Regex.Match(
                output,
                @"coverage[_\s]*percent\s*[:\s]+(\d+\.?\d*)",
                RegexOptions.IgnoreCase);
            if (!kvMatch.Success)
            {
                kvMatch = Regex.Match(
                    output,
                    @"coverage\s*:\s*(\d+\.?\d*)%",
                    RegexOptions.IgnoreCase);
            }

            if (kvMatch.Success
                && double.TryParse(kvMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var kvPct))
            {
                coveragePercent = kvPct;
            }
        }

        return new ExtractedTestMetrics
        {
            TotalTests      = total,
            PassedTests     = passed,
            FailedTests     = failed,
            SkippedTests    = skipped,
            BuildSuccess    = buildSuccess,
            CoveragePercent = coveragePercent,
        };
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
    }
}
