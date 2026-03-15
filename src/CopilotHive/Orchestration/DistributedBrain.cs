using System.Collections.Concurrent;
using System.Text;
using CopilotHive.Copilot;
using CopilotHive.Metrics;
using CopilotHive.Services;
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
        };
    }

    /// <summary>
    /// Connects to the Copilot CLI, retrying up to <see cref="Constants.DistributedBrainMaxRetries"/> times with a 5-second backoff.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _copilotClient = new CopilotClient(new CopilotClientOptions
        {
            CliUrl = $"localhost:{_port}",
            AutoStart = false,
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
            - Is this a documentation-only change? (just coder, maybe skip review)
            - Is this a code change? (needs coder → reviewer → tester → merge)
            - Is there context from previous iterations?

            Respond with JSON:
            {
              "action": "spawn_coder",
              "prompt": "<the prompt you want to send to the first worker>",
              "reason": "<why you chose this plan>"
            }

            Valid actions: spawn_coder, spawn_reviewer, spawn_tester, done, skip
            """;

        var decision = await AskAsync(pipeline, prompt, ct);
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
            - Is this a doc-only change? (maybe skip review)
            - Is this a retry after failure? (what phases need re-running)
            - What does the metrics history suggest?

            Available phases: coding, review, testing, improve, merging

            Respond with JSON:
            {
              "phases": ["coding", "review", "testing", "merging"],
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
    /// <param name="workerRole">Role of the worker the prompt will be sent to.</param>
    /// <param name="additionalContext">Optional extra context to include in the prompt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The crafted prompt string.</returns>
    public async Task<string> CraftPromptAsync(
        GoalPipeline pipeline, string workerRole, string? additionalContext, CancellationToken ct = default)
    {
        var historyContext = BuildMetricsHistoryContext(3);

        var prompt = $$"""
            Craft a prompt for the {{workerRole}} worker.

            Goal: {{pipeline.Description}}
            Iteration: {{pipeline.Iteration}}
            Branch: {{pipeline.CoderBranch ?? "TBD"}}
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}
            {{(historyContext.Length > 0 ? $"\n{historyContext}" : "")}}

            Rules for the prompt you craft:
            - CRITICAL: The branch name is EXACTLY "{{pipeline.CoderBranch ?? "TBD"}}" — do NOT invent or change it
            - The worker infrastructure handles branch creation, checkout, and pushing — do NOT tell workers to create branches or push
            - For coders: Tell them to start implementing immediately — read the relevant files, make code changes, build, test, and commit. Do NOT include git branch or git push commands.
            - For reviewers: tell them to review the diff on branch "{{pipeline.CoderBranch ?? "TBD"}}" against the base branch, produce a REVIEW_REPORT
            - For testers: tell them to build, run tests, write integration tests, produce a TEST_REPORT
            - Include any context from previous phases that would help the worker

            Respond with JSON:
            {
              "action": "spawn_{{workerRole}}",
              "prompt": "<the complete prompt to send to the worker>",
              "reason": "<why you crafted the prompt this way>"
            }
            """;

        var decision = await AskAsync(pipeline, prompt, ct);
        return decision?.Prompt ?? GetFallbackPrompt(workerRole, pipeline);
    }

    /// <summary>
    /// Asks the Brain to interpret the output of a worker and return a verdict.
    /// </summary>
    /// <param name="pipeline">Current goal pipeline state.</param>
    /// <param name="workerRole">Role of the worker that produced the output.</param>
    /// <param name="workerOutput">Raw text output from the worker.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> with the extracted verdict and metrics.</returns>
    public async Task<OrchestratorDecision> InterpretOutputAsync(
        GoalPipeline pipeline, string workerRole, string workerOutput, CancellationToken ct = default)
    {
        var schema = workerRole.ToLowerInvariant() switch
        {
            "tester" => """
                {
                  "verdict": "PASS or FAIL",
                  "test_metrics": {
                    "build_success": true/false,
                    "total_tests": <number>,
                    "passed_tests": <number>,
                    "failed_tests": <number>,
                    "coverage_percent": <number>
                  },
                  "issues": ["<issue1>", "<issue2>"]
                }
                """,
            "reviewer" => """
                {
                  "review_verdict": "APPROVE or REQUEST_CHANGES",
                  "issues": ["<issue1>", "<issue2>"]
                }
                """,
            "improve" => """
                {
                  "verdict": "PASS or FAIL",
                  "issues": ["<issue1>", "<issue2>"]
                }
                """,
            _ => """
                {
                  "verdict": "PASS or FAIL or COMPLETE",
                  "issues": ["<issue1>", "<issue2>"]
                }
                """,
        };

        var modeInstruction = workerRole.ToLowerInvariant() == "improve"
            ? """
              IMPORTANT: You are in INTERPRETATION mode, NOT prompt-crafting mode.
              Do NOT return an "action" or "prompt" field. Do NOT suggest spawning any worker.
              Your ONLY job is to analyze the output below and return a verdict.

              """
            : "";

        var prompt = $$"""
            {{modeInstruction}}Interpret this {{workerRole}}'s output and extract structured data.

            === {{workerRole.ToUpperInvariant()}} OUTPUT (truncated) ===
            {{Truncate(workerOutput, Constants.TruncationVeryLong)}}
            === END OUTPUT ===

            Analyze the output carefully:
            - Did the worker succeed or fail at its task?
            - For testers: did all tests pass? What are the numbers? You MUST populate the test_metrics object with actual numbers from the output — look for test counts in tables, summaries, or dotnet test output.
            - For reviewers: did they approve or request changes? What issues?
            - Extract any specific issues mentioned

            Respond ONLY with this JSON (no other fields):
            {{schema}}
            """;

        return await AskAsync(pipeline, prompt, ct)
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Verdict = "UNKNOWN",
                Reason = "Failed to interpret output",
            };
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
              "action": "<next action: spawn_coder, spawn_reviewer, spawn_tester, merge, done, skip>",
              "prompt": "<prompt for the worker, if spawning one>",
              "reason": "<why this is the right next step>"
            }
            """;

        return await AskAsync(pipeline, prompt, ct)
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
                    break;
                case SessionIdleEvent:
                    done.TrySetResult(response);
                    break;
                case SessionErrorEvent err:
                    done.TrySetException(new InvalidOperationException(err.Data.Message));
                    break;
            }
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(Constants.TaskTimeoutMinutes));
        using var ctReg = cts.Token.Register(() => done.TrySetCanceled());

        await session.SendAsync(new MessageOptions { Prompt = prompt });
        return await done.Task;
    }

    internal static string GetFallbackPrompt(string role, GoalPipeline pipeline) =>
        role.ToLowerInvariant() switch
        {
            "coder" => $"""
                You are working on this goal: {pipeline.Description}
                This is iteration {pipeline.Iteration}. Work on the {pipeline.CoderBranch} branch.
                Write the code and commit your changes with clear commit messages.
                Do NOT run git push — the orchestrator handles that.
                """,
            "reviewer" => $"""
                You are reviewing code changes for this goal: {pipeline.Description}
                This is iteration {pipeline.Iteration}. The coder's work is on branch {pipeline.CoderBranch}.
                Review the diff against the base branch and produce a REVIEW_REPORT block with:
                - verdict: APPROVE or REQUEST_CHANGES
                - issues: list of issues found (if any)
                Do NOT modify any code. Do NOT run git push.
                """,
            "tester" => $"""
                You are testing code for this goal: {pipeline.Description}
                This is iteration {pipeline.Iteration}. The coder's work is on branch {pipeline.CoderBranch}.
                Build the project, run all tests, write integration tests, and produce a TEST_REPORT block with:
                - verdict: PASS or FAIL
                - build_success, total_tests, passed_tests, failed_tests, coverage_percent
                - issues: list of issues found (if any)
                Do NOT run git push — the orchestrator handles that.
                """,
            _ => $"Work on this goal: {pipeline.Description}",
        };

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
