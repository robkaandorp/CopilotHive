using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CopilotHive.Agents;
using CopilotHive.Git;
using CopilotHive.Metrics;
using CopilotHive.Services;
using CopilotHive.Telemetry;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace CopilotHive.Orchestration;

/// <summary>
/// LLM-powered brain that runs inside the orchestrator container.
/// The Brain has two jobs: plan iteration phases and craft worker prompts.
/// Uses a single SharpCoder CodingAgent with one persistent AgentSession
/// that carries context across all goals. File tools give the Brain
/// read-only access to target repositories via BrainRepoManager clones.
/// </summary>
public sealed class DistributedBrain : IDistributedBrain, IAsyncDisposable
{
    private readonly string _modelOverride;
    private readonly int _maxContextTokens;
    private readonly int _maxSteps;
    private readonly ReasoningEffort? _reasoningEffort;
    private readonly ILogger<DistributedBrain> _logger;
    private readonly MetricsTracker? _metricsTracker;
    private readonly BrainRepoManager? _repoManager;
    private readonly string _stateDir;
    private IChatClient? _chatClient;
    private CodingAgent? _agent;
    private AgentSession _session;

    private string _systemPrompt;
    private readonly List<AITool> _brainTools;
    private readonly AgentsManager? _agentsManager;

    /// <summary>
    /// Serialises all Brain LLM calls so that <see cref="_lastToolCallResult"/> is
    /// never overwritten by a concurrent goal's tool lambda.
    /// </summary>
    private readonly SemaphoreSlim _brainCallGate = new(1, 1);

    /// <summary>
    /// Stores the last tool call result captured by the Brain tool lambdas.
    /// Written by tool lambdas (from FunctionInvokingChatClient thread),
    /// read after ExecuteAsync completes.
    /// Access is serialised by <see cref="_brainCallGate"/>.
    /// </summary>
    private volatile BrainToolCallResult? _lastToolCallResult;

    private const string DefaultSystemPrompt = """
        You are the CopilotHive Orchestrator Brain — a product owner and project manager.
        You have two jobs:
        1. Plan iteration phases using the report_iteration_plan tool
        2. Craft clear, specific prompts for workers when asked

        You have read-only access to the target repositories via file tools (read_file, glob, grep).
        Use these to examine project structure, configuration, and code when it helps you plan
        better iterations or craft more targeted worker prompts.

        RULES:
        - When planning iterations, always call report_iteration_plan
        - When crafting prompts, respond with ONLY the prompt text — no tool calls, no JSON, no markdown formatting
        - Never include git checkout/branch/switch/push commands in prompts — infrastructure handles branching
        - Never include framework-specific build/test commands — workers use build and test skills
        """;

    /// <summary>
    /// Initialises a new <see cref="DistributedBrain"/> that connects directly to an LLM provider.
    /// </summary>
    /// <param name="modelOverride">Model string, optionally with provider prefix (e.g. "copilot/claude-sonnet-4.6").</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="metricsTracker">Optional tracker used to include historical metrics in prompts.</param>
    /// <param name="agentsManager">Optional manager used to load the orchestrator's AGENTS.md.</param>
    /// <param name="maxContextTokens">Maximum context window size in tokens. Defaults to <see cref="Constants.DefaultBrainContextWindow"/>.</param>
    /// <param name="maxSteps">Maximum tool-call steps per Brain request. Defaults to <see cref="Constants.DefaultBrainMaxSteps"/>.</param>
    /// <param name="repoManager">Optional manager for persistent Brain repo clones (read-only file access).</param>
    /// <param name="stateDir">Directory for persistent state (session files). Defaults to <c>/app/state</c>.</param>
    public DistributedBrain(string modelOverride, ILogger<DistributedBrain> logger,
        MetricsTracker? metricsTracker = null, Agents.AgentsManager? agentsManager = null,
        int maxContextTokens = Constants.DefaultBrainContextWindow,
        int maxSteps = Constants.DefaultBrainMaxSteps,
        BrainRepoManager? repoManager = null,
        string? stateDir = null)
    {
        _modelOverride = modelOverride;
        _maxContextTokens = maxContextTokens;
        _maxSteps = maxSteps;
        _logger = logger;
        _metricsTracker = metricsTracker;
        _repoManager = repoManager;
        _agentsManager = agentsManager;
        _stateDir = stateDir ?? "/app/state";
        _session = AgentSession.Create("brain");

        var (_, _, reasoning) = SDK.ChatClientFactory.ParseProviderModelAndReasoning(modelOverride);
        _reasoningEffort = reasoning;

        _brainTools = BuildBrainTools();

        var orchestratorInstructions = agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
        _systemPrompt = string.IsNullOrWhiteSpace(orchestratorInstructions)
            ? DefaultSystemPrompt
            : $"{DefaultSystemPrompt}\n\n{orchestratorInstructions}";
    }

    /// <summary>
    /// Creates the IChatClient and CodingAgent for the configured model/provider.
    /// Also loads a previously saved session if one exists.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Brain connecting with model '{Model}'…", _modelOverride);

        _chatClient = SDK.ChatClientFactory.Create(_modelOverride);

        // Try to load a persisted session from a previous run
        var sessionFile = GetSessionFilePath();
        if (File.Exists(sessionFile))
        {
            try
            {
                _session = await AgentSession.LoadAsync(sessionFile, ct);
                _logger.LogInformation("Loaded Brain session with {Count} messages from {File}",
                    _session.MessageHistory.Count, sessionFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Brain session from {File} — starting fresh", sessionFile);
                _session = AgentSession.Create("brain");
            }
        }

        RecreateAgent();

        _logger.LogInformation("Brain connected via CodingAgent (model={Model}, contextWindow={ContextWindow})",
            _modelOverride, _maxContextTokens);
    }

    /// <inheritdoc />
    public async Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
    {
        if (_repoManager is null)
        {
            _logger.LogDebug("No BrainRepoManager configured — skipping repo clone for '{RepoName}'", repoName);
            return;
        }

        await _repoManager.EnsureCloneAsync(repoName, repoUrl, defaultBranch, ct);
        _logger.LogInformation("Brain repo ready for '{RepoName}' at {ClonePath}",
            repoName, _repoManager.GetClonePath(repoName));
    }

    /// <summary>
    /// Builds the AIFunction tools that the Brain LLM can call.
    /// Only <c>report_iteration_plan</c> — prompt crafting uses plain text responses.
    /// </summary>
    private List<AITool> BuildBrainTools()
    {
        var validPhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "coding", "testing", "docwriting", "review", "improve", "merging" };
        var tierablePhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "coding", "testing", "docwriting", "review", "improve" };
        var validTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "standard", "premium" };

        return
        [
            AIFunctionFactory.Create(
                ([Description("Ordered phase names, e.g. [\"coding\",\"testing\",\"docwriting\",\"review\",\"merging\"]")] string[] phases,
                 [Description("JSON-encoded dict of phase name to instruction, e.g. {\"coding\":\"focus on...\",\"review\":\"check...\"}")] string phase_instructions,
                 [Description("Why you chose this iteration plan")] string reason,
                 [Description("Optional JSON-encoded dict of phase name to model tier, e.g. {\"coding\":\"premium\"}. Valid phases: coding, testing, docwriting, review, improve. Valid tiers: standard, premium. Omitted phases use the default tier.")] string? model_tiers = null) =>
                {
                    var invalidPhases = phases?.Where(p => !validPhases.Contains(p)).ToList() ?? [];

                    // Validate model_tiers if provided
                    Dictionary<string, string>? parsedTiers = null;
                    List<string> tierErrors = [];
                    if (model_tiers is not null)
                    {
                        try
                        {
                            parsedTiers = JsonSerializer.Deserialize<Dictionary<string, string>>(model_tiers, ProtocolJson.Options);
                        }
                        catch (JsonException)
                        {
                            tierErrors.Add("model_tiers must be valid JSON");
                        }

                        if (parsedTiers is not null)
                        {
                            var invalidTierPhases = parsedTiers.Keys.Where(k => !tierablePhases.Contains(k)).ToList();
                            if (invalidTierPhases.Count > 0)
                                tierErrors.Add($"invalid phase names in model_tiers: {string.Join(", ", invalidTierPhases)}. Valid: {string.Join(", ", tierablePhases)}");

                            var invalidTierValues = parsedTiers.Values.Where(v => !validTiers.Contains(v)).ToList();
                            if (invalidTierValues.Count > 0)
                                tierErrors.Add($"invalid tier values in model_tiers: {string.Join(", ", invalidTierValues)}. Valid: {string.Join(", ", validTiers)}");
                        }
                    }

                    var error = Shared.ToolValidation.Check(
                        (phases is { Length: > 0 }, "phases must be a non-empty array"),
                        (invalidPhases.Count == 0,
                            $"invalid phase names: {string.Join(", ", invalidPhases)}. Valid: {string.Join(", ", validPhases)}"),
                        (!string.IsNullOrEmpty(reason), "reason is required"),
                        (tierErrors.Count == 0, string.Join("; ", tierErrors)));
                    if (error is not null) return error;

                    _lastToolCallResult = new BrainToolCallResult("report_iteration_plan", new Dictionary<string, object?>
                    {
                        ["phases"] = phases, ["phase_instructions"] = phase_instructions, ["reason"] = reason,
                        ["model_tiers"] = model_tiers,
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
    /// Creates a CodingAgent with current configuration. Called on connect
    /// and when the underlying configuration changes.
    /// </summary>
    private void RecreateAgent()
    {
        if (_chatClient is null)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");

        var workDir = _repoManager?.WorkDirectory ?? _stateDir;

        _agent = new CodingAgent(_chatClient, new AgentOptions
        {
            WorkDirectory = workDir,
            MaxSteps = _maxSteps,
            EnableBash = false,
            EnableFileOps = _repoManager is not null,
            EnableFileWrites = false,
            EnableSkills = false,
            SystemPrompt = _systemPrompt,
            CustomTools = _brainTools,
            MaxContextTokens = _maxContextTokens,
            EnableAutoCompaction = true,
            AutoLoadWorkspaceInstructions = false,
            ReasoningEffort = _reasoningEffort,
            Logger = _logger,
            OnCompacted = r =>
            {
                _logger.LogInformation(
                    "Brain context compaction: {TokensBefore} \u2192 {TokensAfter} tokens ({ReductionPercent}% reduction), {MessagesBefore} \u2192 {MessagesAfter} messages",
                    r.TokensBefore, r.TokensAfter, r.ReductionPercent, r.MessagesBefore, r.MessagesAfter);

                // Re-inject orchestrator instructions after compaction so they survive summarization
                var instructions = _agentsManager?.GetAgentsMd(WorkerRole.Orchestrator);
                if (!string.IsNullOrWhiteSpace(instructions))
                {
                    _session.MessageHistory.Add(new ChatMessage(ChatRole.User,
                        $"ORCHESTRATOR INSTRUCTIONS (re-injected after context compaction):\n\n{instructions}"));
                    _session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
                        "Acknowledged. Orchestrator instructions refreshed."));
                    _logger.LogInformation("Re-injected orchestrator instructions after compaction");
                }
            },
        });

        _logger.LogDebug("CodingAgent created with WorkDirectory={WorkDir}, FileOps={FileOps}",
            workDir, _repoManager is not null);
    }

    /// <summary>Returns the file path for persisting the Brain session.</summary>
    private string GetSessionFilePath() => Path.Combine(_stateDir, "brain-session.json");

    /// <inheritdoc />
    public async Task ResetSessionAsync(CancellationToken ct = default)
    {
        await _brainCallGate.WaitAsync(ct);
        try
        {
            // Clear session message history
            _session.MessageHistory.Clear();

            // Re-read orchestrator instructions and rebuild system prompt
            var orchestratorInstructions = _agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
            _systemPrompt = string.IsNullOrWhiteSpace(orchestratorInstructions)
                ? DefaultSystemPrompt
                : $"{DefaultSystemPrompt}\n\n{orchestratorInstructions}";

            // Recreate agent with fresh system prompt (applies new _systemPrompt)
            if (_chatClient is not null)
                RecreateAgent();

            // Add system message to session so the new prompt is part of the conversation
            _session.MessageHistory.Add(new ChatMessage(ChatRole.System, _systemPrompt));

            // Delete the persisted session file so the old context is not reloaded on restart
            var sessionFile = GetSessionFilePath();
            if (File.Exists(sessionFile))
            {
                File.Delete(sessionFile);
                _logger.LogInformation("Deleted previous Brain session file");
            }

            // Save the reset session
            await SaveSessionAsync(ct);

            _logger.LogInformation("Brain session reset — system prompt rebuilt with latest orchestrator instructions");
        }
        finally
        {
            _brainCallGate.Release();
        }
    }

    /// <summary>Persists the current Brain session to disk.</summary>
    internal async Task SaveSessionAsync(CancellationToken ct = default)
    {
        var path = GetSessionFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _session.SaveAsync(path, ct);
        _logger.LogDebug("Brain session saved ({Count} messages)", _session.MessageHistory.Count);
    }

    /// <inheritdoc/>
    public async Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instructions)) return;

        _session.MessageHistory.Add(new ChatMessage(ChatRole.User,
            $"ORCHESTRATOR INSTRUCTIONS UPDATE:\n\n{instructions}"));
        _session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
            "Acknowledged. I will follow the updated orchestrator instructions for all future goals."));

        _logger.LogInformation("Injected orchestrator instructions into Brain session ({Chars} chars)",
            instructions.Length);

        await SaveSessionAsync(ct);
    }

    /// <summary>
    /// Asks the Brain to plan which phases should run during the current iteration.
    /// </summary>
    public async Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        var previousIterationContext = BuildPreviousIterationContext(pipeline);

        var retryContext = pipeline.Iteration > 1
            ? $"""
              Retry context:
              - This is iteration {pipeline.Iteration} (previous attempts: {pipeline.Iteration - 1})
              - Review retries: {pipeline.ReviewRetries}
              - Test retries: {pipeline.TestRetries}
              This is a retry — use the feedback above to plan which phases need re-running.
              """
            : "This is the first iteration — no previous feedback.";

        var historyContext = BuildMetricsHistoryContext();

        var conversationSummary = pipeline.Conversation.Count > 0
            ? $"Conversation history ({pipeline.Conversation.Count} messages): " +
              Truncate(string.Join(" | ", pipeline.Conversation.Select(e => $"[{e.Role}] {e.Content}")), Constants.TruncationConversationSummary)
            : "";

        var prompt = $$"""
            Plan the workflow for iteration {{pipeline.Iteration}} of goal: {{pipeline.Description}}

            Target repositories: {{string.Join(", ", pipeline.Goal.RepositoryNames)}}
            (You can browse the code under these folder names in your working directory)

            {{retryContext}}
            {{previousIterationContext}}
            {{historyContext}}
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
            - model_tiers: (optional) JSON object to escalate specific phases to premium tier
              (e.g. {"coding": "premium"}). Valid phases: coding, testing, docwriting, review, improve.
              Only use premium when previous iterations failed and you believe the task requires
              stronger reasoning. Omitted phases use the default tier.
            """;

        if (_agent is null)
        {
            _logger.LogWarning("Brain not connected — using default iteration plan for goal {GoalId}", pipeline.GoalId);
            return IterationPlan.Default();
        }

        try
        {
            const int maxToolAttempts = 3;
            string currentPrompt = prompt;

            for (int attempt = 1; attempt <= maxToolAttempts; attempt++)
            {
                var (response, toolCall) = await Shared.CopilotRetryPolicy.ExecuteAsync(
                    () => ExecuteBrainAsync(currentPrompt, ct),
                    onRetry: (retryAttempt, delay, ex) =>
                    {
                        _logger.LogWarning(
                            "Brain iteration plan call failed (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                            retryAttempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                    },
                    ct);

                pipeline.Conversation.Add(new ConversationEntry("user", currentPrompt));
                pipeline.Conversation.Add(new ConversationEntry("assistant", response));

                if (toolCall is { ToolName: "report_iteration_plan" })
                {
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
                    break;
                }

                if (attempt < maxToolAttempts)
                {
                    _logger.LogWarning(
                        "Brain responded with text instead of calling report_iteration_plan (attempt {Attempt}/{Max}). Nudging.",
                        attempt, maxToolAttempts);
                    currentPrompt = "You must call the report_iteration_plan tool now. Do not respond with text.";
                }
                else
                {
                    _logger.LogWarning(
                        "Brain did not call report_iteration_plan after {MaxAttempts} attempts for goal {GoalId}",
                        maxToolAttempts, pipeline.GoalId);
                }
            }
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

        var phaseTiers = new Dictionary<GoalPhase, ModelTier>();
        if (dto.ModelTiers is not null)
        {
            foreach (var (key, value) in dto.ModelTiers)
            {
                if (Enum.TryParse<GoalPhase>(key, ignoreCase: true, out var phase) && value is not null)
                    phaseTiers[phase] = ModelTierExtensions.ParseModelTier(value);
            }
        }

        return new IterationPlan
        {
            Phases = phases,
            PhaseInstructions = instructions,
            PhaseTiers = phaseTiers,
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
            ModelTiers = ParseJsonDictArg(args, "model_tiers"),
            Reason = GetStringArg(args, "reason"),
        };

        return MapIterationPlan(dto);
    }

    /// <summary>DTO for deserializing the Brain's iteration plan JSON response.</summary>
    internal sealed record IterationPlanDto
    {
        public List<string> Phases { get; init; } = [];
        public Dictionary<string, string>? PhaseInstructions { get; init; }
        public Dictionary<string, string>? ModelTiers { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Asks the Brain to craft a prompt for the specified phase's worker.
    /// </summary>
    public async Task<string> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        var roleName = phase.ToWorkerRole().ToRoleName();
        var historyContext = BuildMetricsHistoryContext(3);

        var phaseInstructions = "";
        if (pipeline.Plan?.PhaseInstructions.TryGetValue(phase, out var instructions) == true)
            phaseInstructions = $"\nPhase instructions from the plan:\n{instructions}";

        // Check if docwriting preceded review in this iteration's plan, so the
        // reviewer knows that CHANGELOG/README changes are expected and in-scope.
        var docWritingPrecededReview = pipeline.Plan?.Phases is { } phases
            && phases.IndexOf(GoalPhase.DocWriting) is >= 0 and var dwIdx
            && phases.IndexOf(GoalPhase.Review) is >= 0 and var rvIdx
            && dwIdx < rvIdx;

        var roleInstruction = phase switch
        {
            GoalPhase.Coding => """
                - For coders: Tell them to start implementing immediately — read the relevant files, make code changes, use build skill, use test skill, and commit with `git add -A && git commit`. NEVER include git checkout, git branch, or git push commands. NEVER include dotnet/npm/cargo commands — only reference build and test skills.
                """,
            GoalPhase.Review when docWritingPrecededReview => """
                - For reviewers: Do NOT include any git diff commands in your prompt — the worker's WORKSPACE CONTEXT already provides the correct diff command with the exact merge-base hash. Just tell them to review the branch changes using the diff command from their workspace context, focus only on the diff lines (+ and -), and call the report_review_verdict tool when done.
                - IMPORTANT: The docwriting phase already ran before this review. Changes to CHANGELOG.md, README.md, and XML doc comments are EXPECTED and should NOT be flagged as scope violations.
                """,
            GoalPhase.Review => """
                - For reviewers: Do NOT include any git diff commands in your prompt — the worker's WORKSPACE CONTEXT already provides the correct diff command with the exact merge-base hash. Just tell them to review the branch changes using the diff command from their workspace context, focus only on the diff lines (+ and -), and call the report_review_verdict tool when done.
                """,
            GoalPhase.Testing => """
                - For testers: tell them to build, run the test skill, write integration tests, and call the report_test_results tool when done. Do NOT tell them to create report files.
                """,
            GoalPhase.DocWriting => """
                - For docwriters: Do NOT include any git diff commands in your prompt — the worker's WORKSPACE CONTEXT already provides the correct diff command. Tell them to use the diff command from their workspace context to see what changed, then update README, CHANGELOG, and XML doc comments. Build to verify and commit. Call the report_doc_changes tool when done.
                """,
            GoalPhase.Improve => """
                - For improvers: tell them to analyze iteration results and update *.agents.md files directly using file tools. Do NOT tell them to run git commands — the infrastructure commits and pushes automatically.
                """,
            _ => throw new InvalidOperationException($"Unhandled phase in CraftPromptAsync: '{phase}'"),
        };

        // When retrying after review/test rejection, include the specific feedback
        // so the coder/tester knows exactly what to fix.
        var previousFeedback = (phase is GoalPhase.Coding or GoalPhase.Testing && pipeline.Iteration > 1)
            ? BuildPreviousIterationContext(pipeline)
            : "";

        var prompt = $$"""
            Craft a prompt for the {{roleName}} worker.

            Goal: {{pipeline.Description}}
            Iteration: {{pipeline.Iteration}}
            Current phase: {{phase}}
            Target repositories: {{string.Join(", ", pipeline.Goal.RepositoryNames)}}
            {{phaseInstructions}}
            {{(previousFeedback.Length > 0 ? $"\n{previousFeedback}" : "")}}
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}
            {{(historyContext.Length > 0 ? $"\n{historyContext}" : "")}}

            The worker has access to project skills (e.g. build, test) that describe how to build and test this project.
            Tell the worker to use those skills instead of hardcoding framework-specific commands.

            Rules for the prompt you craft:
            - The branch is already checked out by the infrastructure — do NOT mention branch names
            - NEVER include git checkout, git branch, git switch, or git push commands — the infrastructure handles all branching
            - NEVER include framework-specific build/test commands (dotnet build, npm test, etc.) — tell workers to use build and test skills
            {{roleInstruction}}
            - Include any context from previous phases that would help the worker

            Respond with ONLY the prompt text — no tool calls, no JSON, no markdown wrapping.
            """;

        if (_agent is null)
        {
            _logger.LogWarning("Brain not connected — using fallback prompt for {GoalId} phase {Phase}",
                pipeline.GoalId, phase);
            return $"Work on: {pipeline.Description}";
        }

        try
        {
            var craftedPrompt = await Shared.CopilotRetryPolicy.ExecuteAsync(
                async () =>
                {
                    _logger.LogDebug("Brain craft-prompt request for {GoalId} (phase={Phase}):\n{Prompt}",
                        pipeline.GoalId, phase, Truncate(prompt, Constants.TruncationVerbose));

                    var (response, _) = await ExecuteBrainAsync(prompt, ct);

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

    /// <summary>Extracts a JSON-encoded string→string dictionary from tool call arguments.</summary>
    internal static Dictionary<string, string>? ParseJsonDictArg(Dictionary<string, object?> args, string key)
    {
        var str = GetStringArg(args, key);
        if (string.IsNullOrEmpty(str))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(str, ProtocolJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Formats a context-usage log message for the Brain LLM call.</summary>
    /// <param name="inputTokens">Cumulative input tokens used so far.</param>
    /// <param name="contextWindow">Maximum context window size in tokens.</param>
    /// <param name="callerName">Name of the calling method (populated by <see cref="System.Runtime.CompilerServices.CallerMemberNameAttribute"/>).</param>
    /// <returns>A human-readable context-usage message.</returns>
    internal static string FormatContextUsageMessage(long inputTokens, int contextWindow, string callerName)
    {
        var pct = contextWindow > 0 ? inputTokens * 100.0 / contextWindow : 0.0;
        return $"Brain context usage: {pct.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}% ({inputTokens}/{contextWindow} tokens) after {callerName}";
    }

    private async Task<(string Text, BrainToolCallResult? ToolCall)> ExecuteBrainAsync(
        string prompt, CancellationToken ct,
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        EnsureConnected();

        await _brainCallGate.WaitAsync(ct);
        try
        {
            _lastToolCallResult = null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(Constants.TaskTimeoutMinutes));

            var result = await _agent!.ExecuteAsync(_session, prompt, cts.Token);
            var responseText = result.Message;

            // Log usage
            if (result.Usage is not null)
            {
                _logger.LogDebug(
                    "Brain Usage: model={Model} in={InputTokens} out={OutputTokens} tools={ToolCalls}",
                    result.ModelId, result.Usage.InputTokenCount, result.Usage.OutputTokenCount,
                    result.ToolCallCount);
            }

            // Log context size (compaction is logged via OnCompacted callback)
            var estimatedTokens = _session.EstimatedContextTokens;
            var usagePct = _maxContextTokens > 0 ? (int)(estimatedTokens * 100.0 / _maxContextTokens) : 0;
            _logger.LogInformation(
                "Brain context: messages={Messages} ~tokens={EstTokens}/{Limit} ({Pct}%) cumIn={CumIn} cumOut={CumOut}",
                _session.MessageHistory.Count, estimatedTokens, _maxContextTokens,
                usagePct, _session.InputTokensUsed, _session.OutputTokensUsed);

            _logger.LogInformation("{Message}", FormatContextUsageMessage(_session.InputTokensUsed, _maxContextTokens, callerName));

            _logger.LogDebug("Brain response ({Length} chars), tool={Tool}",
                responseText.Length, _lastToolCallResult?.ToolName ?? "none");

            // Auto-save session after each Brain call
            try { await SaveSessionAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to save Brain session"); }

            return (responseText, _lastToolCallResult);
        }
        finally
        {
            _brainCallGate.Release();
        }
    }

    /// <inheritdoc />
    public BrainStats? GetStats()
    {
        if (_agent is null) return null;

        var estimatedTokens = _session.EstimatedContextTokens;
        var usagePct = _maxContextTokens > 0 ? (int)(estimatedTokens * 100.0 / _maxContextTokens) : 0;

        return new BrainStats
        {
            Model = _modelOverride,
            MessageCount = _session.MessageHistory.Count,
            EstimatedContextTokens = estimatedTokens,
            MaxContextTokens = _maxContextTokens,
            ContextUsagePercent = usagePct,
            CumulativeInputTokens = _session.InputTokensUsed,
            CumulativeOutputTokens = _session.OutputTokensUsed,
            MaxSteps = _maxSteps,
            IsConnected = true,
        };
    }

    /// <summary>
    /// Builds a concise metrics history summary from the last N iterations.
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
        if (_agent is null)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");
    }

    internal static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>
    /// Builds a summary of the previous iteration's reviewer/tester feedback
    /// from <see cref="GoalPipeline.PhaseOutputs"/>. Returns an empty string
    /// for the first iteration.
    /// </summary>
    internal static string BuildPreviousIterationContext(GoalPipeline pipeline)
    {
        if (pipeline.Iteration <= 1)
            return "";

        var prevIteration = pipeline.Iteration - 1;
        var sb = new StringBuilder();
        sb.AppendLine($"Previous iteration ({prevIteration}) feedback:");

        var hasAnyFeedback = false;

        // Include reviewer feedback — the most critical signal for replanning
        var reviewerKey = $"reviewer-{prevIteration}";
        if (pipeline.PhaseOutputs.TryGetValue(reviewerKey, out var reviewerOutput)
            && !string.IsNullOrWhiteSpace(reviewerOutput))
        {
            hasAnyFeedback = true;
            sb.AppendLine("  REVIEWER feedback (this is why the iteration was rejected):");
            sb.AppendLine($"  {Truncate(reviewerOutput, Constants.TruncationConversationSummary)}");
        }

        // Include tester feedback if tests failed
        var testerKey = $"tester-{prevIteration}";
        if (pipeline.PhaseOutputs.TryGetValue(testerKey, out var testerOutput)
            && !string.IsNullOrWhiteSpace(testerOutput))
        {
            hasAnyFeedback = true;
            sb.AppendLine("  TESTER feedback:");
            sb.AppendLine($"  {Truncate(testerOutput, Constants.TruncationConversationSummary)}");
        }

        // Include coder output for context on what was attempted
        var coderKey = $"coder-{prevIteration}";
        if (pipeline.PhaseOutputs.TryGetValue(coderKey, out var coderOutput)
            && !string.IsNullOrWhiteSpace(coderOutput))
        {
            hasAnyFeedback = true;
            sb.AppendLine("  CODER output (what was attempted):");
            sb.AppendLine($"  {Truncate(coderOutput, Constants.TruncationMedium)}");
        }

        if (!hasAnyFeedback)
        {
            sb.AppendLine("  (No phase outputs recorded for the previous iteration)");
        }

        return sb.ToString();
    }

    /// <summary>Saves the session and disposes the underlying chat client.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await SaveSessionAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to save Brain session on dispose"); }

        _agent = null;
        _chatClient?.Dispose();
        _brainCallGate.Dispose();
    }
}
