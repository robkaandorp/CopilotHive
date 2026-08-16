using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using CopilotHive.Configuration;
using CopilotHive.Goals;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Services;

/// <summary>
/// Monitors CI status for a goal's merge commit via the GitHub API, publishes
/// <see cref="EventType.CiSucceeded"/> / <see cref="EventType.CiFailed"/> events,
/// and creates issues from test failures.
/// </summary>
/// <remarks>
/// The public entry points are <c>virtual</c> so tests can substitute the monitor via a
/// subclass (rather than mocking a non-virtual member) when verifying how callers such as
/// <c>GoalLifecycleService</c> launch and isolate monitoring.
/// </remarks>
public class CiMonitorService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromSeconds(60);

    private static readonly Regex XUnitTestRegex = new(
        @"✗\s+([A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+)",
        RegexOptions.Compiled);
    private static readonly Regex DotnetTestRegex = new(
        @"Failed:\s+([A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+)",
        RegexOptions.Compiled);
    private static readonly Regex MstestTestRegex = new(
        @"Failed\s+([A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+)",
        RegexOptions.Compiled);

    private readonly IIssueStore? _issueStore;
    private readonly IEventBus? _eventBus;
    private readonly HiveConfigFile? _config;
    private readonly UserService? _userService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<CiMonitorService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan? _timeoutOverride;
    private readonly ConcurrentDictionary<string, bool> _inFlight = new();

    /// <summary>
    /// Initialises a new <see cref="CiMonitorService"/> with optional dependencies.
    /// All parameters are optional so the service can be registered via a DI factory
    /// using <c>GetService</c> for each dependency.
    /// </summary>
    /// <param name="issueStore">Optional issue store for creating CI failure issues.</param>
    /// <param name="eventBus">Optional event bus for publishing CI events.</param>
    /// <param name="config">Optional hive configuration.</param>
    /// <param name="userService">Optional user service for the GitHub OAuth token.</param>
    /// <param name="httpClientFactory">Optional HTTP client factory for GitHub API calls.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="pollInterval">Polling interval between check-run fetches; defaults to 30 seconds.</param>
    /// <param name="timeoutOverride">Optional CI timeout override for tests.</param>
    public CiMonitorService(
        IIssueStore? issueStore = null,
        IEventBus? eventBus = null,
        HiveConfigFile? config = null,
        UserService? userService = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<CiMonitorService>? logger = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeoutOverride = null)
    {
        _issueStore = issueStore;
        _eventBus = eventBus;
        _config = config;
        _userService = userService;
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<CiMonitorService>.Instance;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _timeoutOverride = timeoutOverride;
    }

    /// <summary>
    /// Monitors CI status for a single merge commit in a repository. Polls the GitHub
    /// check-runs API until all checks complete, then publishes a CI event and creates
    /// issues for any test failures.
    /// </summary>
    /// <param name="goalId">The goal whose merge commit is being monitored.</param>
    /// <param name="repoName">The repository name as configured in hive-config.yaml.</param>
    /// <param name="mergeCommitSha">The merge commit SHA to monitor.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task MonitorMergeAsync(string goalId, string repoName, string mergeCommitSha, CancellationToken ct)
    {
        if (_config is null || _httpClientFactory is null || _eventBus is null)
        {
            _logger.LogWarning(
                "CI monitoring skipped for goal {GoalId} repo {Repo}: missing config, HTTP client factory, or event bus",
                goalId, repoName);
            return;
        }

        var repoConfig = _config.Repositories.FirstOrDefault(
            r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase));
        if (repoConfig is null || !repoConfig.MonitorCi)
        {
            _logger.LogDebug(
                "CI monitoring skipped for goal {GoalId} repo {Repo}: repository not configured or MonitorCi=false",
                goalId, repoName);
            return;
        }

        var inFlightKey = $"{goalId}:{mergeCommitSha}";
        if (!_inFlight.TryAdd(inFlightKey, true))
        {
            _logger.LogDebug("CI monitoring already in flight for goal {GoalId} commit {Sha}", goalId, mergeCommitSha);
            return;
        }

        try
        {
            // Resolve the GitHub token: user-service OAuth token first, then environment variables.
            string? token = null;
            if (_userService is not null)
            {
                try
                {
                    token = await _userService.GetActiveAccessTokenAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get active access token for CI monitoring — falling back to environment");
                }
            }
            token ??= Environment.GetEnvironmentVariable("GH_TOKEN")
                      ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("No GitHub token available for CI monitoring of goal {GoalId} repo {Repo}", goalId, repoName);
                return;
            }

            // Parse owner/repo from the repository URL.
            if (string.IsNullOrWhiteSpace(repoConfig.Url) || !repoConfig.Url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Repository '{Repo}' URL '{Url}' is not a GitHub repository — CI monitoring not supported",
                    repoName, repoConfig.Url);
                return;
            }
            if (!TryParseGitHubRepo(repoConfig.Url, out var owner, out var repo))
            {
                _logger.LogWarning(
                    "Malformed GitHub repository URL '{Url}' for repo '{Repo}' — CI monitoring skipped",
                    repoConfig.Url, repoName);
                return;
            }

            // Timeout: linked token for all HTTP calls and delays.
            var timeout = _timeoutOverride ?? TimeSpan.FromMinutes(repoConfig.CiTimeoutMinutes);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var linkedToken = linkedCts.Token;

            var client = _httpClientFactory.CreateClient("github-api");

            // Why the loop terminated. Determined at the point the linked token throws so the
            // caller token is inspected while it is still the authoritative signal — a CI timeout
            // firing microseconds later must not be mistaken for the cause of a caller abort.
            // Stays None when the loop is left by an explicit `return`, in which case the
            // post-loop classification must never run.
            var cancelCause = CancelCause.None;

            while (true)
            {
                FetchResult fetchResult;
                try
                {
                    fetchResult = await FetchCheckRunsAsync(client, owner, repo, mergeCommitSha, token, linkedToken);
                }
                catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
                {
                    cancelCause = ClassifyCancellation(ct);
                    break;
                }
                if (fetchResult.Outcome == FetchOutcome.Return)
                    return;

                if (fetchResult.CheckRuns is { Count: > 0 } checkRuns)
                {
                    if (checkRuns.Any(r => !string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Some checks still running — continue polling.
                    }
                    else
                    {
                        // All checks completed — classify by conclusion.
                        var failedRuns = new List<CheckRunData>();
                        foreach (var run in checkRuns)
                        {
                            if (string.Equals(run.Conclusion, "skipped", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (IsFailConclusion(run.Conclusion))
                                failedRuns.Add(run);
                        }

                        // Caller cancellation is authoritative and is checked BEFORE any terminal
                        // publication: a cancellation racing a completed response must produce no
                        // event on either the success or the failure path.
                        if (ct.IsCancellationRequested)
                        {
                            LogCallerCancellation(goalId, repoName, mergeCommitSha);
                            return;
                        }

                        if (failedRuns.Count == 0)
                        {
                            _logger.LogInformation(
                                "CI passed for goal {GoalId} repo {Repo} commit {Sha} ({CheckCount} checks)",
                                goalId, repoName, mergeCommitSha, checkRuns.Count);
                            _eventBus.Publish(new SystemEvent(
                                Type: EventType.CiSucceeded,
                                Message: $"All {checkRuns.Count} checks passed",
                                GoalId: goalId,
                                Repository: repoName));
                            return;
                        }

                        _logger.LogWarning(
                            "CI failed for goal {GoalId} repo {Repo} commit {Sha}: {FailedCount} check(s) failed",
                            goalId, repoName, mergeCommitSha, failedRuns.Count);
                        try
                        {
                            await HandleCiFailureAsync(goalId, repoName, mergeCommitSha, failedRuns, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // Caller cancelled while issues were being created. Cancellation wins:
                            // no CiFailed event is published.
                            LogCallerCancellation(goalId, repoName, mergeCommitSha);
                        }
                        return;
                    }
                }
                // else: zero check runs → pending, continue polling.

                // Delay between polling iterations.
                try
                {
                    await Task.Delay(_pollInterval, linkedToken);
                }
                catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
                {
                    cancelCause = ClassifyCancellation(ct);
                    break;
                }
            }

            // Only reachable after the loop broke on cancellation; every normal termination
            // returns from inside the loop, so CancelCause.None here is a contract violation.
            switch (cancelCause)
            {
                case CancelCause.Caller:
                    LogCallerCancellation(goalId, repoName, mergeCommitSha);
                    return;
                case CancelCause.Timeout:
                    _logger.LogWarning(
                        "CI monitoring timed out after {Timeout} for goal {GoalId} repo {Repo} commit {Sha}",
                        timeout, goalId, repoName, mergeCommitSha);
                    return;
                case CancelCause.None:
                    throw new InvalidOperationException(
                        $"CI monitoring loop for goal '{goalId}' repo '{repoName}' exited without a cancellation cause.");
                default:
                    throw new InvalidOperationException($"Unhandled cancellation cause '{cancelCause}'.");
            }
        }
        finally
        {
            _inFlight.TryRemove(inFlightKey, out _);
        }
    }

    /// <summary>
    /// Monitors CI for a goal across multiple repositories. The merge commit hashes are
    /// comma-separated and zipped with the repository names; each pair is monitored
    /// concurrently with its own per-repository CI timeout.
    /// </summary>
    /// <param name="goalId">The goal whose merge commits are being monitored.</param>
    /// <param name="commaSeparatedHashes">Comma-separated merge commit SHAs, one per repository.</param>
    /// <param name="repoNames">Repository names, one per merge commit SHA.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task MonitorGoalAsync(string goalId, string commaSeparatedHashes, List<string> repoNames, CancellationToken ct)
    {
        var hashes = commaSeparatedHashes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var count = Math.Min(hashes.Count, repoNames.Count);
        if (hashes.Count != repoNames.Count)
        {
            _logger.LogWarning(
                "Goal {GoalId} has {HashCount} merge hashes but {RepoCount} repository names — monitoring the first {Count} pairs",
                goalId, hashes.Count, repoNames.Count, count);
        }

        var tasks = new List<Task>(count);
        for (var i = 0; i < count; i++)
        {
            var hash = hashes[i];
            var repo = repoNames[i];
            tasks.Add(MonitorPairAsync(goalId, repo, hash));
        }

        await Task.WhenAll(tasks);

        async Task MonitorPairAsync(string gid, string rname, string sha)
        {
            try
            {
                await MonitorMergeAsync(gid, rname, sha, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CI monitoring failed for goal {GoalId} repo {Repo} commit {Sha}", gid, rname, sha);
            }
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>Why the polling loop stopped, when it stopped because a token fired.</summary>
    private enum CancelCause
    {
        /// <summary>The loop terminated normally (via an explicit <c>return</c>), not by cancellation.</summary>
        None,
        /// <summary>The caller's token was cancelled — terminal, no event.</summary>
        Caller,
        /// <summary>The CI timeout elapsed — terminal, no event.</summary>
        Timeout,
    }

    /// <summary>
    /// Classifies why the linked token fired. The caller token is inspected first and wins:
    /// when both the caller token and the CI timeout are signalled, the caller's intent is
    /// authoritative. Called at the throw site so the caller token is read as close as
    /// possible to the moment it was observed.
    /// </summary>
    private static CancelCause ClassifyCancellation(CancellationToken ct) =>
        ct.IsCancellationRequested ? CancelCause.Caller : CancelCause.Timeout;

    private void LogCallerCancellation(string goalId, string repoName, string sha) =>
        _logger.LogInformation(
            "CI monitoring cancelled for goal {GoalId} repo {Repo} commit {Sha}", goalId, repoName, sha);

    private enum FetchOutcome { Continue, Return }

    private sealed record FetchResult(FetchOutcome Outcome, List<CheckRunData>? CheckRuns);

    private sealed record CheckRunData(
        string Name,
        string Status,
        string? Conclusion,
        string? HtmlUrl,
        string? Summary,
        string? Text);

    private static bool IsFailConclusion(string? conclusion)
    {
        if (string.IsNullOrEmpty(conclusion))
            return true; // null/unknown conclusion → treat as fail

        return conclusion.ToLowerInvariant() switch
        {
            "success" or "neutral" => false,
            "failure" or "cancelled" or "timed_out" or "action_required" or "startup_failure" or "stale" => true,
            "skipped" => false, // handled by the caller (ignored)
            _ => true, // unknown conclusion → treat as fail
        };
    }

    private static bool TryParseGitHubRepo(string url, out string owner, out string repo)
    {
        owner = "";
        repo = "";

        if (string.IsNullOrWhiteSpace(url) || !url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        string path;
        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = url["git@github.com:".Length..];
        }
        else if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            path = url["https://github.com/".Length..];
        }
        else
        {
            return false;
        }

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        owner = parts[0];
        repo = parts[1];
        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
    }

    private async Task<FetchResult> FetchCheckRunsAsync(
        HttpClient client, string owner, string repo, string sha, string token, CancellationToken linkedToken)
    {
        var allRuns = new List<CheckRunData>();
        var totalCount = 0;
        var page = 1;

        while (true)
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100&page={page}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, linkedToken);
            }
            catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
            {
                throw; // cancellation — propagate to the polling loop
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transport error fetching check runs for {Owner}/{Repo} commit {Sha} — will retry", owner, repo, sha);
                return new FetchResult(FetchOutcome.Continue, null);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("GitHub API returned 401 for {Owner}/{Repo} commit {Sha} — token invalid or expired", owner, repo, sha);
                    return new FetchResult(FetchOutcome.Return, null);
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var rateLimitRemaining = GetHeaderValue(response, "X-RateLimit-Remaining");
                    if (string.Equals(rateLimitRemaining, "0", StringComparison.Ordinal))
                    {
                        var retryAfter = ParseRetryAfter(response) ?? DefaultRateLimitWait;
                        _logger.LogWarning("GitHub API rate limit exceeded for {Owner}/{Repo} — waiting {Seconds}s", owner, repo, retryAfter.TotalSeconds);
                        try { await Task.Delay(retryAfter, linkedToken); }
                        catch (OperationCanceledException) when (linkedToken.IsCancellationRequested) { throw; }
                        return new FetchResult(FetchOutcome.Continue, null);
                    }
                    _logger.LogWarning("GitHub API returned 403 for {Owner}/{Repo} commit {Sha}", owner, repo, sha);
                    return new FetchResult(FetchOutcome.Return, null);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("GitHub API returned 404 for {Owner}/{Repo} commit {Sha} — commit or repository not found", owner, repo, sha);
                    return new FetchResult(FetchOutcome.Return, null);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = ParseRetryAfter(response) ?? DefaultRateLimitWait;
                    _logger.LogWarning("GitHub API returned 429 for {Owner}/{Repo} — waiting {Seconds}s", owner, repo, retryAfter.TotalSeconds);
                    try { await Task.Delay(retryAfter, linkedToken); }
                    catch (OperationCanceledException) when (linkedToken.IsCancellationRequested) { throw; }
                    return new FetchResult(FetchOutcome.Continue, null);
                }

                if ((int)response.StatusCode >= 500)
                {
                    _logger.LogWarning("GitHub API returned {Status} for {Owner}/{Repo} commit {Sha} — will retry", (int)response.StatusCode, owner, repo, sha);
                    return new FetchResult(FetchOutcome.Continue, null);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("GitHub API returned {Status} for {Owner}/{Repo} commit {Sha} — will retry", (int)response.StatusCode, owner, repo, sha);
                    return new FetchResult(FetchOutcome.Continue, null);
                }

                // Parse JSON.
                JsonDocument doc;
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(linkedToken);
                    doc = JsonDocument.Parse(stream);
                }
                catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Malformed JSON from GitHub API for {Owner}/{Repo} commit {Sha} — will retry", owner, repo, sha);
                    return new FetchResult(FetchOutcome.Continue, null);
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("total_count", out var tc) && tc.ValueKind == JsonValueKind.Number)
                        totalCount = tc.GetInt32();

                    if (root.TryGetProperty("check_runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var run in runs.EnumerateArray())
                            allRuns.Add(ParseCheckRun(run));
                    }
                }
            }

            // Pagination: if total_count > 100, fetch pages 2..N.
            var totalPages = (int)Math.Ceiling(totalCount / 100.0);
            if (page >= totalPages)
                break;
            page++;
        }

        return new FetchResult(FetchOutcome.Continue, allRuns);
    }

    private static CheckRunData ParseCheckRun(JsonElement run)
    {
        var name = run.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? ""
            : "";
        var status = run.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? ""
            : "";
        string? conclusion = null;
        if (run.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String)
            conclusion = c.GetString();
        string? htmlUrl = null;
        if (run.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String)
            htmlUrl = h.GetString();

        string? summary = null;
        string? text = null;
        if (run.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
        {
            if (output.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String)
                summary = sm.GetString();
            if (output.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                text = tx.GetString();
        }

        return new CheckRunData(name, status, conclusion, htmlUrl, summary, text);
    }

    private async Task HandleCiFailureAsync(
        string goalId, string repoName, string sha, List<CheckRunData> failedRuns, CancellationToken ct)
    {
        var created = 0;
        var updated = 0;

        if (_issueStore is not null)
        {
            try
            {
                // Parse test names from all failed check runs.
                var testNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var run in failedRuns)
                {
                    foreach (var name in ParseTestNames(CombineOutput(run)))
                        testNames.Add(name);
                }

                // Create one issue per unique test name.
                foreach (var testName in testNames)
                {
                    var run = failedRuns.FirstOrDefault(r => ContainsTestName(CombineOutput(r), testName)) ?? failedRuns[0];
                    var title = $"CI failure: {testName}";
                    var (c, u) = await CreateOrUpdateIssueAsync(goalId, repoName, sha, title, run, ct);
                    created += c;
                    updated += u;
                }

                // Fallback: one issue per failed check run with no parseable test names.
                foreach (var run in failedRuns)
                {
                    if (ParseTestNames(CombineOutput(run)).Count == 0)
                    {
                        var title = string.IsNullOrWhiteSpace(run.Name)
                            ? "CI failure: unknown check"
                            : $"CI failure: {run.Name}";
                        var (c, u) = await CreateOrUpdateIssueAsync(goalId, repoName, sha, title, run, ct);
                        created += c;
                        updated += u;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation is NOT an issue-store failure: it must abort the whole
                // failure path with no CiFailed event. Rethrow before the generic handler so
                // it can never be swallowed and re-classified as a store error.
                throw;
            }
            catch (Exception ex)
            {
                // A genuine issue-store failure must never suppress the CiFailed event.
                _logger.LogError(ex, "Failed to create CI failure issues for goal {GoalId} repo {Repo}", goalId, repoName);
            }
        }
        else
        {
            _logger.LogInformation("No issue store available — skipping CI failure issue creation for goal {GoalId} repo {Repo}", goalId, repoName);
        }

        // Final caller-cancellation gate: a cancellation that lands after issue creation must
        // still suppress the event. Throwing (rather than returning) keeps a single cancellation
        // path — the caller logs it and returns without publishing.
        ct.ThrowIfCancellationRequested();

        // Guarantee CiFailed publication regardless of issue-store success.
        _eventBus!.Publish(new SystemEvent(
            Type: EventType.CiFailed,
            Message: $"{failedRuns.Count} check(s) failed; created {created} issue(s), updated {updated} issue(s)",
            GoalId: goalId,
            Repository: repoName));
    }

    private async Task<(int Created, int Updated)> CreateOrUpdateIssueAsync(
        string goalId, string repoName, string sha, string title, CheckRunData run, CancellationToken ct)
    {
        var issues = await _issueStore!.GetIssuesAsync(repository: repoName, ct: ct);
        var existing = issues.FirstOrDefault(i =>
            (i.Status == IssueStatus.Open || i.Status == IssueStatus.Triaged) &&
            string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.SourceGoalId, goalId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Description += BuildDedupAppend(run);
            await _issueStore.UpdateIssueAsync(existing, ct);
            return (0, 1);
        }

        var id = await IssueIdGenerator.GenerateAsync(title, _issueStore, ct);
        var issue = new Issue
        {
            Id = id,
            Type = IssueType.Bug,
            Title = title,
            Description = BuildIssueDescription(goalId, sha, run),
            Severity = IssueSeverity.High,
            Status = IssueStatus.Open,
            RepositoryNames = [repoName],
            SourceGoalId = goalId,
            SourceRole = "ci",
        };

        try
        {
            await _issueStore.CreateIssueAsync(issue, ct);
        }
        catch (InvalidOperationException)
        {
            // Duplicate ID race — retry with a GUID-based ID, preserving all fields.
            var retry = new Issue
            {
                Id = $"issue-{Guid.NewGuid():N}",
                Type = issue.Type,
                Title = issue.Title,
                Description = issue.Description,
                Severity = issue.Severity,
                Status = issue.Status,
                RepositoryNames = issue.RepositoryNames,
                SourceGoalId = issue.SourceGoalId,
                SourceRole = issue.SourceRole,
            };
            await _issueStore.CreateIssueAsync(retry, ct);
        }

        return (1, 0);
    }

    private static List<string> ParseTestNames(string output)
    {
        var names = new List<string>();
        foreach (Match match in XUnitTestRegex.Matches(output))
            names.Add(match.Groups[1].Value);
        foreach (Match match in DotnetTestRegex.Matches(output))
            names.Add(match.Groups[1].Value);
        foreach (Match match in MstestTestRegex.Matches(output))
            names.Add(match.Groups[1].Value);
        return names;
    }

    private static bool ContainsTestName(string output, string testName) =>
        output.Contains(testName, StringComparison.OrdinalIgnoreCase);

    private static string CombineOutput(CheckRunData run)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(run.Summary))
            parts.Add(run.Summary);
        if (!string.IsNullOrWhiteSpace(run.Text))
            parts.Add(run.Text);
        return string.Join("\n\n", parts);
    }

    private static string BuildIssueDescription(string goalId, string sha, CheckRunData run)
    {
        var errorOutput = CombineOutput(run);
        var htmlUrl = string.IsNullOrWhiteSpace(run.HtmlUrl) ? "(no URL)" : run.HtmlUrl;
        return $"CI failed for goal '{goalId}' (commit {sha}).\n\n{errorOutput}\n\nCI run: {htmlUrl}";
    }

    private static string BuildDedupAppend(CheckRunData run)
    {
        var errorOutput = CombineOutput(run);
        var htmlUrl = string.IsNullOrWhiteSpace(run.HtmlUrl) ? "(no URL)" : run.HtmlUrl;
        return $"\n\n---\n[Updated {DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}]\n{errorOutput}\nCI run: {htmlUrl}";
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            return values.FirstOrDefault();
        return null;
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var value = GetHeaderValue(response, "Retry-After");
        if (value is null)
            return null;

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(Math.Max(0, seconds));

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}
