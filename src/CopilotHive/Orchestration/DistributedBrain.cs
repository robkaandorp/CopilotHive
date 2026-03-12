using System.Collections.Concurrent;
using CopilotHive.Copilot;
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
    private CopilotClient? _copilotClient;
    private readonly ConcurrentDictionary<string, CopilotSession> _sessions = new();

    private const string SystemPrompt = """
        You are the CopilotHive Orchestrator Brain — a product owner and project manager
        for a distributed multi-agent software development system. You make ALL tactical decisions:

        - You decide which workers to spawn and in what order
        - You craft the prompts sent to workers (coder, reviewer, tester, improver)
        - You interpret worker output to determine verdicts and extract metrics
        - You decide whether to retry, skip, or proceed to the next phase

        IMPORTANT: Always respond with valid JSON matching the requested schema.
        Do NOT wrap the JSON in markdown code fences. Return ONLY the JSON object.
        """;

    public DistributedBrain(int port, ILogger<DistributedBrain> logger)
    {
        _port = port;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _copilotClient = new CopilotClient(new CopilotClientOptions
        {
            CliUrl = $"localhost:{_port}",
            AutoStart = false,
        });

        // Retry connection to Copilot CLI
        Exception? lastException = null;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                await _copilotClient.StartAsync();
                _logger.LogInformation("Brain connected to Copilot on port {Port} (attempt {Attempt})", _port, attempt);
                return;
            }
            catch (Exception ex) when (attempt < 20)
            {
                lastException = ex;
                _logger.LogDebug("Brain connection attempt {Attempt}/20 failed: {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        throw new InvalidOperationException(
            $"Brain failed to connect to Copilot on port {_port} after 20 attempts: {lastException?.Message}");
    }

    /// <summary>
    /// Gets or creates a dedicated Copilot session for a goal.
    /// Each session maintains its own conversation history natively.
    /// </summary>
    private async Task<CopilotSession> GetOrCreateSessionAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        EnsureConnected();

        if (_sessions.TryGetValue(pipeline.GoalId, out var existing))
            return existing;

        var session = await _copilotClient!.CreateSessionAsync(new SessionConfig
        {
            Streaming = false,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        });

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
    public async Task ReprimeSessionAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        EnsureConnected();

        // Remove any stale session
        if (_sessions.TryRemove(pipeline.GoalId, out var stale))
            await stale.DisposeAsync();

        var session = await _copilotClient!.CreateSessionAsync(new SessionConfig
        {
            Streaming = false,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        });

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
                {Truncate(summary, 8000)}

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

        var prompt = $$"""
            Plan the workflow for this goal:
            {{pipeline.Description}}

            {{metricsContext}}

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

    public async Task<string> CraftPromptAsync(
        GoalPipeline pipeline, string workerRole, string? additionalContext, CancellationToken ct = default)
    {
        var prompt = $$"""
            Craft a prompt for the {{workerRole}} worker.

            Goal: {{pipeline.Description}}
            Iteration: {{pipeline.Iteration}}
            Branch: {{pipeline.CoderBranch ?? "TBD"}}
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}

            Rules for the prompt you craft:
            - For coders: tell them the goal, the branch to work on, remind them to commit changes and NOT run git push
            - For reviewers: tell them to review the diff on the branch against the base branch, produce a REVIEW_REPORT
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
            _ => """
                {
                  "verdict": "PASS or FAIL or COMPLETE",
                  "issues": ["<issue1>", "<issue2>"]
                }
                """,
        };

        var prompt = $$"""
            Interpret this {{workerRole}}'s output and extract structured data.

            === {{workerRole.ToUpperInvariant()}} OUTPUT (truncated) ===
            {{Truncate(workerOutput, 6000)}}
            === END OUTPUT ===

            Analyze the output carefully:
            - Did the worker succeed or fail at its task?
            - For testers: did all tests pass? What are the numbers?
            - For reviewers: did they approve or request changes? What issues?
            - Extract any specific issues mentioned

            Respond with JSON:
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
            var response = await SendToSessionAsync(session, prompt, ct);

            // Keep audit log in the pipeline for debugging
            pipeline.Conversation.Add(new ConversationEntry("user", Truncate(prompt, 500)));
            pipeline.Conversation.Add(new ConversationEntry("assistant", Truncate(response, 500)));

            var parsed = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(response);
            if (parsed is null)
                _logger.LogWarning("Failed to parse Brain JSON response: {Response}", Truncate(response, 200));

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

        using var ctReg = ct.Register(() => done.TrySetCanceled());
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

    private void EnsureConnected()
    {
        if (_copilotClient is null)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");
    }

    internal static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    public async ValueTask DisposeAsync()
    {
        // Dispose all goal sessions
        foreach (var (goalId, session) in _sessions)
        {
            try { await session.DisposeAsync(); }
            catch { /* best-effort cleanup */ }
        }
        _sessions.Clear();

        if (_copilotClient is not null)
        {
            await _copilotClient.StopAsync();
            _copilotClient = null;
        }
    }
}
