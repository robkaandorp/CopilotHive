using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CopilotHive.Agents;
using CopilotHive.Git;
using CopilotHive.Goals;
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
    private readonly IBrainRepoManager? _repoManager;
    private readonly IGoalStore? _goalStore;
    private readonly string _stateDir;
    private IChatClient? _chatClient;
    private CodingAgent? _agent;
    private AgentSession _session;

    private string _systemPrompt;
    private readonly List<AITool> _brainTools;
    private readonly AgentsManager? _agentsManager;

    /// <summary>
    /// Active pipeline snapshots keyed by goal ID. Used by the <c>get_goal</c> tool to return
    /// iteration and phase information for a goal without requiring a full <see cref="IGoalStore"/> query.
    /// Updated by <see cref="RegisterActivePipeline"/> when goal dispatch begins.
    /// </summary>
    private Dictionary<string, GoalPipeline>? _activePipelines;

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
        - When crafting prompts, respond with ONLY the prompt text — no JSON, no markdown formatting
        - If you need clarification during planning or prompt crafting that cannot be determined from the codebase, call the escalate_to_composer tool with a question and reason
        - Never include git checkout/branch/switch/push commands in prompts — infrastructure handles branching
        - Never include framework-specific build/test commands — workers use build and test skills

        WORKER PROMPT RULES:
        When crafting worker prompts, follow these rules per role:
        - Coders: Tell them to implement immediately, read files, use build/test skills, commit with git add -A && git commit. Never include git branch or push commands.
        - Testers: Tell them to build, run test skill, write integration tests, call report_test_results. Never tell them to create report files.
        - Reviewers: Do NOT include git diff commands — the worker's workspace context provides the correct diff. Tell them to review using their workspace diff commands, focus on +/- lines, call report_review_verdict. Files to change is guidance, Files NOT to change is strict. Test changes are always acceptable. Use the testing phase results to verify that all tests pass — do NOT reject because you cannot run tests yourself.
        - DocWriters: Do NOT include git diff commands. Tell them to use workspace context diff, update only requested docs, build to verify, call report_doc_changes.
        - Improvers: Tell them to analyze results and update *.agents.md files using file tools. No git commands.
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
    /// <param name="goalStore">Optional goal store used by the <c>get_goal</c> tool to retrieve goal details on demand.</param>
    public DistributedBrain(string modelOverride, ILogger<DistributedBrain> logger,
        MetricsTracker? metricsTracker = null, Agents.AgentsManager? agentsManager = null,
        int maxContextTokens = Constants.DefaultBrainContextWindow,
        int maxSteps = Constants.DefaultBrainMaxSteps,
        IBrainRepoManager? repoManager = null,
        string? stateDir = null,
        IGoalStore? goalStore = null)
    {
        _modelOverride = modelOverride;
        _maxContextTokens = maxContextTokens;
        _maxSteps = maxSteps;
        _logger = logger;
        _metricsTracker = metricsTracker;
        _repoManager = repoManager;
        _agentsManager = agentsManager;
        _goalStore = goalStore;
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
    /// Registers a pipeline as active so the <c>get_goal</c> tool can include iteration
    /// and phase context in its response.
    /// </summary>
    /// <param name="pipeline">The active goal pipeline.</param>
    public void RegisterActivePipeline(GoalPipeline pipeline)
    {
        _activePipelines ??= [];
        _activePipelines[pipeline.GoalId] = pipeline;
    }

    /// <summary>
    /// Removes a pipeline from the active-pipeline registry once a goal completes or fails.
    /// </summary>
    /// <param name="goalId">The goal ID whose pipeline to deregister.</param>
    public void DeregisterActivePipeline(string goalId)
    {
        _activePipelines?.Remove(goalId);
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
                ([Description("The question to forward to the Composer for resolution.")] string question,
                 [Description("The reason why the Brain cannot answer this question from the codebase.")] string reason) =>
                {
                    _lastToolCallResult = new BrainToolCallResult("escalate_to_composer", new Dictionary<string, object?>
                    {
                        ["question"] = question,
                        ["reason"] = reason,
                    });
                    return "Escalation recorded.";
                },
                "escalate_to_composer",
                "Escalate a question to the Composer when the Brain cannot answer from the codebase alone."),
            AIFunctionFactory.Create(
                async ([Description("The goal ID to retrieve details for.")] string goal_id) =>
                {
                    if (_goalStore is null)
                        return "Goal store is not available.";

                    var goal = await _goalStore.GetGoalAsync(goal_id);
                    if (goal is null)
                        return $"Goal '{goal_id}' not found.";

                    var pipeline = _activePipelines?.GetValueOrDefault(goal_id);
                    var iterationInfo = pipeline is not null
                        ? $"Current iteration: {pipeline.Iteration}, Phase: {pipeline.Phase}"
                        : "Pipeline not active.";

                    return $"""
                        Goal ID: {goal.Id}
                        Description: {goal.Description}
                        Status: {goal.Status}
                        Repositories: {string.Join(", ", goal.RepositoryNames)}
                        {iterationInfo}
                        """;
                },
                "get_goal",
                "Retrieve goal details (description, status, repositories, iteration info) by goal ID."),
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
    /// Returns a <see cref="PlanResult"/> that either contains the plan or an escalation request.
    /// </summary>
    /// <param name="pipeline">The goal pipeline containing iteration state and context.</param>
    /// <param name="additionalContext">Optional extra context prepended to the planning prompt (e.g. retry context for a previously failed goal).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
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

        var conversationSummary = pipeline.Conversation.Count > 0
            ? $"Conversation history ({pipeline.Conversation.Count} messages): " +
              Truncate(string.Join(" | ", pipeline.Conversation.Select(e => $"[{e.Role}] {e.Content}")), Constants.TruncationConversationSummary)
            : "";

        var prompt = $$"""
            {{(additionalContext is not null ? $"{additionalContext}\n\n" : "")}}Plan the workflow for iteration {{pipeline.Iteration}} of goal: {{pipeline.Description}}

            Target repositories: {{string.Join(", ", pipeline.Goal.RepositoryNames)}}
            (You can browse the code under these folder names in your working directory)

            {{retryContext}}
            {{previousIterationContext}}
            {{conversationSummary}}

            Decide the ordered phases for this iteration. Consider:
            - Is this a documentation-only change? (coder edits, then docwriter — may skip testing)
            - Is this a retry after failure? (what phases need re-running)
            IMPORTANT: Only include the docwriting phase when the goal explicitly requests
            documentation updates (e.g. "update README", "add changelog entry", "update docs").
            Skip docwriting for purely internal changes (refactors, bug fixes, test additions)
            unless the goal description specifically calls for it.
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

            If the goal description is ambiguous or you need domain knowledge to plan properly,
            call the `escalate_to_composer` tool instead with a question and reason.
            """;

        if (_agent is null)
        {
            _logger.LogWarning("Brain not connected — using default iteration plan for goal {GoalId}", pipeline.GoalId);
            return PlanResult.Success(IterationPlan.Default());
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

                pipeline.Conversation.Add(new ConversationEntry("user", currentPrompt, pipeline.Iteration, "planning"));
                pipeline.Conversation.Add(new ConversationEntry("assistant", response, pipeline.Iteration, "planning"));

                // Check for escalate_to_composer BEFORE report_iteration_plan
                if (toolCall is { ToolName: "escalate_to_composer" })
                {
                    var escalationQuestion = GetStringArg(toolCall.Arguments, "question") ?? "Brain requested clarification during planning";
                    var escalationReason = GetStringArg(toolCall.Arguments, "reason") ?? "Brain requested escalation";
                    _logger.LogInformation(
                        "Brain escalated planning for goal {GoalId}: {Reason}", pipeline.GoalId, escalationReason);
                    return PlanResult.Escalated(escalationQuestion, escalationReason);
                }

                if (toolCall is { ToolName: "report_iteration_plan" })
                {
                    var plan = BuildIterationPlanFromToolCall(toolCall);

                    if (plan is { Phases.Count: > 0 })
                    {
                        _logger.LogInformation(
                            "Brain planned iteration {Iteration} for goal {GoalId}: [{Phases}] — {Reason}",
                            pipeline.Iteration, pipeline.GoalId,
                            string.Join(", ", plan.Phases), plan.Reason ?? "no reason");
                        return PlanResult.Success(plan);
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
            pipeline.Conversation.Add(new ConversationEntry("system", $"Error: {ex.Message}", pipeline.Iteration, "error"));
        }

        _logger.LogInformation("Using default iteration plan for goal {GoalId}", pipeline.GoalId);
        return PlanResult.Success(IterationPlan.Default());
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

    /// <inheritdoc />
    public async Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        if (_agent is null)
        {
            _logger.LogDebug("Brain not connected — skipping commit message generation for goal {GoalId}", pipeline.GoalId);
            return null;
        }

        var prompt = $$"""
            Generate a concise git commit message for a squash merge of goal: {{pipeline.Description}}

            Goal ID: {{pipeline.GoalId}}
            Target repositories: {{string.Join(", ", pipeline.Goal.RepositoryNames)}}

            Format:
            - First line: a short imperative subject (~72 characters, no "Goal:" prefix)
            - Blank line
            - 2–4 bullet points summarizing what was implemented

            Respond with ONLY the commit message text — no tool calls, no JSON, no markdown wrapping.
            """;

        try
        {
            string? message = null;

            await Shared.CopilotRetryPolicy.ExecuteAsync(
                async () =>
                {
                    var (response, _) = await ExecuteBrainAsync(prompt, ct);

                    if (!string.IsNullOrWhiteSpace(response))
                        message = response.Trim();
                    else
                        throw new InvalidOperationException(
                            $"Brain returned empty commit message for {pipeline.GoalId}");
                },
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain commit message generation failed for {GoalId} (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        pipeline.GoalId, attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                },
                ct);

            _logger.LogDebug("Brain generated commit message for goal {GoalId}: {Message}",
                pipeline.GoalId, message);

            return message;
        }
        catch (OperationCanceledException)
        {
            throw; // Preserve cancellation - do NOT swallow it
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate Brain commit message for goal {GoalId} — will use fallback",
                pipeline.GoalId);
            return null;
        }
    }

    /// <summary>
    /// Builds the raw prompt text that will be sent to the Brain to craft a worker prompt.
    /// Extracted for testability — allows verifying prompt content without a connected agent.
    /// </summary>
    /// <param name="pipeline">The goal pipeline with phase outputs and iteration state.</param>
    /// <param name="phase">The current goal phase.</param>
    /// <param name="additionalContext">Optional additional context to include.</param>
    /// <returns>The assembled prompt text.</returns>
    internal string BuildCraftPromptText(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null)
    {
        var roleName = phase.ToWorkerRole().ToRoleName();

        var phaseInstructions = "";
        if (pipeline.Plan?.PhaseInstructions.TryGetValue(phase, out var instructions) == true)
            phaseInstructions = $"\nPhase instructions from the plan:\n{instructions}";

        // Check if docwriting preceded review in this iteration's plan, so the
        // reviewer knows that CHANGELOG/README changes are expected and in-scope.
        var docWritingPrecededReview = pipeline.Plan?.Phases is { } phases
            && phases.IndexOf(GoalPhase.DocWriting) is >= 0 and var dwIdx
            && phases.IndexOf(GoalPhase.Review) is >= 0 and var rvIdx
            && dwIdx < rvIdx;

        // When retrying after review/test rejection, include the specific feedback
        // so the coder/tester knows exactly what to fix.
        var previousFeedback = (phase is GoalPhase.Coding or GoalPhase.Testing && pipeline.Iteration > 1)
            ? BuildPreviousIterationContext(pipeline)
            : "";

        // For review phase, extract the tester output from the current iteration so the reviewer
        // can verify test results without needing to run tests themselves.
        string currentTestResults;
        if (phase == GoalPhase.Review
            && pipeline.PhaseOutputs.TryGetValue($"tester-{pipeline.Iteration}", out var testerOut)
            && !string.IsNullOrWhiteSpace(testerOut))
        {
            const int maxTesterOutputChars = 2000;
            currentTestResults = testerOut.Length > maxTesterOutputChars
                ? testerOut[..maxTesterOutputChars] + "..."
                : testerOut;
        }
        else
        {
            currentTestResults = "";
        }

        // Include docwriting-preceded-review guidance inline when relevant
        var docWritingNote = (phase == GoalPhase.Review && docWritingPrecededReview)
            ? "\nNote: The docwriting phase already ran before this review. Changes to CHANGELOG.md, README.md, and XML doc comments are EXPECTED and should NOT be flagged as scope violations."
            : "";

        return $$"""
            Craft a prompt for the {{roleName}} worker.

            Goal: {{pipeline.GoalId}} (iteration {{pipeline.Iteration}}, phase {{phase}})
            Target repositories: {{string.Join(", ", pipeline.Goal.RepositoryNames)}}
            {{phaseInstructions}}
            {{(previousFeedback.Length > 0 ? $"\n{previousFeedback}" : "")}}
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}
            {{(currentTestResults.Length > 0 ? $"\nCurrent iteration test results (from the tester phase):\n{currentTestResults}" : "")}}
            {{docWritingNote}}

            The worker has access to project skills (e.g. build, test) that describe how to build and test this project.
            Tell the worker to use those skills instead of hardcoding framework-specific commands.

            Respond with ONLY the prompt text — no JSON, no markdown wrapping.
            If you need clarification that cannot be determined from the codebase, call escalate_to_composer instead.
            Use the get_goal tool if you need the full goal description.
            """;
    }

    /// <summary>
    /// Builds a direct worker prompt for the Review phase when the Brain agent is not connected.
    /// Includes tester output and reviewer guidance so reviewers always get test results
    /// and the "verify that all tests pass" instruction, regardless of agent availability.
    /// </summary>
    /// <param name="pipeline">The goal pipeline with phase outputs and iteration state.</param>
    /// <param name="additionalContext">Optional additional context to include.</param>
    /// <returns>A reviewer-ready fallback prompt containing test results and guidance.</returns>
    internal static string BuildReviewFallbackPrompt(GoalPipeline pipeline, string? additionalContext = null)
    {
        var currentTestResults = (pipeline.PhaseOutputs.TryGetValue($"tester-{pipeline.Iteration}", out var testerOut)
            && !string.IsNullOrWhiteSpace(testerOut))
            ? testerOut
            : "";

        return $$"""
            Review the changes for: {{pipeline.Description}}

            Use the diff commands from your workspace context to review the branch changes.
            Focus only on the diff lines (+ and -), then call the report_review_verdict tool when done.

            - "Files to change" in the goal is GUIDANCE, not an exhaustive whitelist. Test files and test changes that cover the modified code are ALWAYS acceptable and expected.
            - "Files NOT to change" in the goal IS a strict prohibition — flag any changes to those files as MAJOR.
            - The goal description defines WHAT to do. New behavior described in the goal is IN SCOPE — do not reject changes just because the base branch doesn't have them yet.
            - Only flag issues that are clearly bugs, security problems, or genuine scope violations (touching unrelated code/features).
            - Use the testing phase results to verify that all tests pass — do NOT reject because you cannot run tests yourself.
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}
            {{(currentTestResults.Length > 0 ? $"\nCurrent iteration test results (from the tester phase):\n{currentTestResults}" : "")}}
            """;
    }

    /// <summary>
    /// Asks the Brain to craft a prompt for the specified phase's worker.
    /// Returns a <see cref="PromptResult"/> that either contains the crafted prompt or an escalation request.
    /// </summary>
    public async Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        var prompt = BuildCraftPromptText(pipeline, phase, additionalContext);

        if (_agent is null)
        {
            _logger.LogWarning("Brain not connected — using fallback prompt for {GoalId} phase {Phase}",
                pipeline.GoalId, phase);
            return phase == GoalPhase.Review
                ? PromptResult.Success(BuildReviewFallbackPrompt(pipeline, additionalContext))
                : PromptResult.Success($"Work on: {pipeline.Description}");
        }

        try
        {
            var craftedPrompt = await Shared.CopilotRetryPolicy.ExecuteAsync(
                async () =>
                {
                    _logger.LogDebug("Brain craft-prompt request for {GoalId} (phase={Phase}):\n{Prompt}",
                        pipeline.GoalId, phase, Truncate(prompt, Constants.TruncationVerbose));

                    var (response, toolCall) = await ExecuteBrainAsync(prompt, ct);

                    _logger.LogDebug("Brain craft-prompt response for {GoalId}:\n{Response}",
                        pipeline.GoalId, Truncate(response, Constants.TruncationVerbose));

                    pipeline.Conversation.Add(new ConversationEntry("user", prompt, pipeline.Iteration, "craft-prompt"));
                    pipeline.Conversation.Add(new ConversationEntry("assistant", response, pipeline.Iteration, "craft-prompt"));

                    // Check for escalate_to_composer tool call
                    if (toolCall is { ToolName: "escalate_to_composer" })
                    {
                        var escalationQuestion = GetStringArg(toolCall.Arguments, "question") ?? "Brain requested clarification during prompt crafting";
                        var escalationReason = GetStringArg(toolCall.Arguments, "reason") ?? "Brain requested escalation";
                        _logger.LogInformation(
                            "Brain escalated prompt crafting for {GoalId} phase {Phase}: {Reason}",
                            pipeline.GoalId, phase, escalationReason);
                        // Return a sentinel that signals escalation — the caller unwraps it
                        return $"__ESCALATION__{escalationQuestion}\x00{escalationReason}";
                    }

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
                    pipeline.Conversation.Add(new ConversationEntry("system", $"Error on attempt {attempt}: {ex.Message}", pipeline.Iteration, "error"));
                },
                ct);

            // Unwrap escalation sentinel
            if (craftedPrompt.StartsWith("__ESCALATION__", StringComparison.Ordinal))
            {
                var payload = craftedPrompt["__ESCALATION__".Length..];
                var sepIdx = payload.IndexOf('\x00');
                var question = sepIdx >= 0 ? payload[..sepIdx] : payload;
                var reason = sepIdx >= 0 ? payload[(sepIdx + 1)..] : string.Empty;
                return PromptResult.Escalated(question, reason);
            }

            return PromptResult.Success(craftedPrompt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain failed to craft prompt for {GoalId} phase {Phase} — using fallback",
                pipeline.GoalId, phase);
            pipeline.Conversation.Add(new ConversationEntry("system", $"CraftPrompt error: {ex.Message}", pipeline.Iteration, "error"));
            return PromptResult.Success($"Work on: {pipeline.Description}");
        }
    }

    /// <inheritdoc />
    public async Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default)
    {
        if (_agent is null)
        {
            _logger.LogWarning(
                "Brain not connected — returning direct fallback answer for question in goal {GoalId}", goalId);
            return BrainResponse.Answer("Brain is not available. Please proceed with your best judgment.");
        }

        var prompt = $"""
            A worker ({workerRole}) in goal {goalId} (iteration {iteration}, phase {phase}) has a question:

            {question}

            If you can answer this from the codebase and project context, respond with ONLY the answer text.
            If the question requires domain knowledge, business context, or a decision that cannot be
            determined from the codebase alone, call the escalate_to_composer tool with the question and reason.
            """;

        try
        {
            var (response, toolCall) = await Shared.CopilotRetryPolicy.ExecuteAsync(
                () => ExecuteBrainAsync(prompt, ct),
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain AskQuestion call failed (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                },
                ct);

            if (toolCall is { ToolName: "escalate_to_composer" })
            {
                var escalationQuestion = GetStringArg(toolCall.Arguments, "question") ?? question;
                var escalationReason = GetStringArg(toolCall.Arguments, "reason") ?? "Brain requested escalation";
                _logger.LogInformation(
                    "Brain escalated question for goal {GoalId} via tool call: {Reason}", goalId, escalationReason);
                return BrainResponse.Escalated(escalationQuestion, escalationReason);
            }

            return BrainResponse.Answer(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain AskQuestionAsync failed for goal {GoalId} — returning fallback", goalId);
            return BrainResponse.Answer("Brain encountered an error. Please proceed with your best judgment.");
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

    /// <inheritdoc />
    public async Task ResetSessionAsync(CancellationToken ct = default)
    {
        await _brainCallGate.WaitAsync(ct);
        try
        {
            // Re-read orchestrator instructions from disk so the system prompt
            // reflects the latest orchestrator.agents.md content.
            var freshInstructions = _agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
            _systemPrompt = string.IsNullOrWhiteSpace(freshInstructions)
                ? DefaultSystemPrompt
                : $"{DefaultSystemPrompt}\n\n{freshInstructions}";

            _session = AgentSession.Create("brain");
            RecreateAgent();

            var sessionFile = GetSessionFilePath();
            if (File.Exists(sessionFile))
                File.Delete(sessionFile);

            _logger.LogInformation("Brain session reset — conversation history cleared, orchestrator instructions reloaded from disk, and session file deleted.");
        }
        finally
        {
            _brainCallGate.Release();
        }
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
