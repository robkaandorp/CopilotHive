using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using CopilotHive.Configuration;
using CopilotHive.Goals;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Services;

/// <summary>Outcome of a single CI probe against the GitHub check-runs API.</summary>
/// <param name="Status">Classification of the probe.</param>
/// <param name="CheckRuns">
/// The relevant check runs: all valid runs for <see cref="CiProbeStatus.Succeeded"/> and
/// <see cref="CiProbeStatus.StillRunning"/>, only the failed runs for
/// <see cref="CiProbeStatus.Failed"/>, and empty otherwise.
/// </param>
/// <param name="ErrorDetail">Error classification token when <paramref name="Status"/> is <see cref="CiProbeStatus.Error"/>.</param>
/// <param name="RetryAfter">Suggested delay before the next probe, when the error is retryable.</param>
internal sealed record CiProbeResult(
    CiProbeStatus Status,
    IReadOnlyList<CheckRunData> CheckRuns,
    string? ErrorDetail = null,
    TimeSpan? RetryAfter = null);

/// <summary>Classification of a single CI probe.</summary>
internal enum CiProbeStatus
{
    /// <summary>All non-skipped check runs completed with a passing conclusion.</summary>
    Succeeded,
    /// <summary>All check runs completed and at least one failed.</summary>
    Failed,
    /// <summary>At least one check run has not completed yet.</summary>
    StillRunning,
    /// <summary>The commit has no check runs yet.</summary>
    NoChecks,
    /// <summary>The probe could not be completed; see <see cref="CiProbeResult.ErrorDetail"/>.</summary>
    Error,
}

/// <summary>A single GitHub check run, reduced to the fields CI monitoring needs.</summary>
/// <param name="Name">The check-run name.</param>
/// <param name="Status">The check-run status (e.g. <c>queued</c>, <c>in_progress</c>, <c>completed</c>).</param>
/// <param name="Conclusion">The conclusion once completed, or <c>null</c>.</param>
/// <param name="HtmlUrl">Link to the check run on GitHub, or <c>null</c>.</param>
/// <param name="OutputSummary">The check run's output summary, or <c>null</c>.</param>
/// <param name="OutputText">The check run's output text, or <c>null</c>.</param>
/// <param name="DetailsUrl">The check run's <c>details_url</c> (used to locate the job log), or <c>null</c>.</param>
/// <param name="RunId">The GitHub Actions run ID parsed from <see cref="DetailsUrl"/>, or <c>null</c>.</param>
internal sealed record CheckRunData(
    string Name,
    string Status,
    string? Conclusion,
    string? HtmlUrl,
    string? OutputSummary,
    string? OutputText,
    string? DetailsUrl,
    string? RunId);

/// <summary>
/// Outcome of parsing CI log content: the extracted test failures and, when the log
/// cannot be parsed into failures, a sanitized snippet of the raw log for context.
/// </summary>
/// <param name="Failures">Test failures parsed from the log; empty when none could be parsed.</param>
/// <param name="FallbackSnippet">Sanitized raw-log snippet to use when <paramref name="Failures"/> is empty, or <c>null</c>.</param>
internal sealed record LogFetchResult(
    List<(string TestName, string Error, string StackTrace)> Failures,
    string? FallbackSnippet);

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
    private static readonly TimeSpan DefaultStartupScanWindow = TimeSpan.FromMinutes(60);

    /// <summary>Lowest epoch-seconds value <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts.</summary>
    private const long MinUnixSeconds = -62135596800L;

    /// <summary>Highest epoch-seconds value <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts.</summary>
    private const long MaxUnixSeconds = 253402300799L;

    /// <summary>
    /// How long after a merge a commit with zero check runs is still considered "CI may
    /// start any moment now". Older commits with no checks are assumed to have no CI at all.
    /// </summary>
    private static readonly TimeSpan NoChecksGracePeriod = TimeSpan.FromMinutes(5);

    private static readonly Regex XUnitTestRegex = new(
        @"✗\s+([A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+)",
        RegexOptions.Compiled);
    private static readonly Regex DotnetTestRegex = new(
        @"Failed:\s+([A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+)",
        RegexOptions.Compiled);
    private static readonly Regex MstestTestRegex = new(
        @"Failed\s+([A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches an xUnit failure header line: <c>Failed {name} [duration]</c>. The name is
    /// captured non-greedily up to the trailing duration bracket, so theory names containing
    /// spaces still parse. Multiline so headers are anchored to their own line, and leading
    /// whitespace is tolerated because console output indents failure lines.
    /// </summary>
    private static readonly Regex XUnitFailedTestRegex = new(
        @"^\s*Failed\s+(.+?)\s+\[\d+[^\]]*\]\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Extracts the GitHub Actions run ID from a check run's <c>details_url</c>.</summary>
    private static readonly Regex ActionsRunIdRegex = new(
        @"/actions/runs/(\d+)",
        RegexOptions.Compiled);

    /// <summary>Redacts <c>ghp_</c> GitHub personal-access tokens (36-character secrets).</summary>
    private static readonly Regex GhpTokenRegex = new(
        @"ghp_[A-Za-z0-9]{36}",
        RegexOptions.Compiled);

    /// <summary>Redacts <c>gho_</c> GitHub OAuth access tokens (36-character secrets).</summary>
    private static readonly Regex GhoTokenRegex = new(
        @"gho_[A-Za-z0-9]{36}",
        RegexOptions.Compiled);

    /// <summary>Redacts <c>ghs_</c> GitHub user-to-server tokens (36-character secrets).</summary>
    private static readonly Regex GhsTokenRegex = new(
        @"ghs_[A-Za-z0-9]{36}",
        RegexOptions.Compiled);

    /// <summary>Redacts HTTP <c>Bearer</c> authorization tokens (any base64-ish value).</summary>
    private static readonly Regex BearerTokenRegex = new(
        @"Bearer [A-Za-z0-9._\-]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Redacts secret-looking environment variables (names containing TOKEN, KEY, SECRET, or
    /// PASSWORD) while preserving the key name. Multiline (<c>RegexOptions.Multiline</c>) and
    /// case-sensitive so ordinary words such as <c>token</c> in prose are never touched.
    /// </summary>
    private static readonly Regex SecretEnvVarRegex = new(
        @"^([A-Z_]*(TOKEN|KEY|SECRET|PASSWORD)[A-Z_]*)=.+$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Redacts ADO-style <c>Password=...</c> connection-string fragments.</summary>
    private static readonly Regex PasswordAssignmentRegex = new(
        @"Password=[^;]+",
        RegexOptions.Compiled);

    /// <summary>Matches the <c>Error Message:</c> section header of an xUnit failure block.</summary>
    private static readonly Regex XUnitErrorMessageRegex = new(
        @"^\s*Error Message:\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Matches the <c>Stack Trace:</c> section header of an xUnit failure block.</summary>
    private static readonly Regex XUnitStackTraceRegex = new(
        @"^\s*Stack Trace:\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly IGoalStore? _goalStore;
    private readonly IIssueStore? _issueStore;
    private readonly IEventBus? _eventBus;
    private readonly HiveConfigFile? _config;
    private readonly UserService? _userService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<CiMonitorService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan? _timeoutOverride;
    private readonly TimeSpan _startupScanWindow;

    /// <summary>
    /// Monitoring runs currently in flight, keyed by goal + commit + repository so the same
    /// commit is never monitored twice concurrently while distinct repositories still are.
    /// </summary>
    private readonly ConcurrentDictionary<(string GoalId, string Sha, string Repo), bool> _inFlight = new();

    /// <summary>
    /// Terminal CI events already published, keyed by goal + commit + repository + event type.
    /// Guarantees at-most-once publication across the startup scan and live monitoring.
    /// </summary>
    private readonly ConcurrentDictionary<(string GoalId, string Sha, string Repo, EventType Type), bool> _publishedEvents = new();

    /// <summary>Maximum number of per-runId log-fetch results kept in <see cref="_logFetchCache"/>.</summary>
    private const int LogFetchCacheCapacity = 20;

    /// <summary>
    /// Guards <see cref="_logFetchCache"/> and <see cref="_logFetchCacheOrder"/> as a single
    /// unit. Lock-free collections cannot express "insert and record insertion order" as one
    /// atomic step: two concurrent misses for the same runId would each enqueue the key, and a
    /// later eviction would then dequeue the stale duplicate and drop a freshly inserted value
    /// instead of the actual oldest one. Every read, insert, and eviction therefore runs here.
    /// </summary>
    private readonly Lock _logFetchCacheLock = new();

    /// <summary>
    /// Per-runId cache of successful log-fetch results, bounded to <see cref="LogFetchCacheCapacity"/>
    /// entries. Only non-null results are cached; eviction is FIFO via <see cref="_logFetchCacheOrder"/>.
    /// Access only under <see cref="_logFetchCacheLock"/>.
    /// </summary>
    private readonly Dictionary<string, LogFetchResult> _logFetchCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Insertion order of <see cref="_logFetchCache"/> keys, for FIFO eviction. A key is
    /// enqueued exactly once per insertion, so the queue mirrors the dictionary one-to-one.
    /// Access only under <see cref="_logFetchCacheLock"/>.
    /// </summary>
    private readonly Queue<string> _logFetchCacheOrder = new();

    /// <summary>Size (in bytes) of the UTF-8-aligned ring buffer used when reading a job log body.</summary>
    private const int LogReadBufferSize = 200_000;

    /// <summary>
    /// UTF-8 decoder with a replacement fallback: a truncated multi-byte sequence at the tail
    /// of the ring buffer decodes to U+FFFD instead of throwing.
    /// </summary>
    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Initialises a new <see cref="CiMonitorService"/> with optional dependencies.
    /// All parameters are optional so the service can be registered via a DI factory
    /// using <c>GetService</c> for each dependency.
    /// </summary>
    /// <param name="goalStore">Optional goal store, used by the startup scan to find recently merged goals.</param>
    /// <param name="issueStore">Optional issue store for creating CI failure issues.</param>
    /// <param name="eventBus">Optional event bus for publishing CI events.</param>
    /// <param name="config">Optional hive configuration.</param>
    /// <param name="userService">Optional user service for the GitHub OAuth token.</param>
    /// <param name="httpClientFactory">Optional HTTP client factory for GitHub API calls.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="pollInterval">Polling interval between check-run fetches; defaults to 30 seconds.</param>
    /// <param name="timeoutOverride">Optional CI timeout override for tests.</param>
    /// <param name="startupScanWindow">How far back the startup scan looks for merged goals; defaults to 60 minutes.</param>
    public CiMonitorService(
        IGoalStore? goalStore = null,
        IIssueStore? issueStore = null,
        IEventBus? eventBus = null,
        HiveConfigFile? config = null,
        UserService? userService = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<CiMonitorService>? logger = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeoutOverride = null,
        TimeSpan? startupScanWindow = null)
    {
        _goalStore = goalStore;
        _issueStore = issueStore;
        _eventBus = eventBus;
        _config = config;
        _userService = userService;
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<CiMonitorService>.Instance;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _timeoutOverride = timeoutOverride;
        _startupScanWindow = startupScanWindow ?? DefaultStartupScanWindow;
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

        var inFlightKey = (GoalId: goalId, Sha: mergeCommitSha, Repo: repoName);
        if (!_inFlight.TryAdd(inFlightKey, true))
        {
            _logger.LogDebug("CI monitoring already in flight for goal {GoalId} commit {Sha}", goalId, mergeCommitSha);
            return;
        }

        try
        {
            string? token;
            try
            {
                token = await GetGitHubTokenAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                LogCallerCancellation(goalId, repoName, mergeCommitSha);
                return;
            }
            if (token is null)
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
            var parsedRepo = ParseOwnerRepo(repoConfig.Url);
            if (parsedRepo is null)
            {
                _logger.LogWarning(
                    "Malformed GitHub repository URL '{Url}' for repo '{Repo}' — CI monitoring skipped",
                    repoConfig.Url, repoName);
                return;
            }
            var (owner, repo) = parsedRepo.Value;

            // Timeout: linked token for all HTTP calls and delays.
            var timeout = _timeoutOverride ?? TimeSpan.FromMinutes(repoConfig.CiTimeoutMinutes);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var linkedToken = linkedCts.Token;

            var client = CreateGitHubClient(token);

            // Why the loop terminated. Determined at the point the linked token throws so the
            // caller token is inspected while it is still the authoritative signal — a CI timeout
            // firing microseconds later must not be mistaken for the cause of a caller abort.
            // Stays None when the loop is left by an explicit `return`, in which case the
            // post-loop classification must never run.
            var cancelCause = CancelCause.None;

            while (true)
            {
                CiProbeResult probe;
                try
                {
                    probe = await ProbeCiStatusAsync(owner, repo, mergeCommitSha, client, linkedToken);
                }
                catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
                {
                    cancelCause = ClassifyCancellation(ct);
                    break;
                }

                TimeSpan delay;
                switch (probe.Status)
                {
                    case CiProbeStatus.Succeeded:
                        // Caller cancellation is authoritative and is checked BEFORE any terminal
                        // publication: a cancellation racing a completed response must produce no
                        // event on either the success or the failure path.
                        if (ct.IsCancellationRequested)
                        {
                            LogCallerCancellation(goalId, repoName, mergeCommitSha);
                            return;
                        }
                        _logger.LogInformation(
                            "CI passed for goal {GoalId} repo {Repo} commit {Sha} ({CheckCount} checks)",
                            goalId, repoName, mergeCommitSha, probe.CheckRuns.Count);
                        PublishCiSucceeded(goalId, repoName, mergeCommitSha, probe.CheckRuns.Count);
                        return;

                    case CiProbeStatus.Failed:
                        if (ct.IsCancellationRequested)
                        {
                            LogCallerCancellation(goalId, repoName, mergeCommitSha);
                            return;
                        }
                        _logger.LogWarning(
                            "CI failed for goal {GoalId} repo {Repo} commit {Sha}: {FailedCount} check(s) failed",
                            goalId, repoName, mergeCommitSha, probe.CheckRuns.Count);
                        try
                        {
                            var logsClient = _httpClientFactory!.CreateClient("github-api-logs");
                            await HandleCiFailureAsync(
                                goalId, repoName, mergeCommitSha, probe.CheckRuns,
                                owner, repo, token, logsClient, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // Caller cancelled while issues were being created. Cancellation wins:
                            // no CiFailed event is published.
                            LogCallerCancellation(goalId, repoName, mergeCommitSha);
                        }
                        return;

                    case CiProbeStatus.StillRunning:
                        delay = _pollInterval;
                        break;

                    case CiProbeStatus.NoChecks:
                        delay = _pollInterval;
                        break;

                    case CiProbeStatus.Error:
                        if (IsTerminalProbeError(probe.ErrorDetail))
                        {
                            _logger.LogWarning(
                                "CI monitoring stopped for goal {GoalId} repo {Repo} commit {Sha}: GitHub API error '{Detail}'",
                                goalId, repoName, mergeCommitSha, probe.ErrorDetail);
                            return;
                        }
                        delay = probe.RetryAfter ?? _pollInterval;
                        break;

                    default:
                        throw new InvalidOperationException($"Unhandled CI probe status '{probe.Status}'.");
                }

                // Delay between polling iterations.
                try
                {
                    await Task.Delay(delay, linkedToken);
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

    /// <summary>
    /// Scans recently completed goals at startup and reconciles their CI state: publishes
    /// terminal events for commits whose checks already finished while the orchestrator was
    /// down, and resumes background monitoring for commits whose checks are still running.
    /// </summary>
    /// <param name="ct">
    /// Application-lifetime token. It is also handed to any background monitoring tasks the
    /// scan starts, so shutdown stops them.
    /// </param>
    public virtual async Task StartupScanAsync(CancellationToken ct)
    {
        if (_goalStore is null)
        {
            _logger.LogDebug("CI startup scan skipped: no goal store available");
            return;
        }

        IReadOnlyList<Goal> goals;
        try
        {
            goals = await _goalStore.GetAllGoalsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CI startup scan failed to load goals");
            return;
        }

        // CompletedAt is the merge-time proxy: a goal is marked Completed at the moment its
        // merge lands, so it bounds how stale the commit's CI state can be.
        var cutoff = DateTime.UtcNow - _startupScanWindow;
        var candidates = goals
            .Where(g => g.Status == GoalStatus.Completed
                        && !string.IsNullOrWhiteSpace(g.MergeCommitHash)
                        && g.CompletedAt.HasValue
                        && g.CompletedAt.Value >= cutoff)
            .ToList();

        _logger.LogInformation(
            "CI startup scan: {Count} goal(s) merged within the last {Window}", candidates.Count, _startupScanWindow);

        foreach (var goal in candidates)
        {
            try
            {
                await ScanGoalAsync(goal, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CI startup scan failed for goal {GoalId}", goal.Id);
            }
        }
    }

    /// <summary>
    /// Probes and reconciles the CI state of every repository/commit pair of a single goal.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <param name="goal">The completed goal whose merge commits should be reconciled.</param>
    /// <param name="ct">Application-lifetime token, also used for any monitoring started here.</param>
    internal async Task ScanGoalAsync(Goal goal, CancellationToken ct)
    {
        if (_config is null || _httpClientFactory is null || _eventBus is null)
        {
            _logger.LogWarning(
                "CI startup scan skipped for goal {GoalId}: missing config, HTTP client factory, or event bus", goal.Id);
            return;
        }

        var hashes = (goal.MergeCommitHash ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var repoNames = goal.RepositoryNames;
        var count = Math.Min(hashes.Count, repoNames.Count);
        if (hashes.Count != repoNames.Count)
        {
            _logger.LogWarning(
                "Goal {GoalId} has {HashCount} merge hashes but {RepoCount} repository names — scanning the first {Count} pairs",
                goal.Id, hashes.Count, repoNames.Count, count);
        }

        for (var i = 0; i < count; i++)
        {
            var sha = hashes[i];
            var repoName = repoNames[i];

            var repoConfig = _config.Repositories.FirstOrDefault(
                r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase));
            if (repoConfig is null || !repoConfig.MonitorCi)
            {
                _logger.LogDebug(
                    "CI startup scan skipped for goal {GoalId} repo {Repo}: repository not configured or MonitorCi=false",
                    goal.Id, repoName);
                continue;
            }

            var token = await GetGitHubTokenAsync(ct);
            if (token is null)
            {
                _logger.LogWarning(
                    "No GitHub token available for CI startup scan of goal {GoalId} repo {Repo}", goal.Id, repoName);
                continue;
            }

            if (string.IsNullOrWhiteSpace(repoConfig.Url) || !repoConfig.Url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Repository '{Repo}' URL '{Url}' is not a GitHub repository — CI startup scan not supported",
                    repoName, repoConfig.Url);
                continue;
            }
            var parsedRepo = ParseOwnerRepo(repoConfig.Url);
            if (parsedRepo is null)
            {
                _logger.LogWarning(
                    "Malformed GitHub repository URL '{Url}' for repo '{Repo}' — CI startup scan skipped",
                    repoConfig.Url, repoName);
                continue;
            }
            var (owner, repo) = parsedRepo.Value;

            var client = CreateGitHubClient(token);
            var probe = await ProbeCiStatusAsync(owner, repo, sha, client, ct);

            switch (probe.Status)
            {
                case CiProbeStatus.Succeeded:
                    _logger.LogInformation(
                        "CI startup scan: CI already passed for goal {GoalId} repo {Repo} commit {Sha}",
                        goal.Id, repoName, sha);
                    PublishCiSucceeded(goal.Id, repoName, sha, probe.CheckRuns.Count);
                    break;

                case CiProbeStatus.Failed:
                    var logsClient = _httpClientFactory!.CreateClient("github-api-logs");
                    await HandleScannedFailureAsync(goal.Id, repoName, sha, probe.CheckRuns, owner, repo, token, logsClient, ct);
                    break;

                case CiProbeStatus.StillRunning:
                    _logger.LogInformation(
                        "CI startup scan: CI still running for goal {GoalId} repo {Repo} commit {Sha} — resuming monitoring",
                        goal.Id, repoName, sha);
                    StartBackgroundMonitoring(goal.Id, repoName, sha, ct);
                    break;

                case CiProbeStatus.NoChecks:
                    // A commit merged long ago with no check runs almost certainly has no CI
                    // configured; only recent merges are given time for checks to appear.
                    if (goal.CompletedAt.HasValue && goal.CompletedAt.Value < DateTime.UtcNow - NoChecksGracePeriod)
                    {
                        _logger.LogDebug(
                            "CI startup scan: no check runs for goal {GoalId} repo {Repo} commit {Sha} merged more than {Grace} ago — skipping",
                            goal.Id, repoName, sha, NoChecksGracePeriod);
                    }
                    else
                    {
                        StartBackgroundMonitoring(goal.Id, repoName, sha, ct);
                    }
                    break;

                case CiProbeStatus.Error:
                    if (IsTerminalProbeError(probe.ErrorDetail))
                    {
                        _logger.LogWarning(
                            "CI startup scan skipped for goal {GoalId} repo {Repo} commit {Sha}: GitHub API error '{Detail}'",
                            goal.Id, repoName, sha, probe.ErrorDetail);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "CI startup scan: retryable error '{Detail}' for goal {GoalId} repo {Repo} commit {Sha} — resuming monitoring",
                            probe.ErrorDetail, goal.Id, repoName, sha);
                        StartBackgroundMonitoring(goal.Id, repoName, sha, ct);
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled CI probe status '{probe.Status}'.");
            }
        }
    }

    /// <summary>
    /// Handles a failed CI result discovered by the startup scan, guarding it with the same
    /// in-flight key live monitoring uses so a concurrently running monitor cannot duplicate
    /// the issues or the event.
    /// </summary>
    private async Task HandleScannedFailureAsync(
        string goalId, string repoName, string sha, IReadOnlyList<CheckRunData> failedRuns,
        string owner, string repo, string token, HttpClient logsClient, CancellationToken ct)
    {
        var inFlightKey = (GoalId: goalId, Sha: sha, Repo: repoName);
        if (!_inFlight.TryAdd(inFlightKey, true))
        {
            _logger.LogDebug(
                "CI startup scan: monitoring already in flight for goal {GoalId} commit {Sha} — skipping", goalId, sha);
            return;
        }

        try
        {
            _logger.LogWarning(
                "CI startup scan: CI already failed for goal {GoalId} repo {Repo} commit {Sha}: {FailedCount} check(s) failed",
                goalId, repoName, sha, failedRuns.Count);
            await HandleCiFailureAsync(goalId, repoName, sha, failedRuns, owner, repo, token, logsClient, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogCallerCancellation(goalId, repoName, sha);
        }
        finally
        {
            _inFlight.TryRemove(inFlightKey, out _);
        }
    }

    /// <summary>
    /// Starts fire-and-forget monitoring for a commit found still-pending by the startup scan.
    /// Exceptions are logged rather than left unobserved.
    /// </summary>
    private void StartBackgroundMonitoring(string goalId, string repoName, string sha, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await MonitorMergeAsync(goalId, repoName, sha, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "CI monitoring failed for goal {GoalId} repo {Repo} commit {Sha}", goalId, repoName, sha);
            }
        }, CancellationToken.None);
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

    /// <summary>Whether a probe error is permanent (no amount of retrying will help).</summary>
    private static bool IsTerminalProbeError(string? errorDetail) =>
        errorDetail is "401" or "403" or "404";

    /// <summary>
    /// Resolves the GitHub token: the user service's OAuth token first, then environment
    /// variables. Returns <c>null</c> when no token is available.
    /// </summary>
    private async Task<string?> GetGitHubTokenAsync(CancellationToken ct)
    {
        string? token = null;
        if (_userService is not null)
        {
            try
            {
                token = await _userService.GetActiveAccessTokenAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get active access token for CI monitoring — falling back to environment");
            }
        }

        token ??= Environment.GetEnvironmentVariable("GH_TOKEN")
                  ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <summary>Parses <c>owner/repo</c> out of a GitHub HTTPS or SSH URL, or <c>null</c> if malformed.</summary>
    private static (string Owner, string Repo)? ParseOwnerRepo(string? url) =>
        TryParseGitHubRepo(url ?? string.Empty, out var owner, out var repo) ? (owner, repo) : null;

    /// <summary>Creates a GitHub API client with the bearer token attached to every request.</summary>
    private HttpClient CreateGitHubClient(string token)
    {
        var client = _httpClientFactory!.CreateClient("github-api");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Publishes <see cref="EventType.CiSucceeded"/> at most once per goal/commit/repository.</summary>
    private void PublishCiSucceeded(string goalId, string repoName, string sha, int checkCount)
    {
        if (!TryMarkPublished(goalId, sha, repoName, EventType.CiSucceeded))
        {
            _logger.LogDebug(
                "CiSucceeded already published for goal {GoalId} repo {Repo} commit {Sha} — skipping", goalId, repoName, sha);
            return;
        }

        _eventBus!.Publish(new SystemEvent(
            Type: EventType.CiSucceeded,
            Message: $"All {checkCount} checks passed",
            GoalId: goalId,
            Repository: repoName));
    }

    /// <summary>
    /// Atomically claims the right to publish a terminal CI event for this goal/commit/repository.
    /// Returns <c>false</c> when the event was already published.
    /// </summary>
    private bool TryMarkPublished(string goalId, string sha, string repoName, EventType type) =>
        _publishedEvents.TryAdd((goalId, sha, repoName, type), true);

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

    /// <summary>
    /// Performs a single CI probe: fetches every check-run page for a commit and classifies
    /// the result. The probe never delays or retries — the caller decides what to do with a
    /// <see cref="CiProbeStatus.StillRunning"/> or retryable <see cref="CiProbeStatus.Error"/>.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <param name="owner">GitHub repository owner.</param>
    /// <param name="repo">GitHub repository name.</param>
    /// <param name="sha">The commit SHA whose check runs are probed.</param>
    /// <param name="client">The GitHub API client (authorization already attached).</param>
    /// <param name="ct">Cancellation token; cancellation propagates to the caller.</param>
    internal async Task<CiProbeResult> ProbeCiStatusAsync(
        string owner, string repo, string sha, HttpClient client, CancellationToken ct)
    {
        var allRuns = new List<CheckRunData>();
        var declaredRuns = 0;
        var totalCount = 0;
        var page = 1;

        while (true)
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100&page={page}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // cancellation — propagate to the caller
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transport error fetching check runs for {Owner}/{Repo} commit {Sha} — will retry", owner, repo, sha);
                return new CiProbeResult(CiProbeStatus.Error, [], "transport");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("GitHub API returned 401 for {Owner}/{Repo} commit {Sha} — token invalid or expired", owner, repo, sha);
                    return new CiProbeResult(CiProbeStatus.Error, [], "401");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // Rate-limit detection keys on header PRESENCE, never on whether the value
                    // parses: a malformed Retry-After still means "GitHub asked us to back off"
                    // and must never be downgraded to a terminal 403 that abandons monitoring.
                    var retryAfterPresent = HasHeader(response, "Retry-After");
                    var rateLimitRemaining = GetHeaderValue(response, "X-RateLimit-Remaining");
                    var rateLimited = retryAfterPresent
                                      || string.Equals(rateLimitRemaining, "0", StringComparison.Ordinal);
                    if (rateLimited)
                    {
                        // Parsing only decides HOW LONG to wait; an unparseable value falls back
                        // to the reset header and then to the fixed default.
                        var retryAfter = ParseRetryAfter(response) ?? ParseRateLimitReset(response) ?? DefaultRateLimitWait;
                        _logger.LogWarning(
                            "GitHub API rate limit exceeded for {Owner}/{Repo} — retry after {Seconds}s", owner, repo, retryAfter.TotalSeconds);
                        return new CiProbeResult(CiProbeStatus.Error, [], "403-rate-limit", retryAfter);
                    }
                    _logger.LogWarning("GitHub API returned 403 for {Owner}/{Repo} commit {Sha}", owner, repo, sha);
                    return new CiProbeResult(CiProbeStatus.Error, [], "403");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("GitHub API returned 404 for {Owner}/{Repo} commit {Sha} — commit or repository not found", owner, repo, sha);
                    return new CiProbeResult(CiProbeStatus.Error, [], "404");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = ParseRetryAfter(response) ?? DefaultRateLimitWait;
                    _logger.LogWarning("GitHub API returned 429 for {Owner}/{Repo} — retry after {Seconds}s", owner, repo, retryAfter.TotalSeconds);
                    return new CiProbeResult(CiProbeStatus.Error, [], "429", retryAfter);
                }

                if ((int)response.StatusCode >= 500)
                {
                    _logger.LogWarning("GitHub API returned {Status} for {Owner}/{Repo} commit {Sha} — will retry", (int)response.StatusCode, owner, repo, sha);
                    return new CiProbeResult(CiProbeStatus.Error, [], "5xx");
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("GitHub API returned {Status} for {Owner}/{Repo} commit {Sha} — will retry", (int)response.StatusCode, owner, repo, sha);
                    return new CiProbeResult(CiProbeStatus.Error, [], "other-http");
                }

                // Parse JSON.
                JsonDocument doc;
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    doc = JsonDocument.Parse(stream);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Malformed JSON from GitHub API for {Owner}/{Repo} commit {Sha} — will retry", owner, repo, sha);
                    return new CiProbeResult(CiProbeStatus.Error, [], "malformed");
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("total_count", out var tc))
                    {
                        _logger.LogWarning(
                            "GitHub API response for {Owner}/{Repo} commit {Sha} has no total_count — will retry", owner, repo, sha);
                        return new CiProbeResult(CiProbeStatus.Error, [], "malformed");
                    }

                    // JsonValueKind.Number does NOT imply GetInt32() succeeds: fractional and
                    // out-of-range numbers throw, and a negative count would corrupt pagination.
                    // TryGetInt32 keeps every malformed value inside the result-based contract.
                    if (tc.ValueKind != JsonValueKind.Number
                        || !tc.TryGetInt32(out var parsedTotalCount)
                        || parsedTotalCount < 0)
                    {
                        _logger.LogWarning(
                            "GitHub API response for {Owner}/{Repo} commit {Sha} has a malformed total_count — will retry", owner, repo, sha);
                        return new CiProbeResult(CiProbeStatus.Error, [], "malformed");
                    }
                    totalCount = parsedTotalCount;

                    if (!root.TryGetProperty("check_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
                    {
                        _logger.LogWarning(
                            "GitHub API response for {Owner}/{Repo} commit {Sha} has no check_runs array — will retry", owner, repo, sha);
                        return new CiProbeResult(CiProbeStatus.Error, [], "malformed");
                    }

                    foreach (var run in runs.EnumerateArray())
                    {
                        declaredRuns++;
                        var parsed = ParseCheckRun(run);
                        if (parsed is null)
                        {
                            _logger.LogDebug(
                                "Skipping malformed check run for {Owner}/{Repo} commit {Sha}", owner, repo, sha);
                            continue;
                        }
                        allRuns.Add(parsed);
                    }
                }
            }

            // Pagination: if total_count > 100, fetch pages 2..N.
            var totalPages = (int)Math.Ceiling(totalCount / 100.0);
            if (page >= totalPages)
                break;
            page++;
        }

        // Every declared run was malformed → the response carries no usable information.
        if (declaredRuns > 0 && allRuns.Count == 0)
        {
            _logger.LogWarning(
                "All {Count} check run(s) for {Owner}/{Repo} commit {Sha} were malformed — will retry", declaredRuns, owner, repo, sha);
            return new CiProbeResult(CiProbeStatus.Error, [], "malformed");
        }

        if (allRuns.Count == 0)
            return new CiProbeResult(CiProbeStatus.NoChecks, []);

        // A run that has not completed outranks every conclusion: CI is still in progress.
        if (allRuns.Any(r => !string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase)))
            return new CiProbeResult(CiProbeStatus.StillRunning, allRuns);

        var nonSkipped = allRuns
            .Where(r => !string.Equals(r.Conclusion, "skipped", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Every check skipped → vacuously all passed.
        if (nonSkipped.Count == 0)
            return new CiProbeResult(CiProbeStatus.Succeeded, allRuns);

        var failedRuns = nonSkipped.Where(r => IsFailConclusion(r.Conclusion)).ToList();
        if (failedRuns.Count > 0)
            return new CiProbeResult(CiProbeStatus.Failed, failedRuns);

        return new CiProbeResult(CiProbeStatus.Succeeded, allRuns);
    }

    /// <summary>
    /// Parses one check-run element, or returns <c>null</c> when the run is malformed
    /// (missing <c>name</c> or <c>status</c>).
    /// </summary>
    private static CheckRunData? ParseCheckRun(JsonElement run)
    {
        if (run.ValueKind != JsonValueKind.Object)
            return null;

        if (!run.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
            return null;
        var name = n.GetString();
        if (string.IsNullOrEmpty(name))
            return null;

        if (!run.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.String)
            return null;
        var status = s.GetString();
        if (string.IsNullOrEmpty(status))
            return null;

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

        // details_url is always retained when present; the run ID is parsed from it only when
        // it matches the /actions/runs/<id> shape — otherwise DetailsUrl is kept and RunId is null.
        string? detailsUrl = null;
        if (run.TryGetProperty("details_url", out var du) && du.ValueKind == JsonValueKind.String)
            detailsUrl = du.GetString();
        string? runId = null;
        if (!string.IsNullOrEmpty(detailsUrl))
        {
            var match = ActionsRunIdRegex.Match(detailsUrl);
            if (match.Success)
                runId = match.Groups[1].Value;
        }

        return new CheckRunData(name, status, conclusion, htmlUrl, summary, text, detailsUrl, runId);
    }

    /// <summary>
    /// Fetches and parses the CI job logs for a GitHub Actions run, returning the extracted
    /// test failures (or a sanitized fallback snippet when the log carries no parseable
    /// failures). Results are cached per runId.
    /// </summary>
    /// <param name="runId">The GitHub Actions run ID, or <c>null</c>.</param>
    /// <param name="owner">GitHub repository owner.</param>
    /// <param name="repo">GitHub repository name.</param>
    /// <param name="token">Bearer token for the GitHub API calls.</param>
    /// <param name="client">The <c>github-api-logs</c> HTTP client (no auto-redirect).</param>
    /// <param name="ct">Cancellation token; cancellation propagates to the caller.</param>
    /// <returns>
    /// The parsed log result, or <c>null</c> when no runId was given, no failed job log could
    /// be fetched, or a non-cancellation failure occurred.
    /// </returns>
    internal async Task<LogFetchResult?> FetchJobLogsAsync(
        string? runId, string owner, string repo, string token, HttpClient client, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(runId))
            return null;

        if (TryGetCachedLogFetchResult(runId, out var cached))
            return cached;

        LogFetchResult? result;
        try
        {
            result = await FetchJobLogsCoreAsync(runId, owner, repo, token, client, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation — propagate
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch CI job logs for run {RunId} repo {Owner}/{Repo}", runId, owner, repo);
            return null;
        }

        if (result is not null)
            CacheLogFetchResult(runId, result);

        return result;
    }

    /// <summary>
    /// Enumerates a run's failed jobs and fetches each one's log. Partial success is allowed:
    /// a job whose log cannot be fetched is skipped and the remaining jobs still contribute.
    /// </summary>
    private async Task<LogFetchResult?> FetchJobLogsCoreAsync(
        string runId, string owner, string repo, string token, HttpClient client, CancellationToken ct)
    {
        var failedJobIds = new List<long>();

        var jobsUrl = $"https://api.github.com/repos/{owner}/{repo}/actions/runs/{runId}/jobs?per_page=100";
        using (var jobsRequest = CreateAuthorizedRequest(HttpMethod.Get, jobsUrl, token))
        {
            jobsRequest.Headers.Add("Accept", "application/vnd.github+json");
            jobsRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var jobsResponse = await client.SendAsync(jobsRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!jobsResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitHub API returned {Status} fetching jobs for run {RunId} — no logs available",
                    (int)jobsResponse.StatusCode, runId);
                return null;
            }

            await using var stream = await jobsResponse.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // GitHub caps the page at 100 jobs; anything beyond that is examined only on the
            // first page (no pagination) per the log-fetch contract.
            if (root.TryGetProperty("total_count", out var tc)
                && tc.ValueKind == JsonValueKind.Number
                && tc.TryGetInt32(out var totalCount)
                && totalCount > 100)
            {
                _logger.LogWarning(
                    "Run {RunId} has {Count} jobs; only the first 100 are examined", runId, totalCount);
            }

            if (!root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var job in jobs.EnumerateArray())
            {
                if (!job.TryGetProperty("id", out var idProp)
                    || idProp.ValueKind != JsonValueKind.Number
                    || !idProp.TryGetInt64(out var jobId))
                {
                    continue; // malformed job entry — skip
                }

                string? conclusion = null;
                if (job.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String)
                    conclusion = c.GetString();

                if (!string.Equals(conclusion, "failure", StringComparison.OrdinalIgnoreCase))
                    continue;

                failedJobIds.Add(jobId);
            }
        }

        if (failedJobIds.Count == 0)
            return null; // no failed jobs → no logs to fetch

        var combinedFailures = new List<(string TestName, string Error, string StackTrace)>();
        string? lastSuccessfulJobLog = null;

        foreach (var jobId in failedJobIds)
        {
            string? sanitizedLog;
            try
            {
                sanitizedLog = await FetchJobLogAsync(jobId, owner, repo, token, client, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancellation — propagate
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch CI log for job {JobId} run {RunId}", jobId, runId);
                continue; // job failure — partial success is allowed
            }

            if (sanitizedLog is null)
                continue; // log not fetched (HTTP error) — job failure

            lastSuccessfulJobLog = sanitizedLog;

            var jobFailures = ParseTestFailuresFromLogs(sanitizedLog);
            combinedFailures.AddRange(jobFailures);
        }

        if (lastSuccessfulJobLog is null)
            return null; // every job's log fetch failed

        if (combinedFailures.Count > 0)
            return new LogFetchResult(combinedFailures, null);

        // No parseable failures: fall back to a sanitized tail of the last successful log.
        if (string.IsNullOrWhiteSpace(lastSuccessfulJobLog))
            return new LogFetchResult([], null);

        var snippet = lastSuccessfulJobLog.Length <= 500
            ? lastSuccessfulJobLog
            : lastSuccessfulJobLog[^500..];
        return new LogFetchResult([], snippet);
    }

    /// <summary>
    /// Fetches one failed job's log: requests <c>/actions/jobs/{jobId}/logs</c> with bearer
    /// auth, manually follows the 302 redirect to the cross-host signed URL WITHOUT auth, and
    /// reads the body through a bounded UTF-8-aligned ring buffer. The decoded tail is
    /// sanitized before it is returned.
    /// </summary>
    /// <returns>The sanitized log text, or <c>null</c> when the job's log could not be fetched.</returns>
    private async Task<string?> FetchJobLogAsync(
        long jobId, string owner, string repo, string token, HttpClient client, CancellationToken ct)
    {
        var logsUrl = $"https://api.github.com/repos/{owner}/{repo}/actions/jobs/{jobId}/logs";
        using (var request = CreateAuthorizedRequest(HttpMethod.Get, logsUrl, token))
        {
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("GitHub API returned 429 fetching logs for job {JobId} — skipping", jobId);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var remaining = GetHeaderValue(response, "X-RateLimit-Remaining");
                _logger.LogWarning(
                    "GitHub API returned 403 (X-RateLimit-Remaining: {Remaining}) fetching logs for job {JobId} — skipping",
                    remaining ?? "n/a", jobId);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Redirect)
            {
                var location = response.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(location))
                {
                    _logger.LogWarning(
                        "GitHub API returned 302 without a Location header for job {JobId} logs — skipping", jobId);
                    return null;
                }

                // The redirect target is a signed cross-host URL that must NOT receive the
                // bearer token; the fresh request below carries no Authorization header.
                using var redirectRequest = new HttpRequestMessage(HttpMethod.Get, location);
                using var redirectResponse = await client.SendAsync(redirectRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!redirectResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Log download for job {JobId} returned {Status} at the redirect target — skipping",
                        jobId, (int)redirectResponse.StatusCode);
                    return null;
                }

                return await ReadLogBodyAsync(redirectResponse, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitHub API returned {Status} fetching logs for job {JobId} — skipping",
                    (int)response.StatusCode, jobId);
                return null;
            }

            return await ReadLogBodyAsync(response, ct);
        }
    }

    /// <summary>
    /// Streams the log body through a bounded ring buffer (retaining only the most recent
    /// bytes), decodes it as UTF-8, and sanitizes the result. The ring keeps memory bounded
    /// regardless of how large the raw log is.
    /// </summary>
    private static async Task<string> ReadLogBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var ring = new ByteRingBuffer(LogReadBufferSize);
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), ct);
            if (read == 0)
                break;
            ring.Write(chunk.AsSpan(0, read));
        }

        return SanitizeLogContent(ring.DecodeUtf8());
    }

    /// <summary>Creates a request with the GitHub bearer token attached.</summary>
    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>Reads a cached log-fetch result under the cache lock.</summary>
    /// <param name="runId">The GitHub Actions run ID to look up.</param>
    /// <param name="result">The cached result when present.</param>
    /// <returns><c>true</c> when the runId was cached.</returns>
    private bool TryGetCachedLogFetchResult(string runId, out LogFetchResult? result)
    {
        lock (_logFetchCacheLock)
        {
            if (_logFetchCache.TryGetValue(runId, out var cached))
            {
                result = cached;
                return true;
            }
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Stores a successful log-fetch result, evicting the oldest entry when at capacity.
    /// The dictionary write, the order-queue enqueue, and the eviction are one atomic step so
    /// the queue always mirrors the dictionary: a key is enqueued exactly once per insertion,
    /// so eviction can never dequeue a stale duplicate and drop a value that is still current.
    /// </summary>
    private void CacheLogFetchResult(string runId, LogFetchResult result)
    {
        lock (_logFetchCacheLock)
        {
            // A concurrent duplicate fill overwrites the value in place; the key keeps its
            // original queue position rather than being enqueued a second time.
            if (!_logFetchCache.ContainsKey(runId))
                _logFetchCacheOrder.Enqueue(runId);

            _logFetchCache[runId] = result;

            // FIFO eviction. Every queued key is present in the dictionary, so each dequeue
            // removes exactly one real entry and the bound is reached in a bounded number of
            // iterations.
            while (_logFetchCache.Count > LogFetchCacheCapacity && _logFetchCacheOrder.Count > 0)
            {
                var oldest = _logFetchCacheOrder.Dequeue();
                _logFetchCache.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// A fixed-capacity ring buffer that retains only the most recent bytes written to it.
    /// Used to bound the decoded footprint of arbitrarily large CI logs.
    /// </summary>
    private sealed class ByteRingBuffer
    {
        private readonly byte[] _buffer;
        private int _start;
        private int _length;

        public ByteRingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _buffer = new byte[capacity];
        }

        /// <summary>Number of currently retained bytes.</summary>
        public int Length => _length;

        /// <summary>Appends data, evicting the oldest bytes once the ring is full.</summary>
        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return;

            if (data.Length >= _buffer.Length)
            {
                // A single chunk that exceeds the whole ring: keep only its tail.
                data = data[^_buffer.Length..];
                _start = 0;
                _length = 0;
            }

            var offset = 0;
            while (offset < data.Length)
            {
                var writePos = (_start + _length) % _buffer.Length;
                var chunk = Math.Min(data.Length - offset, _buffer.Length - writePos);
                data.Slice(offset, chunk).CopyTo(_buffer.AsSpan(writePos, chunk));
                offset += chunk;

                var newLength = _length + chunk;
                if (newLength <= _buffer.Length)
                {
                    _length = newLength;
                }
                else
                {
                    var excess = newLength - _buffer.Length;
                    _start = (_start + excess) % _buffer.Length;
                    _length = _buffer.Length;
                }
            }
        }

        /// <summary>
        /// Decodes the retained bytes as UTF-8. The window may begin mid-way through a
        /// multi-byte character (its lead byte was evicted), so leading continuation bytes are
        /// skipped to land on a character boundary; a truncated final sequence decodes to the
        /// replacement character instead of throwing.
        /// </summary>
        public string DecodeUtf8()
        {
            if (_length == 0)
                return string.Empty;

            var start = _start;
            var length = _length;
            while (length > 0 && (_buffer[start] & 0b1100_0000) == 0b1000_0000)
            {
                start = (start + 1) % _buffer.Length;
                length--;
            }

            if (length == 0)
                return string.Empty;

            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
                bytes[i] = _buffer[(start + i) % _buffer.Length];

            return LenientUtf8.GetString(bytes);
        }
    }

    /// <summary>
    /// Handles a failed CI result: creates one issue per parseable test failure, fetching the
    /// actual job logs when the check-run output carries no test names. Runs are deduplicated
    /// per runId so the same GitHub Actions run is never fetched twice in one call.
    /// </summary>
    /// <param name="goalId">The goal whose merge commit failed CI.</param>
    /// <param name="repoName">The repository name as configured in hive-config.yaml.</param>
    /// <param name="sha">The merge commit SHA that failed CI.</param>
    /// <param name="failedCheckRuns">The failed check runs to turn into issues.</param>
    /// <param name="owner">GitHub repository owner.</param>
    /// <param name="repo">GitHub repository name.</param>
    /// <param name="token">Bearer token for the GitHub API calls.</param>
    /// <param name="client">The <c>github-api-logs</c> HTTP client (no auto-redirect).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleCiFailureAsync(
        string goalId, string repoName, string sha, IReadOnlyList<CheckRunData> failedCheckRuns,
        string owner, string repo, string token, HttpClient client, CancellationToken ct)
    {
        var created = 0;
        var updated = 0;

        if (_issueStore is not null)
        {
            try
            {
                // Per-call dedup: a runId is added the first time its logs are fetched so a
                // later check run pointing at the same Actions run is skipped.
                var processedRunIds = new HashSet<string>(StringComparer.Ordinal);

                foreach (var run in failedCheckRuns)
                {
                    var output = CombineOutput(run);
                    var testNames = ParseTestNames(output);

                    if (testNames.Count > 0)
                    {
                        // (a) The output already names the failing tests — create issues from
                        // it directly and do NOT mark the runId as processed.
                        foreach (var testName in testNames)
                        {
                            var title = $"CI failure: {testName}";
                            var (c, u) = await CreateOrUpdateIssueAsync(
                                goalId, repoName, title,
                                BuildIssueDescription(goalId, sha, run),
                                BuildDedupAppend(run), ct);
                            created += c;
                            updated += u;
                        }
                        continue;
                    }

                    // No parseable test names in the output.
                    if (string.IsNullOrEmpty(run.RunId))
                    {
                        // (d) No runId to fetch logs from — URL-only fallback.
                        var title = string.IsNullOrWhiteSpace(run.Name)
                            ? "CI failure: unknown check"
                            : $"CI failure: {run.Name}";
                        var (c, u) = await CreateOrUpdateIssueAsync(
                            goalId, repoName, title,
                            BuildUrlOnlyIssueDescription(goalId, sha, run.HtmlUrl),
                            BuildUrlOnlyDedupAppend(run.HtmlUrl), ct);
                        created += c;
                        updated += u;
                        continue;
                    }

                    if (!processedRunIds.Add(run.RunId))
                    {
                        // (c) This Actions run was already processed — skip.
                        continue;
                    }

                    // (b) Fetch the run's job logs and create issues from the log result.
                    var logResult = await FetchJobLogsAsync(run.RunId, owner, repo, token, client, ct);
                    if (logResult is null)
                    {
                        // Log fetch failed — URL-only fallback.
                        var title = string.IsNullOrWhiteSpace(run.Name)
                            ? "CI failure: unknown check"
                            : $"CI failure: {run.Name}";
                        var (c, u) = await CreateOrUpdateIssueAsync(
                            goalId, repoName, title,
                            BuildUrlOnlyIssueDescription(goalId, sha, run.HtmlUrl),
                            BuildUrlOnlyDedupAppend(run.HtmlUrl), ct);
                        created += c;
                        updated += u;
                        continue;
                    }

                    if (logResult.Failures.Count > 0)
                    {
                        // Log-derived issues, one per parsed test failure.
                        foreach (var failure in logResult.Failures)
                        {
                            var sanitizedTestName = SanitizeLogContent(failure.TestName);
                            var sanitizedError = SanitizeLogContent(failure.Error);
                            var sanitizedStackTrace = SanitizeLogContent(failure.StackTrace);
                            var title = $"CI failure: {sanitizedTestName}";
                            var (c, u) = await CreateOrUpdateIssueAsync(
                                goalId, repoName, title,
                                BuildLogDerivedIssueDescription(
                                    goalId, sha, run.HtmlUrl, sanitizedTestName, sanitizedError, sanitizedStackTrace),
                                BuildLogDerivedDedupAppend(
                                    run.HtmlUrl, sanitizedTestName, sanitizedError, sanitizedStackTrace), ct);
                            created += c;
                            updated += u;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(logResult.FallbackSnippet))
                    {
                        // Log-derived fallback: no tests parsed, but the log yielded a snippet.
                        var title = string.IsNullOrWhiteSpace(run.Name)
                            ? "CI failure: unknown check"
                            : $"CI failure: {run.Name}";
                        var (c, u) = await CreateOrUpdateIssueAsync(
                            goalId, repoName, title,
                            BuildLogFallbackIssueDescription(goalId, sha, run.HtmlUrl, logResult.FallbackSnippet),
                            BuildLogFallbackDedupAppend(run.HtmlUrl, logResult.FallbackSnippet), ct);
                        created += c;
                        updated += u;
                    }
                    else
                    {
                        // Empty-snippet fallback: the log was fetched but carried nothing
                        // usable — use the URL-only format, not a log format with an empty body.
                        var title = string.IsNullOrWhiteSpace(run.Name)
                            ? "CI failure: unknown check"
                            : $"CI failure: {run.Name}";
                        var (c, u) = await CreateOrUpdateIssueAsync(
                            goalId, repoName, title,
                            BuildUrlOnlyIssueDescription(goalId, sha, run.HtmlUrl),
                            BuildUrlOnlyDedupAppend(run.HtmlUrl), ct);
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

        // Guarantee CiFailed publication regardless of issue-store success, but at most once
        // per goal/commit/repository.
        if (!TryMarkPublished(goalId, sha, repoName, EventType.CiFailed))
        {
            _logger.LogDebug(
                "CiFailed already published for goal {GoalId} repo {Repo} commit {Sha} — skipping", goalId, repoName, sha);
            return;
        }

        _eventBus!.Publish(new SystemEvent(
            Type: EventType.CiFailed,
            Message: $"{failedCheckRuns.Count} check(s) failed; created {created} issue(s), updated {updated} issue(s)",
            GoalId: goalId,
            Repository: repoName));
    }

    /// <summary>
    /// Creates a CI-failure issue (or appends to an existing open/triaged issue with the same
    /// title and source goal). The description and dedup-append text are pre-built by the
    /// caller so each issue format (output-derived, log-derived, URL-only) stays in one place.
    /// </summary>
    private async Task<(int Created, int Updated)> CreateOrUpdateIssueAsync(
        string goalId, string repoName, string title, string description, string dedupAppend, CancellationToken ct)
    {
        var issues = await _issueStore!.GetIssuesAsync(repository: repoName, ct: ct);
        var existing = issues.FirstOrDefault(i =>
            (i.Status == IssueStatus.Open || i.Status == IssueStatus.Triaged) &&
            string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.SourceGoalId, goalId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Description += dedupAppend;
            await _issueStore.UpdateIssueAsync(existing, ct);
            return (0, 1);
        }

        var id = await IssueIdGenerator.GenerateAsync(title, _issueStore, ct);
        var issue = new Issue
        {
            Id = id,
            Type = IssueType.Bug,
            Title = title,
            Description = description,
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

    /// <summary>
    /// Redacts secrets from raw CI log content: GitHub tokens (<c>ghp_</c>/<c>gho_</c>/<c>ghs_</c>),
    /// <c>Bearer</c> authorization headers, secret-looking environment variables, and
    /// <c>Password=</c> connection-string fragments. Never truncates the input — every line of
    /// the log survives, with only the secret values replaced.
    /// </summary>
    /// <param name="logContent">The raw log content to sanitize.</param>
    /// <returns>The sanitized log content.</returns>
    internal static string SanitizeLogContent(string logContent)
    {
        var sanitized = GhpTokenRegex.Replace(logContent, "***REDACTED***");
        sanitized = GhoTokenRegex.Replace(sanitized, "***REDACTED***");
        sanitized = GhsTokenRegex.Replace(sanitized, "***REDACTED***");
        sanitized = BearerTokenRegex.Replace(sanitized, "Bearer ***REDACTED***");
        sanitized = SecretEnvVarRegex.Replace(sanitized, "$1=***REDACTED***");
        sanitized = PasswordAssignmentRegex.Replace(sanitized, "Password=***REDACTED***");
        return sanitized;
    }

    /// <summary>
    /// Parses xUnit test failures out of a CI log. Each failure block has the shape
    /// <c>Failed {name} [duration]</c> followed by an <c>Error Message:</c> section and a
    /// <c>Stack Trace:</c> section. All such blocks are parsed; logs with no failure headers
    /// (including count-only summaries such as <c>Failed: 0</c>) yield an empty list.
    /// </summary>
    /// <param name="logContent">The raw (unsanitized) log content to parse.</param>
    /// <returns>The parsed failures, or an empty list when none could be parsed.</returns>
    internal static List<(string TestName, string Error, string StackTrace)> ParseTestFailuresFromLogs(string logContent)
    {
        var failures = new List<(string TestName, string Error, string StackTrace)>();

        if (string.IsNullOrEmpty(logContent))
            return failures;

        // Split into lines, preserving positions so sections can be sliced by line index.
        var lines = logContent.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var header = XUnitFailedTestRegex.Match(lines[i]);
            if (!header.Success)
                continue;

            var testName = header.Groups[1].Value.Trim();

            // The failure block ends at the next failure header (or the end of the log).
            var blockEnd = lines.Length;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (XUnitFailedTestRegex.IsMatch(lines[j]))
                {
                    blockEnd = j;
                    break;
                }
            }

            // Locate the Error Message: and Stack Trace: headers inside the block.
            var errorHeaderIndex = -1;
            var stackHeaderIndex = -1;
            for (var j = i + 1; j < blockEnd; j++)
            {
                if (errorHeaderIndex < 0 && XUnitErrorMessageRegex.IsMatch(lines[j]))
                {
                    errorHeaderIndex = j;
                    continue;
                }
                if (stackHeaderIndex < 0 && XUnitStackTraceRegex.IsMatch(lines[j]))
                {
                    stackHeaderIndex = j;
                    break; // the stack trace is the last section of the failure block
                }
            }

            if (errorHeaderIndex < 0)
                continue; // header without a message section is not a parseable failure

            var errorEnd = stackHeaderIndex >= 0 ? stackHeaderIndex : blockEnd;
            var error = string.Join("\n", lines[(errorHeaderIndex + 1)..errorEnd]).Trim();

            var stackTrace = string.Empty;
            if (stackHeaderIndex >= 0)
                stackTrace = string.Join("\n", lines[(stackHeaderIndex + 1)..blockEnd]).Trim();

            failures.Add((testName, error, stackTrace));
        }

        return failures;
    }

    private static string CombineOutput(CheckRunData run)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(run.OutputSummary))
            parts.Add(run.OutputSummary);
        if (!string.IsNullOrWhiteSpace(run.OutputText))
            parts.Add(run.OutputText);
        return string.Join("\n\n", parts);
    }

    /// <summary>Renders the check run's HTML URL, or <c>(no URL)</c> when absent.</summary>
    private static string RenderHtmlUrl(string? htmlUrl) =>
        string.IsNullOrWhiteSpace(htmlUrl) ? "(no URL)" : htmlUrl;

    /// <summary>Builds the description for an issue created from a check run's own output.</summary>
    private static string BuildIssueDescription(string goalId, string sha, CheckRunData run)
    {
        var errorOutput = CombineOutput(run);
        return $"CI failed for goal '{goalId}' (commit {sha}).\n\n{errorOutput}\n\nCI run: {RenderHtmlUrl(run.HtmlUrl)}";
    }

    /// <summary>Builds the dedup-append text for an issue created from a check run's own output.</summary>
    private static string BuildDedupAppend(CheckRunData run)
    {
        var errorOutput = CombineOutput(run);
        return $"\n\n---\n[Updated {DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}]\n{errorOutput}\nCI run: {RenderHtmlUrl(run.HtmlUrl)}";
    }

    /// <summary>Builds the description for a log-derived issue (one parsed test failure).</summary>
    private static string BuildLogDerivedIssueDescription(
        string goalId, string sha, string? htmlUrl, string testName, string error, string stackTrace) =>
        $"CI failed for goal '{goalId}' (commit {sha}).\n\nTest: {testName}\n\nError: {error}\n\nStack Trace:\n{stackTrace}\n\nCI run: {RenderHtmlUrl(htmlUrl)}";

    /// <summary>Builds the dedup-append text for a log-derived issue.</summary>
    private static string BuildLogDerivedDedupAppend(
        string? htmlUrl, string testName, string error, string stackTrace) =>
        $"\n\n---\n[Updated {DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}]\nTest: {testName}\nError: {error}\nStack Trace:\n{stackTrace}\nCI run: {RenderHtmlUrl(htmlUrl)}";

    /// <summary>Builds the description for a log-fallback issue (no tests parsed, snippet available).</summary>
    private static string BuildLogFallbackIssueDescription(
        string goalId, string sha, string? htmlUrl, string snippet) =>
        $"CI failed for goal '{goalId}' (commit {sha}).\n\nLog output (last 500 chars):\n{snippet}\n\nCI run: {RenderHtmlUrl(htmlUrl)}";

    /// <summary>Builds the dedup-append text for a log-fallback issue.</summary>
    private static string BuildLogFallbackDedupAppend(string? htmlUrl, string snippet) =>
        $"\n\n---\n[Updated {DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}]\nLog output (last 500 chars):\n{snippet}\nCI run: {RenderHtmlUrl(htmlUrl)}";

    /// <summary>Builds the description for a URL-only fallback issue (no logs or empty snippet).</summary>
    private static string BuildUrlOnlyIssueDescription(string goalId, string sha, string? htmlUrl) =>
        $"CI failed for goal '{goalId}' (commit {sha}).\n\nCI run: {RenderHtmlUrl(htmlUrl)}";

    /// <summary>Builds the dedup-append text for a URL-only fallback issue.</summary>
    private static string BuildUrlOnlyDedupAppend(string? htmlUrl) =>
        $"\n\n---\n[Updated {DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}]\nCI run: {RenderHtmlUrl(htmlUrl)}";

    /// <summary>Whether the response carries the named header at all, regardless of its value.</summary>
    private static bool HasHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out _);

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

    /// <summary>
    /// Converts the <c>X-RateLimit-Reset</c> epoch-seconds header into a non-negative wait,
    /// or <c>null</c> when the header is absent, unparseable, or outside the representable
    /// epoch range. Never throws: an out-of-range value is rejected BEFORE
    /// <see cref="DateTimeOffset.FromUnixTimeSeconds"/> would throw, so the caller falls back
    /// to the fixed default instead of the exception escaping the result-based contract.
    /// </summary>
    private static TimeSpan? ParseRateLimitReset(HttpResponseMessage response)
    {
        var value = GetHeaderValue(response, "X-RateLimit-Reset");
        if (value is null)
            return null;

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var epochSeconds))
            return null;

        if (epochSeconds < MinUnixSeconds || epochSeconds > MaxUnixSeconds)
            return null;

        var delay = DateTimeOffset.FromUnixTimeSeconds(epochSeconds) - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }
}
