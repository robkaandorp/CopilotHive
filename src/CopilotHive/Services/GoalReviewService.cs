using System.Collections.Concurrent;
using System.Text.Json;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Shared.AI;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SharpCoder;

namespace CopilotHive.Services;

/// <summary>
/// Outcome of a pre-execution goal review.
/// </summary>
/// <param name="Verdict">Either "Approved" or "NeedsChanges".</param>
/// <param name="Issues">Human-readable summary of issues found (newline-separated), or a "no issues" message.</param>
/// <param name="Summary">Recommendation / summary of what should change.</param>
public sealed record ReviewResult(string Verdict, string Issues, string Summary);

/// <summary>
/// Performs pre-execution goal reviews. Creates a temporary, read-only <see cref="CodingAgent"/>
/// with the reviewer model, provides it with the goal description and linked knowledge documents,
/// captures the verdict, and persists a review document in the knowledge graph.
/// All dependencies except <c>logger</c> and <c>stateDir</c> are optional, mirroring
/// <see cref="PipelineDriver"/>.
/// </summary>
public class GoalReviewService
{
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly ConfigRepoManager? _configRepo;
    private readonly HiveConfigFile? _config;
    private readonly IGoalStore? _goalStore;
    private readonly IBrainRepoManager? _brainRepoManager;
    private readonly string _stateDir;
    private readonly ILogger<GoalReviewService> _logger;
    private readonly LlmSessionRegistry? _sessionRegistry;

    // Test seam: factory that produces the chat client used by the review agent.
    private readonly Func<string, IChatClient> _chatClientFactory;

    // Service-level in-process concurrency guard, keyed by goal ID. Prevents two concurrent
    // callers (which may hold separate detached Goal instances for the same ID) from both
    // launching a reviewer agent for the same goal.
    private readonly ConcurrentDictionary<string, byte> _reviewsInProgress = new();

    /// <summary>
    /// Initialises a new <see cref="GoalReviewService"/>.
    /// </summary>
    public GoalReviewService(
        KnowledgeGraph? knowledgeGraph,
        ConfigRepoManager? configRepo,
        HiveConfigFile? config,
        IGoalStore? goalStore,
        IBrainRepoManager? brainRepoManager,
        string stateDir,
        ILogger<GoalReviewService> logger,
        Func<string, IChatClient>? chatClientFactory = null,
        LlmSessionRegistry? sessionRegistry = null)
    {
        _knowledgeGraph = knowledgeGraph;
        _configRepo = configRepo;
        _config = config;
        _goalStore = goalStore;
        _brainRepoManager = brainRepoManager;
        _stateDir = stateDir;
        _logger = logger;
        _chatClientFactory = chatClientFactory ?? (model => ChatClientFactory.Create(model));
        _sessionRegistry = sessionRegistry;
    }

    /// <summary>
    /// Reviews a goal before it is dispatched for execution. Creates a temporary read-only agent
    /// with the reviewer model, feeds it the goal description and linked knowledge documents,
    /// parses the verdict, updates <see cref="Goal.ReviewStatus"/>, and persists a review document.
    /// </summary>
    /// <param name="goal">The goal to review.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The review result (verdict, issues, summary).</returns>
    /// <exception cref="InvalidOperationException">Thrown when a review is already in progress for the goal.</exception>
    public async Task<ReviewResult> ReviewGoalAsync(Goal goal, CancellationToken ct)
    {
        // Service-level in-process concurrency guard: acquire the lock before doing anything else.
        // Released in the outer finally on every exit path.
        if (!_reviewsInProgress.TryAdd(goal.Id, 0))
            throw new InvalidOperationException($"A review is already in progress for goal {goal.Id}");

        // Nullable so that if TryAdd had failed (throwing before this point), no unregister is
        // attempted in the outer finally. Only set once a review session has been registered.
        string? reviewSessionId = null;

        try
        {
            // In-memory guard: the passed-in instance already shows a review in progress.
            if (goal.ReviewStatus == ReviewStatus.Pending)
                throw new InvalidOperationException($"A review is already in progress for goal {goal.Id}");

            // Persisted-state guard: re-read the goal from the store to catch concurrent reviews
            // started by another caller holding a different (detached) instance.
            if (_goalStore is not null)
            {
                var persisted = await _goalStore.GetGoalAsync(goal.Id, ct);
                if (persisted is not null && persisted.ReviewStatus == ReviewStatus.Pending)
                    throw new InvalidOperationException($"A review is already in progress for goal {goal.Id}");
            }

            // Resolve the reviewer model and context window before registering the review session.
            var reviewerModel = _config?.GetModelForRole(WorkerRole.Reviewer) ?? Constants.DefaultWorkerModel;
            var maxContextTokens = _config?.TryGetContextWindowForModel(reviewerModel) ?? Constants.DefaultBrainContextWindow;

            // Register the review session now that the concurrency guards have passed. Rejected
            // concurrent reviews (which throw above) never reach this point, so no registration
            // happens for them.
            reviewSessionId = $"goal-review-{goal.Id}-{Guid.NewGuid():N}";
            _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
            {
                SessionId = reviewSessionId,
                SessionType = "GoalReview",
                GoalId = goal.Id,
                Model = reviewerModel,
                CurrentTokens = 0,
                MaxTokens = maxContextTokens,
                Status = "reviewing",
                LastActivity = DateTime.UtcNow,
            });

            // Everything after setting Pending is wrapped so any failure — including the initial
            // Pending persistence (database error, concurrency conflict, or caller cancellation
            // arriving during the call) — is handled and never leaves the goal stuck in Pending.
            IChatClient? chatClient = null;
            try
            {
                // Set Pending status and persist. Kept inside the try so a persistence failure
                // is recovered by the cancellation/general catch handlers below.
                goal.ReviewStatus = ReviewStatus.Pending;
                if (_goalStore is not null)
                    await _goalStore.UpdateGoalAsync(goal, ct);

                // Gather knowledge context.
                var knowledgeContext = string.Empty;
                if (_knowledgeGraph is not null && goal.Documents.Count > 0)
                {
                    foreach (var docId in goal.Documents)
                    {
                        try
                        {
                            var doc = _knowledgeGraph.GetDocument(docId);
                            if (doc is not null)
                                knowledgeContext += $"## {doc.Title}\n\n{doc.Content}\n\n";
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to load knowledge document {DocId} for goal {GoalId}", docId, goal.Id);
                        }
                    }
                }

                // Build the review prompt.
                var reviewPrompt = BuildReviewPrompt(goal, knowledgeContext);

                // Create the chat client (may throw if the provider/model is misconfigured).
                chatClient = _chatClientFactory(reviewerModel);

                var options = new AgentOptions
                {
                    WorkDirectory = _brainRepoManager?.WorkDirectory ?? _stateDir,
                    EnableBash = false,
                    EnableFileWrites = false,
                    EnableFileOps = true,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                    MaxContextTokens = maxContextTokens,
                    SystemPrompt = BuildReviewerSystemPrompt(),
                };

                var agent = new CodingAgent(chatClient, options);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(5));
                var result = await agent.ExecuteAsync(reviewPrompt, cts.Token);

                // Parse the verdict, issues, and verified items.
                var (verdict, issues, verified, summary) = ParseReviewResult(result.Message);

                // Update and persist the goal's review status.
                goal.ReviewStatus = verdict == "Approved" ? ReviewStatus.Approved : ReviewStatus.NeedsChanges;
                if (_goalStore is not null)
                    await _goalStore.UpdateGoalAsync(goal, ct);

                // Persist the review document (issues + verified sections).
                await CreateOrUpdateReviewDocumentAsync(goal, verdict, issues, verified, summary, ct);

                return new ReviewResult(verdict, JoinIssues(issues), summary);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation (as opposed to the internal five-minute timeout).
                // Reset the persisted status using a non-cancelled token, then propagate.
                _logger.LogWarning("Goal review cancelled by caller for goal {GoalId}", goal.Id);

                goal.ReviewStatus = ReviewStatus.NeedsChanges;
                if (_goalStore is not null)
                {
                    try
                    {
                        await _goalStore.UpdateGoalAsync(goal, CancellationToken.None);
                    }
                    catch (Exception persistEx)
                    {
                        _logger.LogWarning(persistEx, "Failed to reset review status after cancellation for goal {GoalId}", goal.Id);
                    }
                }

                throw;
            }
            catch (Exception ex)
            {
                // Agent failure OR the internal five-minute timeout: treat as a failed review.
                _logger.LogError(ex, "Goal review failed for goal {GoalId}", goal.Id);

                goal.ReviewStatus = ReviewStatus.NeedsChanges;

                var failIssues = $"Review failed: {ex.Message}";
                const string failSummary = "The review agent encountered an error and could not complete the review.";

                if (_goalStore is not null)
                {
                    try
                    {
                        await _goalStore.UpdateGoalAsync(goal, ct);
                    }
                    catch (Exception persistEx)
                    {
                        _logger.LogWarning(persistEx, "Failed to persist review status after review failure for goal {GoalId}", goal.Id);
                    }
                }

                // Best-effort: record the failure as a review document too, so a review-{goal.Id}
                // document always exists after a review attempt. A document failure must not mask
                // the original error.
                try
                {
                    var failIssueList = new List<ReviewIssue> { new("ERROR", failIssues) };
                    await CreateOrUpdateReviewDocumentAsync(goal, "NeedsChanges", failIssueList, [], failSummary, ct);
                }
                catch (Exception docEx)
                {
                    _logger.LogWarning(docEx, "Failed to persist failure review document for goal {GoalId}", goal.Id);
                }

                return new ReviewResult("NeedsChanges", failIssues, failSummary);
            }
            finally
            {
                chatClient?.Dispose();
            }
        }
        finally
        {
            _reviewsInProgress.TryRemove(goal.Id, out _);
            if (reviewSessionId is not null)
                _sessionRegistry?.Unregister(reviewSessionId);
        }
    }

    /// <summary>Returns the system prompt for the read-only review agent.</summary>
    private static string BuildReviewerSystemPrompt() =>
        """
        You are a goal review agent. You review goal descriptions for correctness and completeness before they are dispatched.

        You have read-only access to the target repositories via file tools (read_file, glob, grep).
        Use these to verify that file paths, class names, method names, and field names mentioned in the goal description actually exist in the codebase.

        You cannot modify files. You cannot run bash commands. You can only read files and search the codebase.

        After reviewing, respond with ONLY the JSON result object. No other text.
        """;

    /// <summary>Builds the review prompt from the goal description and linked knowledge context.</summary>
    private static string BuildReviewPrompt(Goal goal, string knowledgeContext)
    {
        var documents = string.IsNullOrWhiteSpace(knowledgeContext) ? "No linked documents." : knowledgeContext;

        return $$"""
        You are reviewing a goal description before it is dispatched for execution.

        ## Goal Description
        {{goal.Description}}

        ## Linked Knowledge Documents
        {{documents}}

        ## Your Task
        Review this goal for:
        1. Feasibility — Can this be done in 1-3 iterations?
        2. Scope — Is the scope appropriate? Too large? Too small?
        3. Acceptance criteria — Clear, testable, complete?
        4. File references — Do the files exist? Use file tools to verify.
        5. Code references — Do the classes/methods/fields mentioned actually exist? Use file tools to verify.
        6. Dependencies — Are depends_on goals correctly ordered?
        7. Contradictions — Any internal contradictions?
        8. Missing context — Is there information the worker would need but isn't provided?
        9. Risk — Any risks (breaking changes, security, data loss)?

        ## Output Format
        Respond with a JSON object:
        {
          "verdict": "Approved" or "NeedsChanges",
          "issues": [
            { "severity": "CRITICAL"|"MAJOR"|"MINOR", "description": "..." }
          ],
          "verified": [
            { "item": "File/method/field name", "exists": true|false }
          ],
          "recommendation": "Summary of what should be changed"
        }
        """;
    }

    /// <summary>
    /// Parses the JSON review result from the agent's raw text response. Attempts to extract the
    /// JSON object embedded in any surrounding text. On failure, returns a NeedsChanges verdict
    /// describing the parse failure.
    /// </summary>
    private (string Verdict, List<ReviewIssue> Issues, List<ReviewVerified> Verified, string Summary) ParseReviewResult(string? rawOutput)
    {
        var raw = rawOutput ?? string.Empty;

        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end < 0 || end <= start)
                return ParseFailure(raw);

            var json = raw.Substring(start, end - start + 1);
            var parsed = JsonSerializer.Deserialize<ReviewJsonResult>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

            if (parsed is null)
                return ParseFailure(raw);

            var verdict = string.Equals(parsed.Verdict, "Approved", StringComparison.Ordinal)
                ? "Approved"
                : "NeedsChanges";

            var issues = parsed.Issues ?? [];
            var verified = parsed.Verified ?? [];

            var summary = string.IsNullOrWhiteSpace(parsed.Recommendation)
                ? "No recommendation provided."
                : parsed.Recommendation;

            return (verdict, issues, verified, summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse review response as JSON");
            return ParseFailure(raw);
        }
    }

    private static (string Verdict, List<ReviewIssue> Issues, List<ReviewVerified> Verified, string Summary) ParseFailure(string raw)
    {
        var truncated = raw.Length > Constants.TruncationMedium
            ? raw.Substring(0, Constants.TruncationMedium)
            : raw;

        return (
            "NeedsChanges",
            [new ReviewIssue("ERROR", "Failed to parse review response")],
            [],
            $"The review agent returned a response that could not be parsed as JSON. Raw output: {truncated}");
    }

    /// <summary>Renders the parsed issues as a single newline-joined string for the <see cref="ReviewResult"/>.</summary>
    private static string JoinIssues(IReadOnlyList<ReviewIssue> issues) =>
        issues.Count > 0
            ? string.Join("\n", issues.Select(i => $"[{i.Severity}] {i.Description}"))
            : "No issues found.";

    /// <summary>
    /// Creates or updates the review document for a goal in the knowledge graph and links it to the goal.
    /// Best-effort: failures are logged and swallowed so review persistence never blocks the pipeline.
    /// </summary>
    private async Task CreateOrUpdateReviewDocumentAsync(
        Goal goal,
        string verdict,
        IReadOnlyList<ReviewIssue> issues,
        IReadOnlyList<ReviewVerified> verified,
        string summary,
        CancellationToken ct)
    {
        if (_knowledgeGraph is null)
            return;

        var docId = $"review-{goal.Id}";

        var issuesMarkdown = issues.Count > 0
            ? string.Join("\n\n", issues.Select(i => $"### [{i.Severity}] {i.Description}"))
            : "No issues found.";

        var verifiedMarkdown = verified.Count > 0
            ? string.Join("\n", verified.Select(v => v.Exists ? $"- ✅ {v.Item} exists" : $"- ❌ {v.Item} does not exist"))
            : "No items verified.";

        var markdownContent =
            $"# Review: {goal.Id}\n\n" +
            $"## Verdict: {verdict}\n\n" +
            $"## Issues\n\n{issuesMarkdown}\n\n" +
            $"## Verified\n{verifiedMarkdown}\n\n" +
            $"## Recommendation\n{summary}\n";

        try
        {
            var existing = _knowledgeGraph.GetDocument(docId);
            if (existing is null)
            {
                await _knowledgeGraph.CreateDocumentAsync(
                    id: docId,
                    title: $"Review: {goal.Id}",
                    type: DocumentType.Scratch,
                    content: markdownContent,
                    topic: "review",
                    author: "reviewer",
                    ct: ct);
            }
            else
            {
                await _knowledgeGraph.UpdateDocumentAsync(docId, content: markdownContent, ct: ct);
            }

            if (!goal.Documents.Contains(docId))
            {
                goal.Documents.Add(docId);
                if (_goalStore is not null)
                    await _goalStore.UpdateGoalAsync(goal, ct);
            }

            if (_configRepo is not null)
                await _knowledgeGraph.CommitToConfigRepoAsync(_configRepo.LocalPath, $"Update review document: {docId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist review document for goal {GoalId}", goal.Id);
        }
    }

    private sealed record ReviewJsonResult(
        string Verdict,
        List<ReviewIssue> Issues,
        List<ReviewVerified> Verified,
        string Recommendation);

    private sealed record ReviewIssue(string Severity, string Description);

    private sealed record ReviewVerified(string Item, bool Exists);
}
