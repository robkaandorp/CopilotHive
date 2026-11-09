using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="CiMonitorService"/>: GitHub check-runs polling, conclusion
/// classification, event publishing, issue creation/dedup, HTTP error handling,
/// pagination, concurrency, and cancellation semantics.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class CiMonitorServiceTests : IDisposable
{
    private const string TestToken = "test-token";

    /// <summary>The streaming parser's per-read byte budget, mirrored here for geometry assertions.</summary>
    private const int StreamChunkBytes = CiLogStreamParser.StreamChunkBytes;

    /// <summary>The streaming parser's per-line decoded-character cap, mirrored here for assertions.</summary>
    private const int MaxLogLineChars = CiLogStreamParser.MaxLogLineChars;

    /// <summary>The streaming parser's per-block decoded-character cap, mirrored here for assertions.</summary>
    private const int MaxFailureBlockChars = CiLogStreamParser.MaxFailureBlockChars;

    /// <summary>The streaming parser's retained-failure occurrence cap, mirrored here for assertions.</summary>
    private const int MaxRetainedFailureCount = CiLogStreamParser.MaxRetainedFailureCount;

    /// <summary>The streaming parser's aggregate retained-character cap, mirrored here for assertions.</summary>
    private const int MaxRetainedFailureChars = CiLogStreamParser.MaxRetainedFailureChars;

    /// <summary>Poll interval, a manual duration on the <see cref="ControlledTimeProvider"/>.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(7);

    /// <summary>
    /// CI timeout, a manual duration on the <see cref="ControlledTimeProvider"/>: it only
    /// expires when a test advances the clock past it, never on its own.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Manual CI timeout for the exhaustion tests: requests land at t = 0, 7, 14 and 21 s, and
    /// advancing the final 3 s from the pending 7 s poll delay reaches t = 24 s, where the timeout fires.
    /// </summary>
    private static readonly TimeSpan ExhaustionTimeout = TimeSpan.FromSeconds(24);

    private readonly string? _origGhToken;
    private readonly string? _origGithubToken;

    public CiMonitorServiceTests()
    {
        _origGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _origGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GH_TOKEN", TestToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _origGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _origGithubToken);
    }

    // ── Test infrastructure ────────────────────────────────────────────────

    private sealed class RecordingEventBus : IEventBus
    {
        public List<SystemEvent> Published { get; } = [];
        public event Action<SystemEvent>? OnEvent;

        public void Publish(SystemEvent evt)
        {
            Published.Add(evt);
            OnEvent?.Invoke(evt);
        }
    }

    private sealed class FakeIssueStore : IIssueStore
    {
        public Dictionary<string, Issue> Issues { get; } = new();
        public bool ThrowOnGet { get; set; }
        public bool ThrowOnCreate { get; set; }

        /// <summary>
        /// Optional hook invoked at the start of <see cref="GetIssuesAsync"/>, letting a test
        /// block inside the store (to create a controlled race) or throw a specific exception.
        /// </summary>
        public Func<CancellationToken, Task>? OnGetIssues { get; set; }

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.ToList());

        public async Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
            CancellationToken ct = default)
        {
            if (OnGetIssues is not null)
                await OnGetIssues(ct);

            if (ThrowOnGet)
                throw new InvalidOperationException("Simulated issue store failure");

            var query = Issues.Values.AsEnumerable();
            if (status.HasValue) query = query.Where(i => i.Status == status.Value);
            if (type.HasValue) query = query.Where(i => i.Type == type.Value);
            if (severity.HasValue) query = query.Where(i => i.Severity == severity.Value);
            if (repository is not null)
                query = query.Where(i => i.RepositoryNames.Any(r => string.Equals(r, repository, StringComparison.OrdinalIgnoreCase)));
            if (sourceGoalId is not null)
                query = query.Where(i => string.Equals(i.SourceGoalId, sourceGoalId, StringComparison.OrdinalIgnoreCase));
            if (linkedGoalId is not null)
                query = query.Where(i => string.Equals(i.LinkedGoalId, linkedGoalId, StringComparison.OrdinalIgnoreCase));

            return query.ToList();
        }

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.TryGetValue(issueId, out var issue) ? issue : null);

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            if (ThrowOnCreate)
                throw new InvalidOperationException("Simulated issue store failure");
            Issues[issue.Id] = issue;
            return Task.FromResult(issue);
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Issues[issue.Id] = issue;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.Remove(issueId));
    }

    /// <summary>An immutable snapshot of an outgoing request, captured before the pipeline disposes it.</summary>
    private sealed record CapturedRequest(string Url, string? AuthorizationScheme, string? AuthorizationParameter);

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        private readonly List<HttpRequestMessage> _requests = [];
        private readonly List<CapturedRequest> _captured = [];
        private readonly object _lock = new();

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get { lock (_lock) return _requests.ToList(); }
        }

        /// <summary>
        /// Snapshots of every request's URL and Authorization header, taken at send time so
        /// they survive HttpClient disposing the <see cref="HttpRequestMessage"/>.
        /// </summary>
        public IReadOnlyList<CapturedRequest> Captured
        {
            get { lock (_lock) return _captured.ToList(); }
        }

        public ScriptedHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public ScriptedHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this(req => Task.FromResult(responder(req)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _requests.Add(request);
                _captured.Add(new CapturedRequest(
                    request.RequestUri!.ToString(),
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter));
            }
            return _responder(request);
        }
    }

    private static HiveConfigFile CreateConfig(params RepositoryConfig[] repos) => new()
    {
        Repositories = [.. repos],
        Orchestrator = new OrchestratorConfig(),
    };

    private static RepositoryConfig CreateRepo(
        string name = "test-repo",
        string url = "https://github.com/org/test-repo",
        bool monitorCi = true,
        int ciTimeoutMinutes = 30) => new()
        {
            Name = name,
            Url = url,
            MonitorCi = monitorCi,
            CiTimeoutMinutes = ciTimeoutMinutes,
        };

    /// <summary>Captures every log entry so tests can assert on (and scan) log output.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Snapshot()
        {
            lock (Entries) return Entries.ToList();
        }
    }

    private static CiMonitorService CreateService(
        ScriptedHttpMessageHandler handler,
        HiveConfigFile? config = null,
        IIssueStore? issueStore = null,
        IEventBus? eventBus = null,
        ILogger<CiMonitorService>? logger = null,
        IGoalStore? goalStore = null,
        TimeSpan? startupScanWindow = null,
        TimeSpan? timeoutOverride = null,
        ControlledTimeProvider? clock = null)
    {
        clock ??= new ControlledTimeProvider();
        return new CiMonitorService(
            goalStore: goalStore,
            issueStore: issueStore,
            eventBus: eventBus,
            config: config ?? CreateConfig(CreateRepo()),
            httpClientFactory: CreateFactory(handler),
            logger: logger ?? NullLogger<CiMonitorService>.Instance,
            pollInterval: PollInterval,
            timeoutOverride: timeoutOverride ?? Timeout,
            startupScanWindow: startupScanWindow,
            timeProvider: clock);
    }

    /// <summary>
    /// Per-request handler gates for the manual-clock polling protocol. Request N (1-based)
    /// completes <see cref="Entered"/>(N) the moment it reaches the handler and then parks on its
    /// OWN response gate until <see cref="Release"/>(N) — so the first request's gate is distinct
    /// from every later one, and the test decides exactly when each response is produced.
    /// The handler never observes the request's cancellation token: cleanup must call
    /// <see cref="ReleaseAll"/> before cancelling, or a parked request would never return.
    /// </summary>
    private sealed class RequestGates
    {
        private readonly object _lock = new();
        private readonly List<TaskCompletionSource> _entered = [];
        private readonly List<TaskCompletionSource> _responseGates = [];
        private int _count;
        private bool _releasedAll;

        /// <summary>Number of requests that have reached the handler.</summary>
        public int Count { get { lock (_lock) return _count; } }

        /// <summary>Completes when request <paramref name="n"/> (1-based) has reached the handler.</summary>
        public Task Entered(int n) { lock (_lock) return EnsureLocked(n).Entered.Task; }

        /// <summary>Lets request <paramref name="n"/> (1-based) produce its response.</summary>
        public void Release(int n)
        {
            TaskCompletionSource gate;
            lock (_lock) gate = EnsureLocked(n).Gate;
            gate.TrySetResult();
        }

        /// <summary>Opens every existing and future response gate (cleanup only).</summary>
        public void ReleaseAll()
        {
            List<TaskCompletionSource> gates;
            lock (_lock)
            {
                _releasedAll = true;
                gates = [.. _responseGates];
            }
            foreach (var gate in gates) gate.TrySetResult();
        }

        /// <summary>
        /// Handler body: records the arrival of the next request, parks on its response gate, then
        /// produces the response for its 1-based sequence number (or throws, for transport faults).
        /// </summary>
        public async Task<HttpResponseMessage> EnterAsync(Func<int, HttpResponseMessage> respond)
        {
            int n;
            TaskCompletionSource entered, gate;
            lock (_lock)
            {
                n = ++_count;
                (entered, gate) = EnsureLocked(n);
            }
            entered.TrySetResult();
            await gate.Task;
            return respond(n);
        }

        private (TaskCompletionSource Entered, TaskCompletionSource Gate) EnsureLocked(int n)
        {
            while (_entered.Count < n)
            {
                _entered.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_releasedAll) gate.TrySetResult();
                _responseGates.Add(gate);
            }
            return (_entered[n - 1], _responseGates[n - 1]);
        }
    }

    /// <summary>Awaits the arrival of request <paramref name="n"/>, bounded by the hang-guard.</summary>
    private static async Task AwaitRequestEnteredAsync(RequestGates gates, int n)
    {
        try
        {
            await gates.Entered(n).WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"request {n} never reached the handler");
        }
    }

    /// <summary>
    /// Protocol steps 1–2: request 1 has entered the handler while its response gate is still
    /// closed, and at that instant the ONLY pending timer on the controlled clock is the CI
    /// timeout with exactly <paramref name="expectedTimeout"/> remaining. This proves the timeout
    /// CancellationTokenSource is registered with the injected clock (a real-time CTS leaves the
    /// snapshot empty).
    /// </summary>
    private static async Task AwaitFirstRequestWithTimeoutArmedAsync(
        RequestGates gates, ControlledTimeProvider clock, TimeSpan expectedTimeout)
    {
        await AwaitRequestEnteredAsync(gates, 1);
        Assert.Equal([expectedTimeout], clock.PendingDueTimes);
    }

    /// <summary>
    /// Rendezvous on the 7 s poll delay registered with the controlled clock. A poll delay that
    /// bypasses the clock never registers this timer and fails with the named diagnostic.
    /// </summary>
    private static async Task AwaitPollTimerAsync(ControlledTimeProvider clock)
    {
        try
        {
            await clock.WaitForPendingTimerAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail("poll timer (7 s) never registered with the controlled clock");
        }
    }

    /// <summary>
    /// Protocol steps 3–4 for request <paramref name="n"/>: release its response, rendezvous on
    /// the 7 s poll timer, advance the manual clock by exactly 7 s, and only once request
    /// <c>n + 1</c> has entered the handler assert that exactly <c>n + 1</c> requests occurred.
    /// </summary>
    private static async Task ReleaseAndPollToNextRequestAsync(
        RequestGates gates, ControlledTimeProvider clock, ScriptedHttpMessageHandler handler, int n)
    {
        gates.Release(n);
        await AwaitPollTimerAsync(clock);
        clock.Advance(TimeSpan.FromSeconds(7));
        await AwaitRequestEnteredAsync(gates, n + 1);
        Assert.Equal(n + 1, handler.Requests.Count);
    }

    /// <summary>
    /// Drives a never-terminal monitor to its <see cref="ExhaustionTimeout"/>: request 1 at
    /// t = 0 with only the 24 s timeout pending, requests 2-4 at t = 7, 14, 21 via the poll timer,
    /// then - after response 4 and the rendezvous on the next 7 s poll timer - advance 3 s to
    /// t = 24 so the timeout fires mid-delay. The monitor must then complete with exactly four
    /// requests, a fifth request must never have been issued, and no timer may be left pending.
    /// </summary>
    private static async Task RunToTimeoutExhaustionAsync(
        RequestGates gates, ControlledTimeProvider clock, ScriptedHttpMessageHandler handler, Task monitor)
    {
        await AwaitFirstRequestWithTimeoutArmedAsync(gates, clock, ExhaustionTimeout);
        for (var n = 1; n <= 3; n++)
            await ReleaseAndPollToNextRequestAsync(gates, clock, handler, n);

        gates.Release(4);
        await AwaitPollTimerAsync(clock);
        clock.Advance(TimeSpan.FromSeconds(3));

        await monitor.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);
        Assert.True(monitor.IsCompletedSuccessfully, "The monitor must return normally once the CI timeout fires.");
        Assert.False(gates.Entered(5).IsCompleted, "A fifth request was issued after the CI timeout fired.");
        Assert.Equal(0, clock.PendingTimerCount);
        Assert.Equal(4, handler.Requests.Count);
    }

    /// <summary>
    /// Best-effort teardown for a gated or parked monitor that never replaces an original test
    /// failure: open every gate/barrier first (the handlers do not observe the request token),
    /// then cancel the test-owned token, then await the monitor bounded by the hang-guard,
    /// swallowing any cleanup exception or timeout.
    /// </summary>
    private static async Task CleanupMonitorAsync(Task? monitor, CancellationTokenSource cts, params Action[] releases)
    {
        foreach (var release in releases)
        {
            try { release(); } catch { /* best-effort cleanup */ }
        }

        try { await cts.CancelAsync(); } catch { /* best-effort cleanup */ }

        if (monitor is null) return;
        try
        {
            await monitor.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
        }
        catch
        {
            // Best-effort: an original failure must never be replaced by a cleanup failure.
        }
    }

    /// <summary>
    /// The exact dedup-append stamp every builder writes, rendered from the test-owned manual
    /// clock's current time — the value the production dedup call sites must read through the
    /// service's <see cref="TimeProvider"/>.
    /// </summary>
    private static string ExpectedStamp(ControlledTimeProvider clock) =>
        $"[Updated {clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}]";

    /// <summary>
    /// Pre-seeds an open issue with the given title, source goal <c>goal-1</c> and repository
    /// <c>test-repo</c>: the issue <c>CreateOrUpdateIssueAsync</c> must find and append to.
    /// </summary>
    private static Issue SeedExistingIssue(FakeIssueStore issueStore, string title, string description)
    {
        var existingIssue = new Issue
        {
            Id = "issue-existing",
            Type = IssueType.Bug,
            Title = title,
            Description = description,
            Severity = IssueSeverity.High,
            Status = IssueStatus.Open,
            RepositoryNames = ["test-repo"],
            SourceGoalId = "goal-1",
            SourceRole = "ci",
        };
        issueStore.Issues[existingIssue.Id] = existingIssue;
        return existingIssue;
    }

    private static IHttpClientFactory CreateFactory(ScriptedHttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory.Object;
    }

    /// <summary>
    /// Creates an <see cref="IHttpClientFactory"/> that returns the same handler-backed client
    /// for every named client. Used by integration tests that drive the real
    /// <see cref="CiMonitorService.MonitorMergeAsync"/> / <see cref="CiMonitorService.StartupScanAsync"/>
    /// paths with a routing handler (e.g. <see cref="CiFailureRoutingHandler"/>) that is not a
    /// <see cref="ScriptedHttpMessageHandler"/>.
    /// </summary>
    private static IHttpClientFactory CreateFactory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory.Object;
    }

    /// <summary>
    /// Creates a <see cref="CiMonitorService"/> wired to a generic routing handler (used by the
    /// HandleCiFailureAsync integration tests).
    /// </summary>
    private static CiMonitorService CreateService(
        HttpMessageHandler handler,
        HiveConfigFile? config = null,
        IIssueStore? issueStore = null,
        IEventBus? eventBus = null,
        ILogger<CiMonitorService>? logger = null,
        IGoalStore? goalStore = null,
        TimeSpan? startupScanWindow = null,
        TimeSpan? timeoutOverride = null,
        ControlledTimeProvider? clock = null)
    {
        clock ??= new ControlledTimeProvider();
        return new CiMonitorService(
            goalStore: goalStore,
            issueStore: issueStore,
            eventBus: eventBus,
            config: config ?? CreateConfig(CreateRepo()),
            httpClientFactory: CreateFactory(handler),
            logger: logger ?? NullLogger<CiMonitorService>.Instance,
            pollInterval: PollInterval,
            timeoutOverride: timeoutOverride ?? Timeout,
            startupScanWindow: startupScanWindow,
            timeProvider: clock);
    }

    private static string CheckRunsJson(int totalCount, params (string Name, string Status, string? Conclusion, string? Summary, string? Text)[] runs)
    {
        var runObjects = runs.Select(r => new
        {
            name = r.Name,
            status = r.Status,
            conclusion = r.Conclusion,
            html_url = $"https://github.com/org/test-repo/actions/runs/{Guid.NewGuid():N}",
            output = new { summary = r.Summary, text = r.Text }
        }).ToArray();
        return JsonSerializer.Serialize(new { total_count = totalCount, check_runs = runObjects });
    }

    private static HttpResponseMessage OkResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ErrorResponse(
        HttpStatusCode status,
        string? retryAfter = null,
        string? rateLimitRemaining = null,
        string? rateLimitReset = null)
    {
        var response = new HttpResponseMessage(status);
        // Retry-After is a strongly-typed header: Headers.Add would reject a malformed value
        // outright, so tests could not reproduce the real-world "header present but garbage"
        // response. TryAddWithoutValidation stores the raw value verbatim.
        if (retryAfter is not null)
            Assert.True(response.Headers.TryAddWithoutValidation("Retry-After", retryAfter));
        if (rateLimitRemaining is not null)
            response.Headers.Add("X-RateLimit-Remaining", rateLimitRemaining);
        if (rateLimitReset is not null)
            response.Headers.Add("X-RateLimit-Reset", rateLimitReset);
        return response;
    }

    // ── Conclusion classification ──────────────────────────────────────────

    [Theory]
    [InlineData("success")]
    [InlineData("neutral")]
    public async Task MonitorMergeAsync_PassConclusions_PublishesCiSucceeded(string conclusion)
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", conclusion, null, null))));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
        Assert.Equal("goal-1", evt.GoalId);
        Assert.Equal("test-repo", evt.Repository);
    }

    [Fact]
    public async Task MonitorMergeAsync_AllSkipped_PublishesCiSucceeded()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(2,
                ("check-1", "completed", "skipped", null, null),
                ("check-2", "completed", "skipped", null, null))));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    [InlineData("action_required")]
    [InlineData("startup_failure")]
    [InlineData("stale")]
    public async Task MonitorMergeAsync_FailConclusions_PublishesCiFailed(string conclusion)
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", conclusion, "Build failed", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
        Assert.Equal("goal-1", evt.GoalId);
    }

    [Fact]
    public async Task MonitorMergeAsync_NullConclusion_TreatedAsFail()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", null, "No conclusion", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }

    [Fact]
    public async Task MonitorMergeAsync_UnknownConclusion_TreatedAsFail()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "mystery", "Unknown", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }

    // ── Monitoring ────────────────────────────────────────────────────────

    [Fact]
    public async Task MonitorMergeAsync_CiSuccess_NoIssuesCreated()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, eventBus.Published[0].Type);
        Assert.Empty(issueStore.Issues);
    }

    /// <summary>
    /// xUnit-only output (<c>✗ Name</c>). Isolated from the other two formats so deleting the
    /// xUnit parser fails this test alone.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_XUnitFormatOnly_ParsesTestName()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure",
                "✗ MyApp.Tests.XunitOnlyTests.ShouldParse", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: MyApp.Tests.XunitOnlyTests.ShouldParse", issue.Title);
    }

    /// <summary>
    /// dotnet-test-only output (<c>Failed: Name</c>) — note the colon, which distinguishes it
    /// from the MSTest format. Removing the dotnet parser fails this test alone.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_DotnetTestFormatOnly_ParsesTestName()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure",
                "Failed: MyApp.Tests.DotnetOnlyTests.ShouldParse", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: MyApp.Tests.DotnetOnlyTests.ShouldParse", issue.Title);
    }

    /// <summary>
    /// MSTest-only output (<c>Failed Name</c>, space-separated, no colon). This format was
    /// previously never exercised — removing the MSTest parser fails this test alone.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_MsTestFormatOnly_ParsesTestName()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure",
                "Failed MyApp.Tests.MsTestOnlyTests.ShouldParse", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: MyApp.Tests.MsTestOnlyTests.ShouldParse", issue.Title);
    }

    /// <summary>
    /// The <c>output.text</c> field must be parsed too, not just <c>output.summary</c> —
    /// this name appears only in <c>text</c>.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_TestNameOnlyInOutputText_IsParsed()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure",
                "1 test failed", "✗ MyApp.Tests.TextOnlyTests.ShouldParse"))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: MyApp.Tests.TextOnlyTests.ShouldParse", issue.Title);
    }

    [Fact]
    public async Task MonitorMergeAsync_CiFailureWithParseableTests_CreatesIssues()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure",
                "✗ MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum",
                "Failed: MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum"))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum", issue.Title);
        Assert.Equal(IssueType.Bug, issue.Type);
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.Equal("ci", issue.SourceRole);
        Assert.Equal("goal-1", issue.SourceGoalId);
        Assert.Equal(IssueStatus.Open, issue.Status);
        Assert.Contains("test-repo", issue.RepositoryNames);
        Assert.Contains("CI failed for goal 'goal-1' (commit abc123).", issue.Description);
        Assert.Contains("CI run:", issue.Description);
    }

    [Fact]
    public async Task MonitorMergeAsync_CiFailureCountOnly_CreatesFallbackIssuePerCheckRun()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(2,
                ("build", "completed", "failure", "Build failed with exit code 1", null),
                ("test", "completed", "failure", "Tests failed", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, eventBus.Published[0].Type);
        Assert.Equal(2, issueStore.Issues.Count);
        Assert.Contains(issueStore.Issues.Values, i => i.Title == "CI failure: build");
        Assert.Contains(issueStore.Issues.Values, i => i.Title == "CI failure: test");
    }

    [Fact]
    public async Task MonitorMergeAsync_CiFailureMixed_ParsedAndFallbackIssues()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(2,
                ("build", "completed", "failure", "✗ MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum", null),
                ("lint", "completed", "failure", "Lint failed", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, eventBus.Published[0].Type);
        Assert.Equal(2, issueStore.Issues.Count);
        Assert.Contains(issueStore.Issues.Values, i => i.Title == "CI failure: MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum");
        Assert.Contains(issueStore.Issues.Values, i => i.Title == "CI failure: lint");
    }

    /// <summary>
    /// The timeout path must be reached by genuinely polling a never-completing CI run and
    /// must NOT publish. Asserting repeated requests plus a timeout-specific log rules out a
    /// no-op implementation (which would issue 0 requests) and a premature-terminal one.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_CiTimeout_PollsRepeatedlyThenReturnsWithNoEvent()
    {
        var eventBus = new RecordingEventBus();
        var logger = new RecordingLogger<CiMonitorService>();
        var gates = new RequestGates();
        var handler = new ScriptedHttpMessageHandler(_ => gates.EnterAsync(_ =>
            OkResponse(CheckRunsJson(1, ("build", "in_progress", null, null, null)))));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, logger: logger, timeoutOverride: ExhaustionTimeout, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            monitor = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

            // Four genuine polls (t = 0, 7, 14, 21) then the manual timeout at t = 24: a no-op
            // issues zero requests and a premature terminal-return exactly one.
            await RunToTimeoutExhaustionAsync(gates, clock, handler, monitor);

            Assert.Empty(eventBus.Published);
            // The timeout branch is distinct from the caller-cancellation branch.
            Assert.Contains(logger.Snapshot(), e => e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(logger.Snapshot(), e => e.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await CleanupMonitorAsync(monitor, cts, gates.ReleaseAll);
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MonitorMergeAsync_ZeroCheckRuns_PollsUntilTimeout()
    {
        var eventBus = new RecordingEventBus();
        var gates = new RequestGates();
        var handler = new ScriptedHttpMessageHandler(_ => gates.EnterAsync(_ => OkResponse(CheckRunsJson(0))));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, timeoutOverride: ExhaustionTimeout, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            monitor = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

            await RunToTimeoutExhaustionAsync(gates, clock, handler, monitor);

            Assert.Empty(eventBus.Published);
        }
        finally
        {
            await CleanupMonitorAsync(monitor, cts, gates.ReleaseAll);
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MonitorMergeAsync_MonitorCiDisabled_NoMonitoring()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(CreateRepo(monitorCi: false));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MonitorMergeAsync_NoToken_NoMonitoring()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MonitorMergeAsync_NullIssueStore_PublishesCiFailedWithoutIssues()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "Build failed", null))));
        var service = CreateService(handler, issueStore: null, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }

    [Fact]
    public async Task MonitorMergeAsync_IssueStoreThrows_CiFailedStillPublished()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore { ThrowOnGet = true };
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "Build failed", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }

    /// <summary>
    /// Check-run-output dedup (<c>BuildDedupAppend</c>): the second call for the same goal and
    /// title appends a block stamped with EXACTLY the service clock's current time. The clock is
    /// advanced between the two calls, so the stamp must be read at append time from the
    /// service's <see cref="TimeProvider"/>, not from the wall clock or a captured value.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_DedupSameTitleAndGoal_AppendsDescription()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var clock = new ControlledTimeProvider();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "✗ MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum", "First failure"))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, clock: clock);

        // First call creates the issue.
        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.DoesNotContain("---", issue.Description);
        Assert.Equal(0, clock.PendingTimerCount);

        // Move the manual clock to a distinctive instant (sub-second ticks show in "O" format).
        clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromTicks(1_234_567));

        // Second call (same goal, same title) appends.
        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        var updated = Assert.Single(issueStore.Issues.Values);
        Assert.Contains("---", updated.Description);
        Assert.Contains($"\n\n---\n{ExpectedStamp(clock)}\n", updated.Description);
    }

    [Fact]
    public async Task MonitorMergeAsync_DedupDifferentGoal_CreatesNewIssue()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "✗ MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        await service.MonitorMergeAsync("goal-2", "test-repo", "def456", TestContext.Current.CancellationToken);

        Assert.Equal(2, issueStore.Issues.Count);
        Assert.All(issueStore.Issues.Values, i => Assert.Equal("CI failure: MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum", i.Title));
    }

    // ── HTTP errors ───────────────────────────────────────────────────────

    [Fact]
    public async Task MonitorMergeAsync_401_ReturnsAfterExactlyOneRequest()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.Unauthorized));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // Exactly one request proves 401 terminates immediately: a retrying branch would park on
        // the 7 s manual poll delay, which never fires because nothing advances the clock, so the
        // monitor would never return.
        Assert.Single(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task MonitorMergeAsync_403RateLimited_WaitsAndRetries()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.Forbidden, retryAfter: "0", rateLimitRemaining: "0");
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, clock: clock);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
        // Retry-After: 0 waits zero time: the only timer ever registered is the CI timeout, and
        // it is released once monitoring returns.
        Assert.Equal([Timeout], clock.RequestedDelays);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MonitorMergeAsync_403NonRateLimited_ReturnsAfterExactlyOneRequest()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.Forbidden));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // Exactly one request distinguishes the terminal 403 branch from the rate-limited 403
        // branch above, which retries. Turning this into a retry would produce >1 request.
        Assert.Single(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task MonitorMergeAsync_404_ReturnsAfterExactlyOneRequest()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.NotFound));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // Exactly one request proves 404 terminates rather than retrying until timeout.
        Assert.Single(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// Control test for the three terminal-return cases above: an endlessly-retried status
    /// (5xx) issues many requests within the same budget. If the terminal branches were
    /// changed to retry, their request counts would look like this one instead of 1.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_RetriedStatus_IssuesManyRequestsUnlikeTerminalBranches()
    {
        var eventBus = new RecordingEventBus();
        var gates = new RequestGates();
        var handler = new ScriptedHttpMessageHandler(_ => gates.EnterAsync(_ => ErrorResponse(HttpStatusCode.InternalServerError)));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, timeoutOverride: ExhaustionTimeout, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            monitor = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

            // A retried status polls on every 7 s tick until the manual timeout: exactly four
            // requests, where each terminal branch above issues exactly one.
            await RunToTimeoutExhaustionAsync(gates, clock, handler, monitor);

            Assert.Empty(eventBus.Published);
        }
        finally
        {
            await CleanupMonitorAsync(monitor, cts, gates.ReleaseAll);
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MonitorMergeAsync_429_WaitsAndRetries()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.TooManyRequests, retryAfter: "0");
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, clock: clock);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
        // Retry-After: 0 waits zero time: the only timer ever registered is the CI timeout, and
        // it is released once monitoring returns.
        Assert.Equal([Timeout], clock.RequestedDelays);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MonitorMergeAsync_5xx_Retries()
    {
        var eventBus = new RecordingEventBus();
        var gates = new RequestGates();
        var handler = new ScriptedHttpMessageHandler(_ => gates.EnterAsync(n =>
        {
            if (n == 1)
                return ErrorResponse(HttpStatusCode.InternalServerError);
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        }));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            monitor = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

            await AwaitFirstRequestWithTimeoutArmedAsync(gates, clock, Timeout);
            await ReleaseAndPollToNextRequestAsync(gates, clock, handler, 1);
            gates.Release(2);
            await monitor.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            Assert.Equal(2, gates.Count);
            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.CiSucceeded, evt.Type);
        }
        finally
        {
            await CleanupMonitorAsync(monitor, cts, gates.ReleaseAll);
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MonitorMergeAsync_MalformedJson_Retries()
    {
        var eventBus = new RecordingEventBus();
        var gates = new RequestGates();
        var handler = new ScriptedHttpMessageHandler(_ => gates.EnterAsync(n =>
        {
            if (n == 1)
                return OkResponse("not valid json");
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        }));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            monitor = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

            await AwaitFirstRequestWithTimeoutArmedAsync(gates, clock, Timeout);
            await ReleaseAndPollToNextRequestAsync(gates, clock, handler, 1);
            gates.Release(2);
            await monitor.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            Assert.Equal(2, gates.Count);
            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.CiSucceeded, evt.Type);
        }
        finally
        {
            await CleanupMonitorAsync(monitor, cts, gates.ReleaseAll);
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MonitorMergeAsync_TransportException_Retries()
    {
        var eventBus = new RecordingEventBus();
        var gates = new RequestGates();
        var handler = new ScriptedHttpMessageHandler(_ => gates.EnterAsync(n =>
        {
            if (n == 1)
                throw new HttpRequestException("Simulated transport failure");
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        }));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            monitor = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

            await AwaitFirstRequestWithTimeoutArmedAsync(gates, clock, Timeout);
            await ReleaseAndPollToNextRequestAsync(gates, clock, handler, 1);
            gates.Release(2);
            await monitor.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            Assert.Equal(2, gates.Count);
            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.CiSucceeded, evt.Type);
        }
        finally
        {
            await CleanupMonitorAsync(monitor, cts, gates.ReleaseAll);
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    // ── Other ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task MonitorMergeAsync_Pagination_FetchesAllPages()
    {
        var eventBus = new RecordingEventBus();
        (string Name, string Status, string? Conclusion, string? Summary, string? Text)[] page1Runs = Enumerable.Range(0, 100)
            .Select(i => (Name: "check-" + i, Status: "completed", Conclusion: (string?)"success", Summary: (string?)null, Text: (string?)null))
            .ToArray();
        (string Name, string Status, string? Conclusion, string? Summary, string? Text)[] page2Runs = Enumerable.Range(100, 50)
            .Select(i => (Name: "check-" + i, Status: "completed", Conclusion: (string?)"success", Summary: (string?)null, Text: (string?)null))
            .ToArray();

        var handler = new ScriptedHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("page=1"))
                return OkResponse(CheckRunsJson(150, page1Runs));
            return OkResponse(CheckRunsJson(150, page2Runs));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // Assert the exact ordered request inputs, not just the count: page 1 then page 2, both
        // asking for the max page size. A wrong page number or per_page value fails here.
        var urls = handler.Captured.Select(c => c.Url).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Contains("per_page=100", urls[0]);
        Assert.Contains("page=1", urls[0]);
        Assert.Contains("per_page=100", urls[1]);
        Assert.Contains("page=2", urls[1]);
        Assert.DoesNotContain("page=3", string.Join('|', urls));

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    /// <summary>
    /// A single page (<c>total_count</c> &lt;= 100) must not trigger a second request —
    /// the complement of the pagination test above.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_SinglePage_DoesNotRequestPageTwo()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var url = Assert.Single(handler.Captured).Url;
        Assert.Contains("page=1", url);
        Assert.Contains("per_page=100", url);
    }

    /// <summary>
    /// Every request must carry <c>Authorization: Bearer {token}</c>, and the token must never
    /// reach the logs. Both halves matter: the header proves authentication is attached
    /// per-request, the log scan proves the secret is not leaked.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_AttachesBearerTokenPerRequest_AndNeverLogsIt()
    {
        const string secret = "ghp_super_secret_token_value";
        Environment.SetEnvironmentVariable("GH_TOKEN", secret);

        var eventBus = new RecordingEventBus();
        var logger = new RecordingLogger<CiMonitorService>();
        var gates = new RequestGates();
        var handler = new ScriptedHttpMessageHandler(_ => gates.EnterAsync(n =>
        {
            // Force a retry so more than one request is inspected — the header must be
            // attached to every request, not only the first.
            if (n == 1)
                return ErrorResponse(HttpStatusCode.InternalServerError);
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        }));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, logger: logger, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            monitor = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

            await AwaitFirstRequestWithTimeoutArmedAsync(gates, clock, Timeout);
            await ReleaseAndPollToNextRequestAsync(gates, clock, handler, 1);
            gates.Release(2);
            await monitor.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            Assert.Equal(2, handler.Captured.Count);
            Assert.All(handler.Captured, c =>
            {
                Assert.Equal("Bearer", c.AuthorizationScheme);
                Assert.Equal(secret, c.AuthorizationParameter);
            });

            // The token must appear in no log message, no exception text, and no request URL.
            foreach (var entry in logger.Snapshot())
            {
                Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, entry.Exception?.ToString() ?? "", StringComparison.Ordinal);
            }
            Assert.All(handler.Captured, c => Assert.DoesNotContain(secret, c.Url, StringComparison.Ordinal));
        }
        finally
        {
            await CleanupMonitorAsync(monitor, cts, gates.ReleaseAll);
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MonitorGoalAsync_MultipleRepos_MonitorsAll()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(
            CreateRepo("repo-a", "https://github.com/org/repo-a"),
            CreateRepo("repo-b", "https://github.com/org/repo-b"));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorGoalAsync("goal-1", "sha1,sha2", ["repo-a", "repo-b"], TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, eventBus.Published.Count);
        Assert.All(eventBus.Published, e => Assert.Equal(EventType.CiSucceeded, e.Type));
    }

    /// <summary>
    /// Proves genuine parallelism via a rendezvous: both repos' first requests must be
    /// in flight simultaneously before either is allowed to complete. A sequential
    /// implementation deadlocks on the barrier and fails on the timeout, so this test
    /// cannot pass if <c>Task.WhenAll</c> is replaced by an awaited loop.
    /// </summary>
    [Fact]
    public async Task MonitorGoalAsync_MultipleRepos_RunConcurrently()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(
            CreateRepo("repo-a", "https://github.com/org/repo-a"),
            CreateRepo("repo-b", "https://github.com/org/repo-b"));

        var inFlight = 0;
        var bothInFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHttpMessageHandler(async _ =>
        {
            if (Interlocked.Increment(ref inFlight) == 2)
                bothInFlight.TrySetResult(true);

            // Neither request may complete until BOTH have arrived. Under sequential
            // execution the first request blocks here forever and the second never starts.
            await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, config: config, eventBus: eventBus, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            monitor = service.MonitorGoalAsync("goal-1", "sha1,sha2", ["repo-a", "repo-b"], cts.Token);
            await monitor.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            Assert.True(bothInFlight.Task.IsCompletedSuccessfully,
                "Both repositories must be monitored concurrently; the rendezvous was never reached.");
            Assert.Equal(2, eventBus.Published.Count);
            Assert.All(eventBus.Published, e => Assert.Equal(EventType.CiSucceeded, e.Type));
        }
        finally
        {
            await CleanupMonitorAsync(monitor, cts, () => bothInFlight.TrySetResult(true));
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task MonitorGoalAsync_MismatchedCounts_MonitorsMinCount()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(
            CreateRepo("repo-a", "https://github.com/org/repo-a"),
            CreateRepo("repo-b", "https://github.com/org/repo-b"));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        // 2 hashes but only 1 repo name → only 1 pair monitored.
        await service.MonitorGoalAsync("goal-1", "sha1,sha2", ["repo-a"], TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.Single(eventBus.Published);
    }

    [Fact]
    public async Task MonitorMergeAsync_InFlightDedup_SecondCallSkipped()
    {
        var eventBus = new RecordingEventBus();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(async _ =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstRequestEntered.TrySetResult();
                await gate.Task;
                return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
            }
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? first = null;
        try
        {
            first = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);
            // The first call's request has reached the handler, so it holds the in-flight key.
            await firstRequestEntered.Task.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);
            await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
            gate.SetResult(true);
            await first.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            Assert.Equal(1, callCount);
            Assert.Single(eventBus.Published);
        }
        finally
        {
            await CleanupMonitorAsync(first, cts, () => gate.TrySetResult(true));
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// Cancels at a controlled in-flight point: the handler blocks inside the FIRST request,
    /// signals the test, then waits for the cancel before returning a fully-successful,
    /// all-completed response. Without a pre-publication caller-token check the service would
    /// see a green CI result and publish <c>CiSucceeded</c> — so this fails on the race the
    /// review identified, and a no-op cannot pass either (the rendezvous asserts the request ran).
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_CallerCancelledWhileRequestInFlight_SuccessResultStillPublishesNoEvent()
    {
        var eventBus = new RecordingEventBus();
        var logger = new RecordingLogger<CiMonitorService>();
        using var requestStarted = new SemaphoreSlim(0, 1);
        var cancelObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new ScriptedHttpMessageHandler(async _ =>
        {
            requestStarted.Release();
            await cancelObserved.Task;
            // A completely green response: only a caller-token check can suppress the event.
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, eventBus: eventBus, logger: logger, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitorTask = null;
        try
        {
            monitorTask = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

            // Rendezvous: proves the implementation actually issued a request (rules out a no-op).
            Assert.True(await requestStarted.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken),
                "The service never issued a request — cancellation would be vacuous.");
            await cts.CancelAsync();
            cancelObserved.SetResult(true);
            await monitorTask.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            Assert.Empty(eventBus.Published);
            Assert.Single(handler.Requests);
            Assert.Contains(logger.Snapshot(), e => e.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
            // Caller cancellation is authoritative — it must not be reported as a CI timeout.
            Assert.DoesNotContain(logger.Snapshot(), e => e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await CleanupMonitorAsync(monitorTask, cts, () => cancelObserved.TrySetResult(true));
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// Same race, failure flavour: cancellation lands while the failure path is creating issues.
    /// The store's first call blocks until cancelled and then throws an OCE — which must be
    /// treated as cancellation (no event), not as an issue-store failure (which publishes).
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_CallerCancelledDuringIssueCreation_PublishesNoEvent()
    {
        var eventBus = new RecordingEventBus();
        var logger = new RecordingLogger<CiMonitorService>();
        using var issueCallStarted = new SemaphoreSlim(0, 1);
        var cancelObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var issueStore = new FakeIssueStore
        {
            OnGetIssues = async ct =>
            {
                issueCallStarted.Release();
                await cancelObserved.Task;
                ct.ThrowIfCancellationRequested();
            }
        };
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "Build failed", null))));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, logger: logger, clock: clock);

        Task? monitorTask = null;
        try
        {
            monitorTask = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

            Assert.True(await issueCallStarted.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken),
                "The failure path never reached the issue store — cancellation would be vacuous.");
            await cts.CancelAsync();
            cancelObserved.SetResult(true);
            await monitorTask.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            // The OCE must NOT be swallowed as a store failure and turned into a CiFailed event.
            Assert.Empty(eventBus.Published);
            Assert.Empty(issueStore.Issues);
            Assert.Contains(logger.Snapshot(), e => e.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await CleanupMonitorAsync(monitorTask, cts, () => cancelObserved.TrySetResult(true));
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// The complement of the test above: a NON-cancellation issue-store exception must still
    /// guarantee <c>CiFailed</c>. Together these prove the two exception classes are handled
    /// differently rather than lumped into one catch-all.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_IssueStoreThrowsOceWithoutCallerCancellation_StillPublishesCiFailed()
    {
        var eventBus = new RecordingEventBus();
        // An OCE whose token is NOT the caller's token is a store failure, not a cancellation.
        var issueStore = new FakeIssueStore
        {
            OnGetIssues = _ => throw new OperationCanceledException("store-internal cancellation")
        };
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "Build failed", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }

    [Fact]
    public async Task MonitorMergeAsync_NonGitHubUrl_NoMonitoring()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(CreateRepo("test-repo", "https://gitlab.com/org/test-repo"));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MonitorMergeAsync_MalformedUrl_NoMonitoring()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(CreateRepo("test-repo", "https://github.com/only-owner"));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MonitorMergeAsync_SshUrl_ParsesOwnerAndRepo()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(CreateRepo("test-repo", "git@github.com:org/test-repo.git"));
        var handler = new ScriptedHttpMessageHandler(req =>
        {
            Assert.Contains("/repos/org/test-repo/commits/", req.RequestUri!.ToString());
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    [Fact]
    public async Task MonitorMergeAsync_HttpsUrlWithGitSuffix_ParsesOwnerAndRepo()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(CreateRepo("test-repo", "https://github.com/org/test-repo.git"));
        var handler = new ScriptedHttpMessageHandler(req =>
        {
            Assert.Contains("/repos/org/test-repo/commits/", req.RequestUri!.ToString());
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    [Fact]
    public async Task MonitorMergeAsync_HttpsUrlWithTrailingSlash_ParsesOwnerAndRepo()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(CreateRepo("test-repo", "https://github.com/org/test-repo/"));
        var handler = new ScriptedHttpMessageHandler(req =>
        {
            Assert.Contains("/repos/org/test-repo/commits/", req.RequestUri!.ToString());
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    // ── Startup scan ───────────────────────────────────────────────────────

    /// <summary>
    /// Records every <see cref="CiMonitorService.StartBackgroundMonitoring"/> launch (the startup
    /// scan's fire-and-forget hand-off) synchronously instead of performing it, so once
    /// <see cref="CiMonitorService.StartupScanAsync"/> returns, tests can assert exactly — with
    /// no waits — whether background monitoring was started and with which cancellation token.
    /// The real hand-off to <see cref="CiMonitorService.MonitorMergeAsync"/> is covered by
    /// <see cref="StartBackgroundMonitoring_ForwardsToMonitorMergeAsyncWithScanToken"/>.
    /// </summary>
    private sealed class MonitorRecordingService : CiMonitorService
    {
        public MonitorRecordingService(
            IGoalStore? goalStore,
            HiveConfigFile config,
            IHttpClientFactory httpClientFactory,
            IEventBus? eventBus = null,
            TimeSpan? startupScanWindow = null,
            ControlledTimeProvider? clock = null)
            : this(clock ?? new ControlledTimeProvider(), goalStore, config, httpClientFactory, eventBus, startupScanWindow)
        {
        }

        private MonitorRecordingService(
            ControlledTimeProvider clock,
            IGoalStore? goalStore,
            HiveConfigFile config,
            IHttpClientFactory httpClientFactory,
            IEventBus? eventBus,
            TimeSpan? startupScanWindow)
            : base(
                goalStore: goalStore,
                eventBus: eventBus,
                config: config,
                httpClientFactory: httpClientFactory,
                pollInterval: PollInterval,
                timeoutOverride: Timeout,
                startupScanWindow: startupScanWindow,
                timeProvider: clock)
        {
            Clock = clock;
        }

        /// <summary>The manual clock this service was constructed with.</summary>
        public ControlledTimeProvider Clock { get; }

        public ConcurrentBag<(string GoalId, string Repo, string Sha, CancellationToken Token)> Monitored { get; } = [];

        internal override void StartBackgroundMonitoring(string goalId, string repoName, string sha, CancellationToken ct) =>
            Monitored.Add((goalId, repoName, sha, ct));
    }

    /// <summary>
    /// Overrides ONLY <see cref="CiMonitorService.MonitorMergeAsync"/>, so the REAL base
    /// <see cref="CiMonitorService.StartBackgroundMonitoring"/> runs and its forwarded arguments
    /// can be observed.
    /// </summary>
    private sealed class MonitorForwardingService : CiMonitorService
    {
        public MonitorForwardingService(
            IGoalStore goalStore,
            HiveConfigFile config,
            IHttpClientFactory httpClientFactory,
            IEventBus eventBus,
            ControlledTimeProvider clock)
            : base(
                goalStore: goalStore,
                eventBus: eventBus,
                config: config,
                httpClientFactory: httpClientFactory,
                pollInterval: PollInterval,
                timeoutOverride: Timeout,
                timeProvider: clock)
        {
        }

        /// <summary>Completes with the arguments of the first forwarded monitoring call.</summary>
        public TaskCompletionSource<(string GoalId, string Repo, string Sha, CancellationToken Token)> Forwarded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task MonitorMergeAsync(string goalId, string repoName, string mergeCommitSha, CancellationToken ct)
        {
            Forwarded.TrySetResult((goalId, repoName, mergeCommitSha, ct));
            return Task.CompletedTask;
        }
    }

    private static MonitorRecordingService CreateRecordingService(
        ScriptedHttpMessageHandler handler,
        IGoalStore? goalStore,
        HiveConfigFile? config = null,
        IEventBus? eventBus = null,
        TimeSpan? startupScanWindow = null,
        ControlledTimeProvider? clock = null) =>
        new(goalStore, config ?? CreateConfig(CreateRepo()), CreateFactory(handler), eventBus, startupScanWindow, clock);

    /// <summary>
    /// The fixed "now" of every fresh <see cref="ControlledTimeProvider"/>: the default
    /// <see cref="Goal.CompletedAt"/> of <see cref="CompletedGoal"/>, so goals built without an
    /// explicit completion time are deterministically "just merged" on any fresh manual clock.
    /// </summary>
    private static readonly DateTime ClockEpoch = new ControlledTimeProvider().GetUtcNow().UtcDateTime;

    private static Goal CompletedGoal(
        string id = "goal-1",
        string? mergeCommitHash = "abc123",
        DateTime? completedAt = null,
        GoalStatus status = GoalStatus.Completed,
        params string[] repositoryNames) => new()
        {
            Id = id,
            Description = "test goal",
            Status = status,
            MergeCommitHash = mergeCommitHash,
            CompletedAt = completedAt ?? ClockEpoch,
            RepositoryNames = repositoryNames.Length == 0 ? ["test-repo"] : [.. repositoryNames],
        };

    private static InMemoryGoalStore StoreWith(params Goal[] goals)
    {
        var store = new InMemoryGoalStore();
        foreach (var goal in goals)
            store.AddGoal(goal);
        return store;
    }

    [Fact]
    public async Task StartupScanAsync_CompletedGoalCiSucceeded_PublishesCiSucceeded()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, eventBus: eventBus, goalStore: StoreWith(CompletedGoal()));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
        Assert.Equal("goal-1", evt.GoalId);
        Assert.Equal("test-repo", evt.Repository);
    }

    /// <summary>
    /// <see cref="CiMonitorService.ScanGoalAsync"/> must pair merge hashes with TARGET
    /// repositories (resolved via <see cref="Goal.ResolveTargetRepositoryNames"/>), not all
    /// <see cref="Goal.RepositoryNames"/>. A goal with three repositories but two targets
    /// must probe exactly the two target repos — the source-only repo must never be probed.
    /// </summary>
    [Fact]
    public async Task ScanGoalAsync_HashesPairedWithTargets_NotAllRepositories()
    {
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var goal = new Goal
        {
            Id = "goal-target-scan",
            Description = "target pairing test",
            Status = GoalStatus.Completed,
            MergeCommitHash = "sha-b,sha-a",
            CompletedAt = clock.GetUtcNow().UtcDateTime,
            // Three repositories, but only repo-b and repo-a are targets (in that order).
            RepositoryNames = ["repo-a", "repo-b", "repo-c"],
            TargetRepositoryNames = "repo-b, repo-a",
        };
        var config = CreateConfig(
            CreateRepo("repo-a"),
            CreateRepo("repo-b"),
            CreateRepo("repo-c"));
        var service = CreateService(handler, config: config, eventBus: eventBus,
            goalStore: StoreWith(goal), clock: clock);

        await service.ScanGoalAsync(goal, TestContext.Current.CancellationToken);

        // Exactly two probes — one per target repo. The source-only repo-c is never probed.
        Assert.Equal(2, handler.Requests.Count);
        var events = eventBus.Published;
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(EventType.CiSucceeded, e.Type));
        Assert.Contains(events, e => e.Repository == "repo-a");
        Assert.Contains(events, e => e.Repository == "repo-b");
        Assert.DoesNotContain(events, e => e.Repository == "repo-c");
    }

    [Fact]
    public async Task StartupScanAsync_CompletedGoalCiFailed_CreatesIssuesAndPublishesCiFailed()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "✗ MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, goalStore: StoreWith(CompletedGoal()));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
        Assert.Equal("goal-1", evt.GoalId);
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum", issue.Title);
        Assert.Equal("goal-1", issue.SourceGoalId);
    }

    /// <summary>
    /// A commit whose checks are still running must hand off to background monitoring rather
    /// than publishing a terminal event. The token handed to the launched monitor must be the
    /// application-lifetime token the scan was given — not <see cref="CancellationToken.None"/> —
    /// so shutdown stops the resumed monitoring.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_CiStillRunning_StartsMonitoringWithScanToken()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "in_progress", null, null, null))));
        var service = CreateRecordingService(handler, StoreWith(CompletedGoal()), eventBus: eventBus);

        using var cts = new CancellationTokenSource();
        await service.StartupScanAsync(cts.Token);

        // The hand-off is recorded synchronously, so this is exact once the scan has returned.
        var monitored = Assert.Single(service.Monitored);
        Assert.Equal(("goal-1", "test-repo", "abc123"), (monitored.GoalId, monitored.Repo, monitored.Sha));
        Assert.Equal(cts.Token, monitored.Token);
        Assert.True(monitored.Token.CanBeCanceled);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// The REAL <see cref="CiMonitorService.StartBackgroundMonitoring"/>, reached through a
    /// still-running startup scan, must forward the goal, repository, commit and the scan's own
    /// token to <see cref="CiMonitorService.MonitorMergeAsync"/>. This covers the hand-off the
    /// <see cref="MonitorRecordingService"/> replaces in the other startup-scan tests.
    /// </summary>
    [Fact]
    public async Task StartBackgroundMonitoring_ForwardsToMonitorMergeAsyncWithScanToken()
    {
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "in_progress", null, null, null))));
        var goal = CompletedGoal(completedAt: clock.GetUtcNow().UtcDateTime);
        var service = new MonitorForwardingService(
            StoreWith(goal), CreateConfig(CreateRepo()), CreateFactory(handler), eventBus, clock);

        using var cts = new CancellationTokenSource();
        await service.StartupScanAsync(cts.Token);

        // Positive signal from the fire-and-forget task: a bounded hang-guard is appropriate.
        var forwarded = await service.Forwarded.Task.WaitAsync(
            ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(("goal-1", "test-repo", "abc123"), (forwarded.GoalId, forwarded.Repo, forwarded.Sha));
        Assert.Equal(cts.Token, forwarded.Token);
        Assert.Single(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// A no-checks commit merged one tick MORE than the 5-minute grace period ago is skipped:
    /// the grace predicate is a strict <c>&lt;</c>, so this is the first instant past the boundary.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_NoChecksAndMergedLongAgo_SkipsMonitoring()
    {
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(0)));
        var goal = CompletedGoal(
            completedAt: clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        var service = CreateRecordingService(handler, StoreWith(goal), eventBus: eventBus, clock: clock);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        // The commit WAS probed — the skip is a decision about the result, not a no-op.
        Assert.Single(handler.Requests);
        Assert.Empty(service.Monitored);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// Complement of the test above: the identical no-checks response for a commit merged
    /// EXACTLY 5 minutes ago must start monitoring — the strict <c>&lt;</c> grace predicate keeps
    /// the boundary itself inside the grace period — proving the grace period is what decides.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_NoChecksAndRecentlyMerged_StartsMonitoring()
    {
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(0)));
        var goal = CompletedGoal(completedAt: clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5));
        var service = CreateRecordingService(handler, StoreWith(goal), eventBus: eventBus, clock: clock);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        var monitored = Assert.Single(service.Monitored);
        Assert.Equal("abc123", monitored.Sha);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task StartupScanAsync_401_SkipsWithoutMonitoring()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.Unauthorized));
        var service = CreateRecordingService(handler, StoreWith(CompletedGoal()), eventBus: eventBus);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.Empty(service.Monitored);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// Complement of the 401 test: a retryable status must resume monitoring rather than being
    /// abandoned, so the two error classes cannot be collapsed into one branch.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_5xx_StartsMonitoring()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.InternalServerError));
        var service = CreateRecordingService(handler, StoreWith(CompletedGoal()), eventBus: eventBus);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(service.Monitored);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task StartupScanAsync_GoalWithoutMergeHash_NotScanned()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, eventBus: eventBus, goalStore: StoreWith(CompletedGoal(mergeCommitHash: null)));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// A goal completed one tick BEFORE the 10-minute window's cutoff is not scanned: the
    /// window predicate is an inclusive <c>&gt;=</c>, so this is the first instant outside it.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_GoalCompletedOutsideWindow_NotScanned()
    {
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var goal = CompletedGoal(
            completedAt: clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        var service = CreateService(
            handler, eventBus: eventBus, goalStore: StoreWith(goal), startupScanWindow: TimeSpan.FromMinutes(10),
            clock: clock);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// The complement of the out-of-window test: the same goal completed EXACTLY at the
    /// 10-minute cutoff IS scanned (the inclusive <c>&gt;=</c> boundary), so an empty result
    /// cannot come from the scan being broken outright.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_GoalCompletedInsideWindow_IsScanned()
    {
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var goal = CompletedGoal(completedAt: clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(10));
        var service = CreateService(
            handler, eventBus: eventBus, goalStore: StoreWith(goal), startupScanWindow: TimeSpan.FromMinutes(10),
            clock: clock);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.Single(eventBus.Published);
    }

    [Theory]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.InProgress)]
    [InlineData(GoalStatus.Pending)]
    public async Task StartupScanAsync_NonCompletedGoal_NotScanned(GoalStatus status)
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, eventBus: eventBus, goalStore: StoreWith(CompletedGoal(status: status)));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task StartupScanAsync_MultipleGoals_EachScanned()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var store = StoreWith(
            CompletedGoal("goal-1", "sha-one"),
            CompletedGoal("goal-2", "sha-two"));
        var service = CreateService(handler, eventBus: eventBus, goalStore: store);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        var urls = handler.Captured.Select(c => c.Url).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Contains(urls, u => u.Contains("/commits/sha-one/"));
        Assert.Contains(urls, u => u.Contains("/commits/sha-two/"));
        Assert.Equal(2, eventBus.Published.Count);
        Assert.Equal(["goal-1", "goal-2"], eventBus.Published.Select(e => e.GoalId).Order().ToList());
    }

    [Fact]
    public async Task StartupScanAsync_MultiRepoGoal_EachPairProbed()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(
            CreateRepo("repo-a", "https://github.com/org/repo-a"),
            CreateRepo("repo-b", "https://github.com/org/repo-b"));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var goal = CompletedGoal("goal-1", "sha-a,sha-b", repositoryNames: ["repo-a", "repo-b"]);
        var service = CreateService(handler, config: config, eventBus: eventBus, goalStore: StoreWith(goal));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        var urls = handler.Captured.Select(c => c.Url).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Contains(urls, u => u.Contains("/repos/org/repo-a/commits/sha-a/"));
        Assert.Contains(urls, u => u.Contains("/repos/org/repo-b/commits/sha-b/"));
        Assert.Equal(2, eventBus.Published.Count);
        Assert.Equal(["repo-a", "repo-b"], eventBus.Published.Select(e => e.Repository).Order().ToList());
    }

    /// <summary>
    /// Re-running the scan must not republish an already-published terminal event. The request
    /// count proves the second scan really re-probed, so the single event comes from the event
    /// dedup and not from the second scan being skipped wholesale.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_RunTwice_PublishesCiSucceededOnce()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, eventBus: eventBus, goalStore: StoreWith(CompletedGoal()));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);
        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    [Fact]
    public async Task StartupScanAsync_FailedCiRunTwice_PublishesCiFailedOnce()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "Build failed", null))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, goalStore: StoreWith(CompletedGoal()));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);
        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }

    /// <summary>
    /// A live <see cref="CiMonitorService.MonitorMergeAsync"/> holds the in-flight key for the
    /// goal/commit/repository, so a concurrent startup scan that finds the same commit already
    /// failed must create no issues and publish no event. The scan's own probe still happens,
    /// so the skip is proven to come from the in-flight guard rather than from a missing probe.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_InFlightMonitoring_SkipsIssuesAndEvent()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var requestCount = 0;
        var monitorRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMonitor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new ScriptedHttpMessageHandler(async _ =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                // The live monitor's request: park here so the in-flight key stays held while
                // the startup scan runs.
                monitorRequestStarted.TrySetResult();
                await releaseMonitor.Task;
                return OkResponse(CheckRunsJson(1, ("build", "in_progress", null, null, null)));
            }
            // The startup scan's request: CI has already failed.
            return OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "Build failed", null)));
        });
        var clock = new ControlledTimeProvider();
        var service = CreateService(
            handler, issueStore: issueStore, eventBus: eventBus, goalStore: StoreWith(CompletedGoal()), clock: clock);

        using var monitorCts = new CancellationTokenSource();
        Task? monitorTask = null;
        try
        {
            monitorTask = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", monitorCts.Token);
            await monitorRequestStarted.Task.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            await service.StartupScanAsync(TestContext.Current.CancellationToken);

            // Exactly the live monitor's parked request plus the scan's own probe.
            Assert.Equal(2, Volatile.Read(ref requestCount));
            Assert.Empty(issueStore.Issues);
            Assert.Empty(eventBus.Published);
        }
        finally
        {
            // Best-effort teardown that never replaces an original failure. Cancelling first
            // means the live monitor's still-running result ends the loop without publishing
            // anything of its own; releasing then unblocks its parked request.
            try { await monitorCts.CancelAsync(); } catch { /* best-effort cleanup */ }
            try { releaseMonitor.TrySetResult(); } catch { /* best-effort cleanup */ }
            if (monitorTask is not null)
            {
                try
                {
                    await monitorTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                }
                catch
                {
                    // Best-effort: an original failure must never be replaced by a cleanup failure.
                }
            }
        }

        Assert.True(monitorTask.IsCompletedSuccessfully, "The live monitor must return normally once cancelled and released.");
        Assert.Empty(eventBus.Published);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task StartupScanAsync_NullGoalStore_ReturnsEarly()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, eventBus: eventBus, goalStore: null);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task StartupScanAsync_MonitorCiDisabled_NotScanned()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(CreateRepo(monitorCi: false));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, config: config, eventBus: eventBus, goalStore: StoreWith(CompletedGoal()));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    // ── Probe classification (internal ProbeCiStatusAsync) ─────────────────

    private static async Task<CiProbeResult> ProbeAsync(ScriptedHttpMessageHandler handler, ControlledTimeProvider? clock = null)
    {
        var service = CreateService(handler, clock: clock ?? new ControlledTimeProvider());
        using var client = new HttpClient(handler, disposeHandler: false);
        return await service.ProbeCiStatusAsync("org", "test-repo", "abc123", client, TestContext.Current.CancellationToken);
    }

    /// <summary>Builds a check-runs response body from a raw <c>check_runs</c> array literal.</summary>
    private static string RawCheckRunsJson(int totalCount, string checkRunsArrayJson) =>
        $"{{\"total_count\":{totalCount},\"check_runs\":{checkRunsArrayJson}}}";

    [Fact]
    public async Task ProbeCiStatusAsync_AllSuccess_ReturnsSucceededWithAllRuns()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(2,
            ("build", "completed", "success", null, null),
            ("lint", "completed", "success", null, null))));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Succeeded, result.Status);
        Assert.Equal(["build", "lint"], result.CheckRuns.Select(r => r.Name).Order().ToList());
        Assert.Null(result.ErrorDetail);
        Assert.Null(result.RetryAfter);
    }

    /// <summary>
    /// Precedence: an incomplete run outranks an already-failed one. Classifying this as
    /// <c>Failed</c> would publish a terminal event while CI is still running.
    /// </summary>
    [Fact]
    public async Task ProbeCiStatusAsync_FailureAndInProgress_ReturnsStillRunning()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(2,
            ("build", "completed", "failure", "Build failed", null),
            ("lint", "in_progress", null, null, null))));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.StillRunning, result.Status);
        Assert.Equal(2, result.CheckRuns.Count);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_OneFailureAllCompleted_ReturnsFailedWithOnlyFailedRuns()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(3,
            ("build", "completed", "success", null, null),
            ("test", "completed", "failure", "Tests failed", null),
            ("lint", "completed", "success", null, null))));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Failed, result.Status);
        var run = Assert.Single(result.CheckRuns);
        Assert.Equal("test", run.Name);
        Assert.Equal("Tests failed", run.OutputSummary);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_SomeNotCompleted_ReturnsStillRunningWithAllRuns()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(3,
            ("build", "completed", "success", null, null),
            ("test", "queued", null, null, null),
            ("lint", "in_progress", null, null, null))));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.StillRunning, result.Status);
        Assert.Equal(3, result.CheckRuns.Count);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_AllSkipped_ReturnsSucceeded()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(2,
            ("build", "completed", "skipped", null, null),
            ("lint", "completed", "skipped", null, null))));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Succeeded, result.Status);
        Assert.Equal(2, result.CheckRuns.Count);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_ZeroChecks_ReturnsNoChecks()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(0)));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.NoChecks, result.Status);
        Assert.Empty(result.CheckRuns);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_401_ReturnsTerminalError()
    {
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.Unauthorized));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("401", result.ErrorDetail);
        Assert.Null(result.RetryAfter);
        Assert.Empty(result.CheckRuns);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_403WithRetryAfter_ReturnsRateLimitErrorWithRetryAfter()
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, retryAfter: "60"));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.Equal(TimeSpan.FromSeconds(60), result.RetryAfter);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_403WithRateLimitReset_ReturnsRetryAfterUntilReset()
    {
        var clock = new ControlledTimeProvider();
        var resetEpoch = clock.GetUtcNow().AddSeconds(120).ToUnixTimeSeconds();
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, rateLimitRemaining: "0",
                rateLimitReset: resetEpoch.ToString(CultureInfo.InvariantCulture)));

        var result = await ProbeAsync(handler, clock);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
    }

    /// <summary>A reset timestamp already in the past must clamp to zero, never a negative delay.</summary>
    [Fact]
    public async Task ProbeCiStatusAsync_403WithPastRateLimitReset_ClampsRetryAfterToZero()
    {
        var clock = new ControlledTimeProvider();
        var resetEpoch = clock.GetUtcNow().AddSeconds(-120).ToUnixTimeSeconds();
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, rateLimitRemaining: "0",
                rateLimitReset: resetEpoch.ToString(CultureInfo.InvariantCulture)));

        var result = await ProbeAsync(handler, clock);

        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.Equal(TimeSpan.Zero, result.RetryAfter);
    }

    /// <summary>
    /// The HTTP-date form of <c>Retry-After</c> is converted to the exact wait until that date,
    /// measured against the service's clock.
    /// </summary>
    [Fact]
    public async Task ProbeCiStatusAsync_403WithHttpDateRetryAfter_ReturnsExactDelayUntilDate()
    {
        var clock = new ControlledTimeProvider();
        var retryAt = (clock.GetUtcNow() + TimeSpan.FromSeconds(90)).ToString("R", CultureInfo.InvariantCulture);
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, retryAfter: retryAt));

        var result = await ProbeAsync(handler, clock);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.Equal(TimeSpan.FromSeconds(90), result.RetryAfter);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_403WithRateLimitRemainingOnly_ReturnsDefaultRetryAfter()
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, rateLimitRemaining: "0"));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.Equal(TimeSpan.FromSeconds(60), result.RetryAfter);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_403WithoutRateLimitHeaders_ReturnsTerminalError()
    {
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.Forbidden));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("403", result.ErrorDetail);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_429_ReturnsRetryableErrorWithRetryAfter()
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.TooManyRequests, retryAfter: "120"));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("429", result.ErrorDetail);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_5xx_ReturnsRetryableError()
    {
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.BadGateway));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("5xx", result.ErrorDetail);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_400_ReturnsOtherHttpError()
    {
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.BadRequest));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("other-http", result.ErrorDetail);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_TransportException_ReturnsTransportError()
    {
        var handler = new ScriptedHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("Simulated transport failure")));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("transport", result.ErrorDetail);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_MalformedJson_ReturnsMalformedError()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse("not valid json"));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("malformed", result.ErrorDetail);
        Assert.Empty(result.CheckRuns);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_MissingCheckRunsArray_ReturnsMalformedError()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse("{\"total_count\":2}"));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("malformed", result.ErrorDetail);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_MissingTotalCount_ReturnsMalformedError()
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse("{\"check_runs\":[{\"name\":\"build\",\"status\":\"completed\",\"conclusion\":\"success\"}]}"));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("malformed", result.ErrorDetail);
    }

    /// <summary>
    /// One unusable run must not discard the usable ones: the valid run still decides the
    /// outcome and the malformed entry is dropped rather than treated as a failure.
    /// </summary>
    [Fact]
    public async Task ProbeCiStatusAsync_SomeRunsMalformed_UsesValidRunsAndSkipsMalformed()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(RawCheckRunsJson(3, """
            [
              {"status":"completed","conclusion":"failure"},
              {"name":"no-status","conclusion":"failure"},
              {"name":"build","status":"completed","conclusion":"success"}
            ]
            """)));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Succeeded, result.Status);
        var run = Assert.Single(result.CheckRuns);
        Assert.Equal("build", run.Name);
    }

    /// <summary>
    /// Complement of the partially-malformed case: when NO run survives parsing, the response
    /// carries no usable information and must be retried rather than reported as "no checks".
    /// </summary>
    [Fact]
    public async Task ProbeCiStatusAsync_AllRunsMalformed_ReturnsMalformedError()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(RawCheckRunsJson(2, """
            [
              {"status":"completed","conclusion":"failure"},
              {"name":"no-status","conclusion":"success"}
            ]
            """)));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("malformed", result.ErrorDetail);
        Assert.Empty(result.CheckRuns);
    }

    [Fact]
    public async Task ProbeCiStatusAsync_Pagination_FetchesAllPagesAndAggregatesRuns()
    {
        (string Name, string Status, string? Conclusion, string? Summary, string? Text)[] page1Runs = [.. Enumerable.Range(0, 100)
            .Select(i => (Name: "check-" + i, Status: "completed", Conclusion: (string?)"success", Summary: (string?)null, Text: (string?)null))];
        (string Name, string Status, string? Conclusion, string? Summary, string? Text)[] page2Runs = [.. Enumerable.Range(100, 50)
            .Select(i => (Name: "check-" + i, Status: "completed", Conclusion: (string?)"success", Summary: (string?)null, Text: (string?)null))];

        var handler = new ScriptedHttpMessageHandler(req =>
            OkResponse(req.RequestUri!.ToString().Contains("&page=1", StringComparison.Ordinal)
                ? CheckRunsJson(150, page1Runs)
                : CheckRunsJson(150, page2Runs)));

        var result = await ProbeAsync(handler);

        var urls = handler.Captured.Select(c => c.Url).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Contains("per_page=100", urls[0]);
        Assert.Contains("page=1", urls[0]);
        Assert.Contains("per_page=100", urls[1]);
        Assert.Contains("page=2", urls[1]);
        Assert.Equal(CiProbeStatus.Succeeded, result.Status);
        Assert.Equal(150, result.CheckRuns.Count);
    }

    /// <summary>
    /// Pagination must also aggregate across pages when deciding the outcome: a failure that
    /// only appears on page 2 must still produce <c>Failed</c>.
    /// </summary>
    [Fact]
    public async Task ProbeCiStatusAsync_FailureOnSecondPage_ReturnsFailed()
    {
        (string Name, string Status, string? Conclusion, string? Summary, string? Text)[] page1Runs = [.. Enumerable.Range(0, 100)
            .Select(i => (Name: "check-" + i, Status: "completed", Conclusion: (string?)"success", Summary: (string?)null, Text: (string?)null))];
        (string Name, string Status, string? Conclusion, string? Summary, string? Text)[] page2Runs =
            [("late-check", "completed", "failure", "Boom", null)];

        var handler = new ScriptedHttpMessageHandler(req =>
            OkResponse(req.RequestUri!.ToString().Contains("&page=1", StringComparison.Ordinal)
                ? CheckRunsJson(101, page1Runs)
                : CheckRunsJson(101, page2Runs)));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Failed, result.Status);
        var run = Assert.Single(result.CheckRuns);
        Assert.Equal("late-check", run.Name);
    }

    // ── Regression: malformed rate-limit headers must stay retryable ───────

    /// <summary>
    /// A 403 whose <c>Retry-After</c> value is unparseable is still a rate-limit response:
    /// detection must key on header PRESENCE. Classifying it as a terminal 403 would abandon
    /// CI monitoring permanently because GitHub sent a garbage back-off value.
    /// </summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("60s")]
    [InlineData("-")]
    public async Task ProbeCiStatusAsync_403WithMalformedRetryAfter_StaysRateLimitedWithDefaultWait(string retryAfter)
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, retryAfter: retryAfter));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        // The malformed value must NOT downgrade the classification to terminal "403".
        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.Equal(TimeSpan.FromSeconds(60), result.RetryAfter);
    }

    /// <summary>
    /// With a malformed <c>Retry-After</c> the reset header is still honoured for the wait
    /// duration — parsing decides only HOW LONG to back off, never WHETHER to back off.
    /// </summary>
    [Fact]
    public async Task ProbeCiStatusAsync_403MalformedRetryAfterWithReset_UsesResetForWait()
    {
        var clock = new ControlledTimeProvider();
        var resetEpoch = clock.GetUtcNow().AddSeconds(120).ToUnixTimeSeconds();
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, retryAfter: "not-a-number",
                rateLimitReset: resetEpoch.ToString(CultureInfo.InvariantCulture)));

        var result = await ProbeAsync(handler, clock);

        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
    }

    /// <summary>
    /// Complement of the malformed-value tests: only a 403 carrying NEITHER header is terminal.
    /// Together these prove presence — not parseability — is what separates the two branches.
    /// </summary>
    [Fact]
    public async Task ProbeCiStatusAsync_403WithUnrelatedHeadersOnly_RemainsTerminal()
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, rateLimitRemaining: "42"));

        var result = await ProbeAsync(handler);

        Assert.Equal("403", result.ErrorDetail);
        Assert.Null(result.RetryAfter);
    }

    /// <summary>
    /// An epoch value beyond <see cref="DateTimeOffset"/>'s range parses as a number but would
    /// throw inside <c>FromUnixTimeSeconds</c>. It must be rejected before that call so the
    /// result-based error contract holds and the caller still gets a usable back-off.
    /// </summary>
    [Theory]
    [InlineData("99999999999999")]
    [InlineData("9223372036854775807")]
    [InlineData("soon")]
    public async Task ProbeCiStatusAsync_403WithUnusableReset_FallsBackToDefaultWait(string reset)
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, rateLimitRemaining: "0", rateLimitReset: reset));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.Equal(TimeSpan.FromSeconds(60), result.RetryAfter);
    }

    /// <summary>Every rate-limited RetryAfter must be usable as a delay — never negative.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("1000000000")]
    public async Task ProbeCiStatusAsync_403WithAnyReset_NeverReturnsNegativeRetryAfter(string reset)
    {
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, rateLimitRemaining: "0", rateLimitReset: reset));

        var result = await ProbeAsync(handler);

        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.NotNull(result.RetryAfter);
        Assert.True(result.RetryAfter!.Value >= TimeSpan.Zero,
            $"RetryAfter must be non-negative; got {result.RetryAfter}.");
    }

    // ── Regression: malformed total_count must not escape the contract ─────

    /// <summary>
    /// <c>JsonValueKind.Number</c> does not guarantee <c>GetInt32()</c> succeeds: fractional and
    /// out-of-range numbers throw, and a negative value corrupts the pagination arithmetic.
    /// Every such value must surface as <c>Error("malformed")</c> rather than an exception
    /// escaping the probe or a silent fallback to zero.
    /// </summary>
    [Theory]
    [InlineData("-1")]           // negative → invalid pagination
    [InlineData("-100")]
    [InlineData("1.5")]          // fractional → GetInt32 throws
    [InlineData("1e30")]         // out of Int32 range → GetInt32 throws
    [InlineData("99999999999")]  // out of Int32 range
    [InlineData("\"12\"")]       // string kind, not a number
    [InlineData("null")]
    [InlineData("true")]
    public async Task ProbeCiStatusAsync_MalformedTotalCount_ReturnsMalformedError(string totalCountJson)
    {
        var body = $"{{\"total_count\":{totalCountJson},\"check_runs\":[{{\"name\":\"build\",\"status\":\"completed\",\"conclusion\":\"success\"}}]}}";
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(body));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("malformed", result.ErrorDetail);
        Assert.Empty(result.CheckRuns);
    }

    /// <summary>
    /// Complement of the malformed-total_count theory: a valid count on the same body shape
    /// classifies normally, so the rejections above are about the value and not the payload.
    /// </summary>
    [Fact]
    public async Task ProbeCiStatusAsync_ValidTotalCount_ClassifiesNormally()
    {
        var body = "{\"total_count\":1,\"check_runs\":[{\"name\":\"build\",\"status\":\"completed\",\"conclusion\":\"success\"}]}";
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(body));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Succeeded, result.Status);
        Assert.Equal("build", Assert.Single(result.CheckRuns).Name);
    }

    // ── Regression: dedup keys are repository-aware ────────────────────────

    /// <summary>
    /// Same goal AND same SHA in two different repositories must both be monitored: the
    /// in-flight key includes the repository. The rendezvous forces both requests to be in
    /// flight simultaneously, so dropping <c>Repo</c> from the key would make the second call
    /// skip, leave the barrier unmet, and fail here rather than passing silently.
    /// </summary>
    [Fact]
    public async Task MonitorGoalAsync_SameShaDifferentRepos_BothMonitoredConcurrently()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(
            CreateRepo("repo-a", "https://github.com/org/repo-a"),
            CreateRepo("repo-b", "https://github.com/org/repo-b"));

        var inFlight = 0;
        var bothInFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHttpMessageHandler(async _ =>
        {
            if (Interlocked.Increment(ref inFlight) == 2)
                bothInFlight.TrySetResult(true);

            // Neither request completes until BOTH arrived. If the in-flight key ignored the
            // repository, the second call would be skipped and this barrier never met.
            await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, config: config, eventBus: eventBus, clock: clock);

        using var cts = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            // Identical SHA for both repositories — only the repository dimension separates them.
            monitor = service.MonitorGoalAsync("goal-1", "same-sha,same-sha", ["repo-a", "repo-b"], cts.Token);
            await monitor.WaitAsync(ControlledTimeProvider.HangGuard, TestContext.Current.CancellationToken);

            Assert.True(bothInFlight.Task.IsCompletedSuccessfully,
                "Both repositories must be monitored for the same SHA; the in-flight key is not repository-aware.");
            Assert.Equal(2, handler.Requests.Count);
            // Event dedup must also be repository-aware: one CiSucceeded per repository.
            Assert.Equal(2, eventBus.Published.Count);
            Assert.All(eventBus.Published, e => Assert.Equal(EventType.CiSucceeded, e.Type));
            Assert.Equal(["repo-a", "repo-b"], eventBus.Published.Select(e => e.Repository).Order().ToList());
        }
        finally
        {
            await CleanupMonitorAsync(monitor, cts, () => bothInFlight.TrySetResult(true));
        }

        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// Failure flavour of the repository-dimension test: the same failing SHA in two
    /// repositories must publish a <c>CiFailed</c> for each, and create issues attributed to
    /// each repository. Dropping <c>Repo</c> from the published-event key would emit only one.
    /// </summary>
    [Fact]
    public async Task MonitorGoalAsync_SameShaDifferentRepos_PublishesCiFailedPerRepository()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var config = CreateConfig(
            CreateRepo("repo-a", "https://github.com/org/repo-a"),
            CreateRepo("repo-b", "https://github.com/org/repo-b"));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "Build failed", null))));
        var service = CreateService(handler, config: config, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorGoalAsync("goal-1", "same-sha,same-sha", ["repo-a", "repo-b"], TestContext.Current.CancellationToken);

        Assert.Equal(2, eventBus.Published.Count);
        Assert.All(eventBus.Published, e => Assert.Equal(EventType.CiFailed, e.Type));
        Assert.Equal(["repo-a", "repo-b"], eventBus.Published.Select(e => e.Repository).Order().ToList());
        Assert.Equal(2, issueStore.Issues.Count);
        Assert.Equal(
            ["repo-a", "repo-b"],
            issueStore.Issues.Values.Select(i => i.RepositoryNames.Single()).Order().ToList());
    }

    /// <summary>
    /// The complement that pins the key down: the SAME goal, SHA and repository must publish
    /// only once. With the test above (different repositories → two events) this proves the key
    /// distinguishes exactly on the repository dimension and not on nothing at all.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_SameGoalShaAndRepoTwice_PublishesCiSucceededOnce()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // Both calls really ran (the second was not skipped by the in-flight guard, which is
        // released before it returns) — the single event comes from the published-event dedup.
        Assert.Equal(2, handler.Requests.Count);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    /// <summary>
    /// Startup-scan flavour: one goal whose SAME SHA landed in two repositories must be probed
    /// and published per repository, so a repository-blind dedup key cannot pass.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_SameShaDifferentRepos_PublishesPerRepository()
    {
        var eventBus = new RecordingEventBus();
        var config = CreateConfig(
            CreateRepo("repo-a", "https://github.com/org/repo-a"),
            CreateRepo("repo-b", "https://github.com/org/repo-b"));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var goal = CompletedGoal("goal-1", "same-sha,same-sha", repositoryNames: ["repo-a", "repo-b"]);
        var service = CreateService(handler, config: config, eventBus: eventBus, goalStore: StoreWith(goal));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        var urls = handler.Captured.Select(c => c.Url).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Contains(urls, u => u.Contains("/repos/org/repo-a/commits/same-sha/"));
        Assert.Contains(urls, u => u.Contains("/repos/org/repo-b/commits/same-sha/"));
        Assert.Equal(2, eventBus.Published.Count);
        Assert.Equal(["repo-a", "repo-b"], eventBus.Published.Select(e => e.Repository).Order().ToList());
    }

    /// <summary>
    /// Startup-scan failure flavour: the same failing SHA in two repositories must not have one
    /// repository's issue creation suppressed by the other's in-flight key.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_SameShaDifferentReposFailed_CreatesIssuesPerRepository()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var config = CreateConfig(
            CreateRepo("repo-a", "https://github.com/org/repo-a"),
            CreateRepo("repo-b", "https://github.com/org/repo-b"));
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "Build failed", null))));
        var goal = CompletedGoal("goal-1", "same-sha,same-sha", repositoryNames: ["repo-a", "repo-b"]);
        var service = CreateService(
            handler, config: config, issueStore: issueStore, eventBus: eventBus, goalStore: StoreWith(goal));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, eventBus.Published.Count);
        Assert.All(eventBus.Published, e => Assert.Equal(EventType.CiFailed, e.Type));
        Assert.Equal(2, issueStore.Issues.Count);
        Assert.Equal(
            ["repo-a", "repo-b"],
            issueStore.Issues.Values.Select(i => i.RepositoryNames.Single()).Order().ToList());
    }

    // ── ParseCheckRun: DetailsUrl and RunId capture ───────────────────────

    /// <summary>
    /// Parses a single check-run JSON element via the internal probe path and returns the
    /// resulting <see cref="CheckRunData"/>. The check_runs array carries exactly one run.
    /// </summary>
    private static async Task<CheckRunData> ParseSingleRunAsync(string checkRunJson)
    {
        var body = RawCheckRunsJson(1, $"[{checkRunJson}]");
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(body));
        var result = await ProbeAsync(handler);
        return Assert.Single(result.CheckRuns);
    }

    [Fact]
    public async Task ParseCheckRun_DetailsUrlPresent_CapturesDetailsUrl()
    {
        var run = await ParseSingleRunAsync("""
            {
              "name": "build",
              "status": "completed",
              "conclusion": "failure",
              "details_url": "https://github.com/org/repo/actions/runs/123456789"
            }
            """);

        Assert.Equal("https://github.com/org/repo/actions/runs/123456789", run.DetailsUrl);
        Assert.Equal("123456789", run.RunId);
    }

    [Fact]
    public async Task ParseCheckRun_DetailsUrlAbsent_DetailsUrlIsNull()
    {
        var run = await ParseSingleRunAsync("""
            {
              "name": "build",
              "status": "completed",
              "conclusion": "failure"
            }
            """);

        Assert.Null(run.DetailsUrl);
    }

    [Fact]
    public async Task ParseCheckRun_DetailsUrlWithActionsRuns_ParsesRunId()
    {
        var run = await ParseSingleRunAsync("""
            {
              "name": "build",
              "status": "completed",
              "conclusion": "failure",
              "details_url": "https://github.com/org/repo/actions/runs/9876543210/attempts/1"
            }
            """);

        Assert.Equal("9876543210", run.RunId);
        Assert.Equal("https://github.com/org/repo/actions/runs/9876543210/attempts/1", run.DetailsUrl);
    }

    [Fact]
    public async Task ParseCheckRun_DetailsUrlPresentButNoRunPath_RetainsDetailsUrlRunIdNull()
    {
        var url = "https://example.com/some-other-page";
        var run = await ParseSingleRunAsync($$"""
            {
              "name": "build",
              "status": "completed",
              "conclusion": "failure",
              "details_url": "{{url}}"
            }
            """);

        Assert.Equal(url, run.DetailsUrl);
        Assert.Null(run.RunId);
    }

    [Fact]
    public async Task ParseCheckRun_NoDetailsUrl_RunIdIsNull()
    {
        var run = await ParseSingleRunAsync("""
            {
              "name": "build",
              "status": "completed",
              "conclusion": "success"
            }
            """);

        Assert.Null(run.RunId);
    }

    // ── SanitizeLogContent ────────────────────────────────────────────────

    [Theory]
    [InlineData("ghp_")]
    [InlineData("gho_")]
    [InlineData("ghs_")]
    public void SanitizeLogContent_GitHubTokenPrefix_RedactsFull36CharSuffix(string prefix)
    {
        // GitHub token secrets are a 4-char prefix + 36-char suffix (e.g. ghp_xxxx...).
        var suffix = new string('a', 36);
        var token = prefix + suffix;
        var input = $"Authorization: {token} and done";

        var sanitized = CiMonitorService.SanitizeLogContent(input);

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("***REDACTED***", sanitized);
    }

    [Fact]
    public void SanitizeLogContent_BearerToken_RedactsValue()
    {
        var input = "header: Bearer abc.def-ghi_xyz123";

        var sanitized = CiMonitorService.SanitizeLogContent(input);

        Assert.Equal("header: Bearer ***REDACTED***", sanitized);
        Assert.DoesNotContain("abc.def-ghi_xyz123", sanitized);
    }

    [Fact]
    public void SanitizeLogContent_SecretEnvVar_PreservesKeyNameRedactsValue()
    {
        var input = "MY_API_TOKEN=abc123";

        var sanitized = CiMonitorService.SanitizeLogContent(input);

        Assert.Equal("MY_API_TOKEN=***REDACTED***", sanitized);
    }

    [Fact]
    public void SanitizeLogContent_ConnectionStringPassword_RedactsPassword()
    {
        var input = "Server=.;Database=db;User Id=sa;Password=s3cr3t;Trusted_Connection=False;";

        var sanitized = CiMonitorService.SanitizeLogContent(input);

        Assert.DoesNotContain("s3cr3t", sanitized);
        Assert.Contains("Password=***REDACTED***", sanitized);
        // The surrounding connection-string fragments survive untouched.
        Assert.Contains("Server=.", sanitized);
        Assert.Contains("Database=db", sanitized);
    }

    [Fact]
    public void SanitizeLogContent_LongInput_DoesNotTruncate()
    {
        // Only matched secrets are redacted; the full length of the input is preserved.
        var padding = new string('x', 10_000);
        var input = $"start {padding} ghp_{new string('b', 36)} end {padding}";

        var sanitized = CiMonitorService.SanitizeLogContent(input);

        Assert.DoesNotContain("ghp_", sanitized);
        Assert.Contains("***REDACTED***", sanitized);
        // The non-secret padding (before and after the token) survives at full length.
        var expectedLength = input.Length - ("ghp_" + new string('b', 36)).Length + "***REDACTED***".Length;
        Assert.Equal(expectedLength, sanitized.Length);
    }

    // ── ParseTestFailuresFromLogs ──────────────────────────────────────────

    [Fact]
    public void ParseTestFailuresFromLogs_SingleFailure_ReturnsOneEntryWithCorrectFields()
    {
        // Normalize line endings: on Windows the raw string literal contains \r\n, but the
        // parser splits/joins on \n, so \r would leak into interior lines of Error/StackTrace.
        var log = """
            Starting test execution...

            Failed CopilotHive.Tests.MyTests.TestOne [1 ms]
              Error Message:
                Assert.Equal() Failure
                Expected: 1
                Actual:   2
              Stack Trace:
                at MyTests.TestOne() in /src/MyTests.cs:line 10
                at Xunit.TestRunner.Run()
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("CopilotHive.Tests.MyTests.TestOne", failure.TestName);
        // The error-message and stack-trace section contents are joined and then Trim()'d, which
        // strips leading whitespace from the first line only; interior lines keep their original
        // xUnit indentation.
        Assert.Equal("Assert.Equal() Failure\n    Expected: 1\n    Actual:   2", failure.Error);
        Assert.Equal("at MyTests.TestOne() in /src/MyTests.cs:line 10\n    at Xunit.TestRunner.Run()", failure.StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_MultipleFailures_ReturnsEntriesInOrder()
    {
        var log = """
            Failed Tests.Alpha.First [2 ms]
              Error Message:
                alpha failure
              Stack Trace:
                at Alpha.First()

            Failed Tests.Beta.Second [3 ms]
              Error Message:
                beta failure
              Stack Trace:
                at Beta.Second()
            """;

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Equal(2, failures.Count);
        Assert.Equal("Tests.Alpha.First", failures[0].TestName);
        Assert.Equal("alpha failure", failures[0].Error);
        Assert.Equal("at Alpha.First()", failures[0].StackTrace);
        Assert.Equal("Tests.Beta.Second", failures[1].TestName);
        Assert.Equal("beta failure", failures[1].Error);
        Assert.Equal("at Beta.Second()", failures[1].StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_NoFailures_ReturnsEmptyList()
    {
        var log = """
            Passed!  - Failed:     0, Passed:   10, Skipped:     0, Total:   10
            Total tests: 10
            """;

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Empty(failures);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_CountOnlyOutput_ReturnsEmptyList()
    {
        // A bare count-only summary (no per-test "Failed {name} [duration]" blocks) must not be
        // mistaken for a parseable failure — xUnit's failure blocks are the only signal.
        var log = "Failed: 3\nPassed: 10\nTotal: 13";

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Empty(failures);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_TimestampPrefixedFailure_ParsesAndStripsTimestamps()
    {
        // Raw GitHub Actions job logs prefix every line with an ISO-8601 timestamp. Without
        // stripping them the "Failed {name} [duration]" header never matches and the caller
        // degrades to the 500-char FallbackSnippet.
        var log = string.Join("\n",
            "2026-08-22T09:47:38.1197374Z Failed CopilotHive.Tests.MyTests.TestOne [582 ms]",
            "2026-08-22T09:47:38.1198000Z   Error Message:",
            "2026-08-22T09:47:38.1199000Z    Assert.Equal() Failure",
            "2026-08-22T09:47:38.1200000Z   Stack Trace:",
            "2026-08-22T09:47:38.1201000Z    at MyTests.TestOne() in /src/MyTests.cs:line 10");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("CopilotHive.Tests.MyTests.TestOne", failure.TestName);
        Assert.Equal("Assert.Equal() Failure", failure.Error);
        Assert.Equal("at MyTests.TestOne() in /src/MyTests.cs:line 10", failure.StackTrace);
        Assert.DoesNotContain("2026-08-22T", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-22T", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("Z ", failure.TestName, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_TimestampWithoutFraction_ParsesFailure()
    {
        var log = string.Join("\n",
            "2026-08-22T09:47:38Z Failed Tests.Alpha.First [1 ms]",
            "2026-08-22T09:47:38Z   Error Message:",
            "2026-08-22T09:47:38Z    boom");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("Tests.Alpha.First", failure.TestName);
        Assert.Equal("boom", failure.Error);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_MultipleTimestampedFailures_FindsAll()
    {
        var log = string.Join("\n",
            "2026-08-22T09:47:38.1197374Z Failed Tests.Alpha.First [2 ms]",
            "2026-08-22T09:47:38.1197375Z   Error Message:",
            "2026-08-22T09:47:38.1197376Z    alpha failure",
            "2026-08-22T09:47:38.1197377Z   Stack Trace:",
            "2026-08-22T09:47:38.1197378Z    at Alpha.First()",
            "2026-08-22T09:47:38.1197379Z ",
            "2026-08-22T09:47:38.1197380Z Failed Tests.Beta.Second [3 ms]",
            "2026-08-22T09:47:38.1197381Z   Error Message:",
            "2026-08-22T09:47:38.1197382Z    beta failure",
            "2026-08-22T09:47:38.1197383Z   Stack Trace:",
            "2026-08-22T09:47:38.1197384Z    at Beta.Second()");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Equal(2, failures.Count);
        Assert.Equal("Tests.Alpha.First", failures[0].TestName);
        Assert.Equal("alpha failure", failures[0].Error);
        Assert.Equal("at Alpha.First()", failures[0].StackTrace);
        Assert.Equal("Tests.Beta.Second", failures[1].TestName);
        Assert.Equal("beta failure", failures[1].Error);
        Assert.Equal("at Beta.Second()", failures[1].StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_MtpTimestampedFailures_ParsesNamesErrorsAndStackTraces()
    {
        // Real Microsoft.Testing.Platform stdout captured from a deliberately failing run of this
        // repository's suite (two theory cases whose identities contain spaces, parentheses and
        // quotes, plus one plain assertion failure), with the GitHub Actions timestamp prefixes
        // raw job logs carry. MTP uses a distinct shape from VSTest: a lower-case
        // "failed {identity} ({duration})" header, an indented "from {dll} ({tfm}|{arch})" line, a
        // bare error line (no "Error Message:" label) and bare "at …" frames (no "Stack Trace:"
        // label). Stripping the timestamp prefix also consumes the indentation that followed it,
        // so the captured error and stack text is unindented.
        var log = string.Join("\n",
            "2026-10-04T19:04:12.1000000Z Running tests from /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64)",
            "2026-10-04T19:04:12.2000000Z failed CopilotHive.Tests.MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(label: \"another case with spaces\") (0ms)",
            "2026-10-04T19:04:12.2000100Z   from /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64)",
            "2026-10-04T19:04:12.2000200Z   scratch theory failure for 'another case with spaces'",
            "2026-10-04T19:04:12.2000300Z     at CopilotHive.Tests.MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(String label) in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpScratchFailureTests.cs:23",
            "2026-10-04T19:04:12.2000400Z     at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
            "2026-10-04T19:04:12.2000500Z     at System.Reflection.MethodBaseInvoker.InvokeDirectByRefWithFewArgs(Object obj, Span`1 copyOfArgs, BindingFlags invokeAttr)",
            "2026-10-04T19:04:12.3000000Z failed CopilotHive.Tests.MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(label: \"case with spaces\") (0ms)",
            "2026-10-04T19:04:12.3000100Z   from /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64)",
            "2026-10-04T19:04:12.3000200Z   scratch theory failure for 'case with spaces'",
            "2026-10-04T19:04:12.3000300Z     at CopilotHive.Tests.MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(String label) in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpScratchFailureTests.cs:23",
            "2026-10-04T19:04:12.3000400Z     at InvokeStub_MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(Object, Span`1)",
            "2026-10-04T19:04:12.3000500Z     at System.Reflection.MethodBaseInvoker.InvokeWithOneArg(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters, CultureInfo culture)",
            "2026-10-04T19:04:12.4000000Z failed CopilotHive.Tests.MtpScratchFailureTests.ScratchFailure_AssertionProducesStackTrace (0ms)",
            "2026-10-04T19:04:12.4000100Z   from /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64)",
            "2026-10-04T19:04:12.4000200Z   Assert.Equal() Failure: Values differ",
            "2026-10-04T19:04:12.4000300Z   Expected: 42",
            "2026-10-04T19:04:12.4000400Z   Actual:   40",
            "2026-10-04T19:04:12.4000500Z     at CopilotHive.Tests.MtpScratchFailureTests.ScratchFailure_AssertionProducesStackTrace() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpScratchFailureTests.cs:15",
            "2026-10-04T19:04:12.4000600Z     at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
            "2026-10-04T19:04:12.4000700Z     at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
            "2026-10-04T19:04:12.5000000Z skipped CopilotHive.Tests.Knowledge.KnowledgeGraphLockingAndBatchDeleteTests.CommitToConfigRepoAsync_CaseInsensitiveFileSystem_DeleteThenWriteKeepsNewContent",
            "2026-10-04T19:04:12.5000100Z   Filesystem is case-sensitive — the delete/write collision cannot occur.",
            "2026-10-04T19:04:12.5000200Z   from /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64)",
            "2026-10-04T19:04:12.6000000Z /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64) failed with 3 error(s) (4m 54s 690ms)",
            "2026-10-04T19:04:12.7000000Z Test run summary: Failed!",
            "2026-10-04T19:04:12.7000100Z   total: 11130",
            "2026-10-04T19:04:12.7000200Z   failed: 3");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Equal(3, failures.Count);

        // Theory identity keeps the spaces, parentheses, and quotes of the label suffix.
        Assert.Equal(
            "CopilotHive.Tests.MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(label: \"another case with spaces\")",
            failures[0].TestName);
        Assert.Equal("scratch theory failure for 'another case with spaces'", failures[0].Error);
        Assert.Equal(
            "at CopilotHive.Tests.MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(String label) in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpScratchFailureTests.cs:23\n" +
            "at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)\n" +
            "at System.Reflection.MethodBaseInvoker.InvokeDirectByRefWithFewArgs(Object obj, Span`1 copyOfArgs, BindingFlags invokeAttr)",
            failures[0].StackTrace);

        Assert.Equal(
            "CopilotHive.Tests.MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(label: \"case with spaces\")",
            failures[1].TestName);
        Assert.Equal("scratch theory failure for 'case with spaces'", failures[1].Error);
        Assert.Equal(
            "at CopilotHive.Tests.MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(String label) in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpScratchFailureTests.cs:23\n" +
            "at InvokeStub_MtpScratchFailureTests.ScratchTheory_FailsForCaseWithSpaces(Object, Span`1)\n" +
            "at System.Reflection.MethodBaseInvoker.InvokeWithOneArg(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters, CultureInfo culture)",
            failures[1].StackTrace);

        Assert.Equal(
            "CopilotHive.Tests.MtpScratchFailureTests.ScratchFailure_AssertionProducesStackTrace",
            failures[2].TestName);
        Assert.Equal("Assert.Equal() Failure: Values differ\nExpected: 42\nActual:   40", failures[2].Error);
        Assert.Equal(
            "at CopilotHive.Tests.MtpScratchFailureTests.ScratchFailure_AssertionProducesStackTrace() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpScratchFailureTests.cs:15\n" +
            "at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)\n" +
            "at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
            failures[2].StackTrace);

        // The skipped test and the run footer must not leak into the last failure's fields.
        Assert.DoesNotContain("skipped", failures[2].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Test run summary", failures[2].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("failed with 3 error(s)", failures[2].StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("from /copilot-home", failures[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_MtpFailureAtEndOfLogWithoutFooter_ParsesBlock()
    {
        // No following result line or footer: the block runs to the end of the log.
        var log = string.Join("\n",
            "failed Tests.Alpha.First (12ms)",
            "  from /repo/bin/Release/net10.0/Tests.dll (net10.0|x64)",
            "  boom",
            "    at Alpha.First() in /src/Alpha.cs:line 7");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("Tests.Alpha.First", failure.TestName);
        Assert.Equal("boom", failure.Error);
        Assert.Equal("at Alpha.First() in /src/Alpha.cs:line 7", failure.StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_MtpProseLine_NotMatchedAsFailure()
    {
        // Same guard as the VSTest header: prose in the "failed … (duration)" shape has no dotted
        // Class.Method identity, so it must not be reported as a test failure.
        var log = string.Join("\n",
            "failed to restore packages (3s)",
            "  from /repo/bin/Release/net10.0/Tests.dll (net10.0|x64)",
            "  NuGet feed unreachable");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Empty(failures);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_VstestAndMtpBlocksInOneLog_ParsesBoth()
    {
        // Transition period: other monitored repositories still emit VSTest output while this one
        // emits MTP. Both shapes must parse from a single log, in order, without either hiding the
        // other.
        var log = string.Join("\n",
            "2026-08-22T09:47:38.1197374Z Failed Tests.Alpha.First [2 ms]",
            "2026-08-22T09:47:38.1197375Z   Error Message:",
            "2026-08-22T09:47:38.1197376Z    vstest failure",
            "2026-08-22T09:47:38.1197377Z   Stack Trace:",
            "2026-08-22T09:47:38.1197378Z    at Alpha.First()",
            "2026-10-04T19:04:12.2000000Z failed Tests.Beta.Second (0ms)",
            "2026-10-04T19:04:12.2000100Z   from /repo/bin/Release/net10.0/Tests.dll (net10.0|x64)",
            "2026-10-04T19:04:12.2000200Z   mtp failure",
            "2026-10-04T19:04:12.2000300Z     at Beta.Second()");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Equal(2, failures.Count);
        Assert.Equal("Tests.Alpha.First", failures[0].TestName);
        Assert.Equal("vstest failure", failures[0].Error);
        Assert.Equal("at Alpha.First()", failures[0].StackTrace);
        Assert.Equal("Tests.Beta.Second", failures[1].TestName);
        Assert.Equal("mtp failure", failures[1].Error);
        Assert.Equal("at Beta.Second()", failures[1].StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_MtpSecondScaleDuration_ParsesBlock()
    {
        // MTP's HumanReadableDurationFormatter emits space-separated multi-component durations.
        // This header is verbatim from a captured run (a failing scratch test that slept 1.5s);
        // the duration's embedded space must not defeat the header match or the identity capture.
        // The log is plain (untimestamped) console output, so only the first line of each captured
        // field loses its indentation to the final Trim(); the interior lines keep it.
        var log = string.Join("\n",
            "failed CopilotHive.Tests.MtpDurationScratchTests.ScratchSecondScaleFailure_ProducesSecondMillisecondDuration (1s 500ms)",
            "  from /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64)",
            "  Assert.Equal() Failure: Values differ",
            "  Expected: 42",
            "  Actual:   40",
            "    at CopilotHive.Tests.MtpDurationScratchTests.ScratchSecondScaleFailure_ProducesSecondMillisecondDuration() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpDurationScratchTests.cs:22",
            "    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
            "    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal(
            "CopilotHive.Tests.MtpDurationScratchTests.ScratchSecondScaleFailure_ProducesSecondMillisecondDuration",
            failure.TestName);
        Assert.Equal("Assert.Equal() Failure: Values differ\n  Expected: 42\n  Actual:   40", failure.Error);
        Assert.Equal(
            "at CopilotHive.Tests.MtpDurationScratchTests.ScratchSecondScaleFailure_ProducesSecondMillisecondDuration() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpDurationScratchTests.cs:22\n" +
            "    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)\n" +
            "    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
            failure.StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_MtpMinuteScaleDuration_ParsesBlock()
    {
        // Three-component duration, verbatim from a captured run (a failing scratch test that
        // slept 63s). Two interior spaces must still leave the identity intact.
        var log = string.Join("\n",
            "2026-10-04T22:32:41.1000000Z failed CopilotHive.Tests.MtpDurationScratchTests.ScratchMinuteScaleFailure_ProducesMinuteSecondMillisecondDuration (1m 03s 000ms)",
            "2026-10-04T22:32:41.1000100Z   from /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64)",
            "2026-10-04T22:32:41.1000200Z   minute-scale scratch failure",
            "2026-10-04T22:32:41.1000300Z     at CopilotHive.Tests.MtpDurationScratchTests.ScratchMinuteScaleFailure_ProducesMinuteSecondMillisecondDuration() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpDurationScratchTests.cs:30",
            "2026-10-04T22:32:41.1000400Z     at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
            "2026-10-04T22:32:41.1000500Z     at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal(
            "CopilotHive.Tests.MtpDurationScratchTests.ScratchMinuteScaleFailure_ProducesMinuteSecondMillisecondDuration",
            failure.TestName);
        Assert.Equal("minute-scale scratch failure", failure.Error);
        Assert.Equal(
            "at CopilotHive.Tests.MtpDurationScratchTests.ScratchMinuteScaleFailure_ProducesMinuteSecondMillisecondDuration() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpDurationScratchTests.cs:30\n" +
            "at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)\n" +
            "at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
            failure.StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_MtpFinalFailureBeforeAssemblyFooter_StackTraceNotSwallowed()
    {
        // The final failure is followed directly by MTP's per-assembly result footer, with no
        // intervening skipped/passed result line (the captured shape when the last test in the
        // assembly fails, or when skips are reported earlier in the log). The footer — and the
        // blank line that precedes it — must bound the block: without that boundary the error
        // text swallows the frames and the footer, and StackTrace comes back empty.
        var log = string.Join("\n",
            "2026-10-04T22:32:41.2000000Z failed CopilotHive.Tests.MtpDurationScratchTests.ScratchMinuteScaleFailure_ProducesMinuteSecondMillisecondDuration (1m 03s 000ms)",
            "2026-10-04T22:32:41.2000100Z   from /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64)",
            "2026-10-04T22:32:41.2000200Z   minute-scale scratch failure",
            "2026-10-04T22:32:41.2000300Z     at CopilotHive.Tests.MtpDurationScratchTests.ScratchMinuteScaleFailure_ProducesMinuteSecondMillisecondDuration() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpDurationScratchTests.cs:30",
            "2026-10-04T22:32:41.2000400Z     at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
            "2026-10-04T22:32:41.3000000Z ",
            "2026-10-04T22:32:41.4000000Z /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64) failed with 3 error(s) (5m 19s 718ms)",
            "2026-10-04T22:32:41.5000000Z Exit code: 2");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal(
            "CopilotHive.Tests.MtpDurationScratchTests.ScratchMinuteScaleFailure_ProducesMinuteSecondMillisecondDuration",
            failure.TestName);
        Assert.Equal("minute-scale scratch failure", failure.Error);
        Assert.Equal(
            "at CopilotHive.Tests.MtpDurationScratchTests.ScratchMinuteScaleFailure_ProducesMinuteSecondMillisecondDuration() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpDurationScratchTests.cs:30\n" +
            "at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
            failure.StackTrace);

        // Footer text must appear in NO parsed field.
        Assert.DoesNotContain("failed with 3 error(s)", failure.TestName, StringComparison.Ordinal);
        Assert.DoesNotContain("failed with 3 error(s)", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("failed with 3 error(s)", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("net10.0|x64", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("net10.0|x64", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit code", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit code", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("InterpretedInvoke_Method", failure.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_MtpNewlineTerminatedLog_ParsesFullStack()
    {
        // The log ends with a final newline, so the last line is an empty one. That trailing
        // blank must not terminate the backwards stack-frame scan and strip the trace. Untimestamped
        // output keeps the interior frames' indentation (only the first line loses it to Trim()).
        var log = string.Join("\n",
                      "failed CopilotHive.Tests.MtpDurationScratchTests.ScratchQuickFailure_ProducesMillisecondDuration (0ms)",
                      "  from /copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64)",
                      "  quick scratch failure",
                      "    at CopilotHive.Tests.MtpDurationScratchTests.ScratchQuickFailure_ProducesMillisecondDuration() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpDurationScratchTests.cs:14",
                      "    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
                      "    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)")
                  + "\n";

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal(
            "CopilotHive.Tests.MtpDurationScratchTests.ScratchQuickFailure_ProducesMillisecondDuration",
            failure.TestName);
        Assert.Equal("quick scratch failure", failure.Error);
        Assert.Equal(
            "at CopilotHive.Tests.MtpDurationScratchTests.ScratchQuickFailure_ProducesMillisecondDuration() in /copilot-home/CopilotHive/tests/CopilotHive.Tests/MtpDurationScratchTests.cs:14\n" +
            "    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)\n" +
            "    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
            failure.StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_FailedToProse_NotMatchedAsFailure()
    {
        // "Failed to ..." prose can satisfy the header shape when it ends in a digit-leading
        // bracket, but it has no dotted Class.Method name, so it must not be reported.
        var log = string.Join("\n",
            "2026-08-22T09:47:38.1197374Z Failed to clone repository [3 attempts]",
            "2026-08-22T09:47:38.1197375Z   Error Message:",
            "2026-08-22T09:47:38.1197376Z    network unreachable");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Empty(failures);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_FailedColonProse_NotMatchedAsFailure()
    {
        var log = string.Join("\n",
            "2026-08-22T09:47:38.1197374Z Failed: the build step exited [1 time]",
            "2026-08-22T09:47:38.1197375Z   Error Message:",
            "2026-08-22T09:47:38.1197376Z    exit code 1");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Empty(failures);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_BlankLineBetweenErrorAndStackTrace_BlockContinues()
    {
        var log = string.Join("\n",
            "Failed Tests.Alpha.First [5 ms]",
            "  Error Message:",
            "   expected 1 actual 2",
            "",
            "  Stack Trace:",
            "   at Alpha.First()");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("expected 1 actual 2", failure.Error);
        Assert.Equal("at Alpha.First()", failure.StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_EndOfStackTraceMarker_EndsStackTraceSection()
    {
        var log = string.Join("\n",
            "Failed Tests.Alpha.First [5 ms]",
            "  Error Message:",
            "   boom",
            "  Stack Trace:",
            "   at Alpha.First()",
            "   --- End of stack trace from previous location ---",
            "   at Xunit.Sdk.TestInvoker.Invoke()");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("at Alpha.First()", failure.StackTrace);
        Assert.DoesNotContain("End of stack trace", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("TestInvoker", failure.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_BlankLineAfterStackTrace_EndsBlock()
    {
        var log = string.Join("\n",
            "Failed Tests.Alpha.First [5 ms]",
            "  Error Message:",
            "   boom",
            "  Stack Trace:",
            "   at Alpha.First()",
            "",
            "Cleaning up orphaned containers...",
            "Removed 3 containers.");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("at Alpha.First()", failure.StackTrace);
        Assert.DoesNotContain("orphaned", failure.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_NextFailedHeaderAfterStackTrace_EndsPreviousBlock()
    {
        // No blank line separating the blocks: the next "Failed Class.Method [duration]" header
        // must terminate the previous stack trace and open a new block.
        var log = string.Join("\n",
            "Failed Tests.Alpha.First [5 ms]",
            "  Error Message:",
            "   alpha boom",
            "  Stack Trace:",
            "   at Alpha.First()",
            "Failed Tests.Beta.Second [6 ms]",
            "  Error Message:",
            "   beta boom",
            "  Stack Trace:",
            "   at Beta.Second()");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Equal(2, failures.Count);
        Assert.Equal("at Alpha.First()", failures[0].StackTrace);
        Assert.Equal("alpha boom", failures[0].Error);
        Assert.Equal("Tests.Beta.Second", failures[1].TestName);
        Assert.Equal("at Beta.Second()", failures[1].StackTrace);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_TheoryNameWithSpaces_StillParses()
    {
        // The existing header regex tolerates theory display names containing spaces; the
        // additive dotted-name check must not regress that.
        var log = string.Join("\n",
            "2026-08-22T09:47:38.1197374Z Failed Tests.Alpha.Theory(value: \"a b\") [12 ms]",
            "2026-08-22T09:47:38.1197375Z   Error Message:",
            "2026-08-22T09:47:38.1197376Z    boom");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("Tests.Alpha.Theory(value: \"a b\")", failure.TestName);
    }

    [Fact]
    public void ParseTestFailuresFromLogs_TimestampedLogWithNoFailurePatterns_ReturnsEmptyList()
    {
        // When nothing parses, the caller falls back to FallbackSnippet — that path is preserved.
        var log = string.Join("\n",
            "2026-08-22T09:47:38.1197374Z Cleaning up orphaned containers...",
            "2026-08-22T09:47:38.1197375Z Removed 3 containers.",
            "2026-08-22T09:47:38.1197376Z Passed!  - Failed:     0, Passed:   10");

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Empty(failures);
    }

    // ── FetchJobLogsAsync (internal) ───────────────────────────────────────


    /// <summary>
    /// Builds a JSON body for the <c>/actions/runs/{runId}/jobs</c> endpoint. Each job has an
    /// integer <c>id</c> and a string <c>conclusion</c>; only jobs whose conclusion is
    /// <c>failure</c> are fetched.
    /// </summary>
    private static string JobsJson(int totalCount, params (long Id, string Conclusion)[] jobs)
    {
        var jobObjects = jobs.Select(j => new
        {
            id = j.Id,
            conclusion = j.Conclusion,
        }).ToArray();
        return JsonSerializer.Serialize(new { total_count = totalCount, jobs = jobObjects });
    }

    /// <summary>
    /// An xUnit failure log body containing exactly one parseable failure, used as the log
    /// payload returned by the redirect target.
    /// </summary>
    private static string SingleFailureLog() => """
        Starting test execution...

        Failed Tests.Alpha.First [2 ms]
          Error Message:
            alpha failure
          Stack Trace:
            at Alpha.First()
        """;

    /// <summary>
    /// An xUnit failure log body containing two parseable failures in order.
    /// </summary>
    private static string TwoFailureLog() => """
        Failed Tests.Alpha.First [2 ms]
          Error Message:
            alpha failure
          Stack Trace:
            at Alpha.First()

        Failed Tests.Beta.Second [3 ms]
          Error Message:
            beta failure
          Stack Trace:
            at Beta.Second()
        """;

    /// <summary>A log body with no xUnit failure blocks (a passing summary).</summary>
    private static string NoFailureLog() => """
        Passed!  - Failed:     0, Passed:   10, Skipped:     0, Total:   10
        Total tests: 10
        """;

    /// <summary>
    /// A 302 redirect response whose <c>Location</c> points at a cross-host signed URL. The
    /// test's responder serves the configured log body at that URL.
    /// </summary>
    private sealed class RedirectHandler : HttpMessageHandler
    {
        private readonly string _redirectTarget = $"https://logs.example.com/{Guid.NewGuid():N}";
        private readonly string _logBody;
        private readonly List<CapturedRequest> _captured = [];
        private readonly object _lock = new();

        public IReadOnlyList<CapturedRequest> Captured
        {
            get { lock (_lock) return _captured.ToList(); }
        }

        public RedirectHandler(string logBody) => _logBody = logBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _captured.Add(new CapturedRequest(
                    request.RequestUri!.ToString(),
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter));
            }

            // The API endpoint (api.github.com) returns 302 to the redirect target.
            if (request.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri(_redirectTarget) }
                });
            }

            // The redirect target (cross-host) returns the log body with 200.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_logBody, Encoding.UTF8, "text/plain")
            });
        }
    }

    /// <summary>
    /// A handler that returns 302 for api.github.com requests but returns a non-2xx status
    /// at the redirect target, simulating a failed log download.
    /// </summary>
    private sealed class RedirectThenFailHandler : HttpMessageHandler
    {
        private readonly string _redirectTarget = $"https://logs.example.com/{Guid.NewGuid():N}";
        private readonly HttpStatusCode _redirectStatus;
        private readonly List<CapturedRequest> _captured = [];
        private readonly object _lock = new();

        public IReadOnlyList<CapturedRequest> Captured
        {
            get { lock (_lock) return _captured.ToList(); }
        }

        public RedirectThenFailHandler(HttpStatusCode redirectStatus) => _redirectStatus = redirectStatus;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _captured.Add(new CapturedRequest(
                    request.RequestUri!.ToString(),
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter));
            }

            if (request.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri(_redirectTarget) }
                });
            }

            return Task.FromResult(new HttpResponseMessage(_redirectStatus));
        }
    }

    /// <summary>
    /// A handler that serves the jobs endpoint from api.github.com and serves a configurable
    /// per-job-log response. The responder function receives the request URL and returns the
    /// appropriate <see cref="HttpResponseMessage"/>.
    /// </summary>
    private sealed class JobsAndLogsHandler : HttpMessageHandler
    {
        private readonly string _jobsBody;
        private readonly Func<Uri, HttpResponseMessage> _logResponder;
        private readonly List<CapturedRequest> _captured = [];
        private readonly object _lock = new();

        public IReadOnlyList<CapturedRequest> Captured
        {
            get { lock (_lock) return _captured.ToList(); }
        }

        public JobsAndLogsHandler(string jobsBody, Func<Uri, HttpResponseMessage> logResponder)
        {
            _jobsBody = jobsBody;
            _logResponder = logResponder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _captured.Add(new CapturedRequest(
                    request.RequestUri!.ToString(),
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter));
            }

            var uri = request.RequestUri!;
            // The jobs endpoint contains "/actions/runs/.../jobs"
            if (uri.AbsolutePath.Contains("/actions/runs/", StringComparison.Ordinal)
                && uri.AbsolutePath.EndsWith("/jobs", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_jobsBody, Encoding.UTF8, "application/json")
                });
            }

            // The job-log endpoint contains "/actions/jobs/.../logs"
            return Task.FromResult(_logResponder(uri));
        }
    }

    /// <summary>
    /// A <see cref="JobsAndLogsHandler"/> variant that awaits an async hook BEFORE producing
    /// any response. Pairing one instance whose hook waits on a gate with another whose hook
    /// signals that gate rendezvouses two concurrent <c>FetchJobLogsAsync</c> callers: neither
    /// response can complete until both callers have already checked the cache and missed.
    /// </summary>
    private sealed class GatedJobsAndLogsHandler : HttpMessageHandler
    {
        private readonly string _jobsBody;
        private readonly Func<Uri, HttpResponseMessage> _logResponder;
        private readonly Func<Task> _beforeResponse;
        private readonly List<CapturedRequest> _captured = [];
        private readonly object _lock = new();

        public IReadOnlyList<CapturedRequest> Captured
        {
            get { lock (_lock) return _captured.ToList(); }
        }

        public GatedJobsAndLogsHandler(
            string jobsBody,
            Func<Uri, HttpResponseMessage> logResponder,
            Func<Task> beforeResponse)
        {
            _jobsBody = jobsBody;
            _logResponder = logResponder;
            _beforeResponse = beforeResponse;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _captured.Add(new CapturedRequest(
                    request.RequestUri!.ToString(),
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter));
            }

            // Runs before ANY response bytes exist, so a caller parked here has already
            // performed (and missed) its cache lookup.
            await _beforeResponse();

            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("/actions/runs/", StringComparison.Ordinal)
                && uri.AbsolutePath.EndsWith("/jobs", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_jobsBody, Encoding.UTF8, "application/json")
                };
            }

            return _logResponder(uri);
        }
    }

    private static HttpResponseMessage LogResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/plain")
    };

    private static HttpResponseMessage LogResponse(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body)
    };

    /// <summary>
    /// A read-only stream that serves a fixed body but deliberately returns IRREGULAR PARTIAL
    /// read lengths (never more than <see cref="PartialReadSize"/> bytes per call, regardless of
    /// how many were asked for). <see cref="MaxRequestedSize"/> and <see cref="TotalBytesRead"/>
    /// record exactly how far the reader reached, so a streaming reader can be shown to consume the
    /// WHOLE body while never requesting more than its documented chunk size at once.
    /// </summary>
    private sealed class PartialReadRecordingStream : Stream
    {
        /// <summary>Maximum number of bytes returned from a single read, however many were requested.</summary>
        public const int PartialReadSize = 8191;

        private readonly byte[] _body;
        private int _position;

        public PartialReadRecordingStream(byte[] body) => _body = body;

        /// <summary>Largest number of bytes any single read asked the stream for.</summary>
        public int MaxRequestedSize { get; private set; }

        /// <summary>Total bytes actually handed to the reader (i.e. consumed from the stream).</summary>
        public int TotalBytesRead => _position;

        /// <summary>Number of <c>ReadAsync</c> calls the reader issued.</summary>
        public int ReadCallCount { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            ReadCallCount++;
            MaxRequestedSize = Math.Max(MaxRequestedSize, destination.Length);

            var available = _body.Length - _position;
            var count = Math.Min(Math.Min(destination.Length, available), PartialReadSize);
            if (count <= 0)
                return ValueTask.FromResult(0);

            _body.AsSpan(_position, count).CopyTo(destination.Span);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _body.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A 200 log response whose body is served by the given stream (not a buffered byte array),
    /// so the reader's actual read geometry against the stream is observable.
    /// </summary>
    private static HttpResponseMessage LogResponse(Stream body) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(body)
    };

    /// <summary>
    /// A stream that serves its first chunk and then cancels the caller's token source and throws
    /// <see cref="OperationCanceledException"/> — a cancellation that lands in the MIDDLE of the
    /// streaming read, not before the request.
    /// </summary>
    private sealed class CancellingStream : Stream
    {
        private readonly CancellationTokenSource _cts;
        private readonly byte[] _prefix;
        private bool _served;

        public CancellingStream(CancellationTokenSource cts, byte[] prefix)
        {
            _cts = cts;
            _prefix = prefix;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (_served)
            {
                _cts.Cancel();
                throw new OperationCanceledException(_cts.Token);
            }

            _served = true;
            var count = Math.Min(_prefix.Length, destination.Length);
            _prefix.AsSpan(0, count).CopyTo(destination.Span);
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _prefix.Length;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Appends filler LINES (never shorter than two characters) until the builder's length reaches
    /// <paramref name="targetLength"/> exactly. The filler is pure ASCII, so the builder's
    /// character count is also its UTF-8 byte count — which is what makes the chunk-boundary
    /// arithmetic below exact.
    /// </summary>
    private static void AppendFillerTo(StringBuilder sb, int targetLength)
    {
        while (sb.Length < targetLength)
        {
            var room = targetLength - sb.Length;
            var lineLength = Math.Min(100, room - 1);
            sb.Append(new string('f', lineLength)).Append('\n');
        }

        Assert.Equal(targetLength, sb.Length);
    }

    /// <summary>
    /// Builds a log in which each given block starts a fixed number of bytes BEFORE a stream-chunk
    /// boundary, so the block's own header line is split across two chunk reads.
    /// </summary>
    /// <param name="blocks">
    /// The blocks, in log order, with the number of characters of the block that precede the chunk
    /// boundary.
    /// </param>
    /// <returns>
    /// The assembled log (pure ASCII, so byte offsets and character offsets coincide) and the
    /// start offset of each block within it.
    /// </returns>
    private static (string Log, int[] BlockStarts) BuildLogWithBlocksSplitAtChunkBoundaries(
        IReadOnlyList<(string Block, int SplitBytesIntoBlock)> blocks)
    {
        var sb = new StringBuilder();
        var starts = new int[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            var boundary = StreamChunkBytes * (i + 1);
            var start = boundary - blocks[i].SplitBytesIntoBlock;
            AppendFillerTo(sb, start);
            starts[i] = sb.Length;
            sb.Append(blocks[i].Block);
        }

        return (sb.ToString(), starts);
    }

    private static HttpResponseMessage JobsNotFoundResponse() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage JobLogNotFoundResponse() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage JobLog429Response() => new(HttpStatusCode.TooManyRequests);

    private static HttpResponseMessage JobLog403Response(string? rateLimitRemaining = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        if (rateLimitRemaining is not null)
            response.Headers.Add("X-RateLimit-Remaining", rateLimitRemaining);
        return response;
    }

    private static LogFetchResult? InvokeFetch(
        CiMonitorService service, string? runId, HttpClient client, CancellationToken ct = default)
    {
        return service.FetchJobLogsAsync(runId, "org", "test-repo", "test-token", client, ct)
            .GetAwaiter().GetResult();
    }

    /// <summary>Creates a minimal CiMonitorService for direct internal-method testing.</summary>
    private static CiMonitorService CreateServiceForLogFetch() =>
        new(logger: NullLogger<CiMonitorService>.Instance);

    [Fact]
    public async Task FetchJobLogsAsync_FailedJobWithLog_ReturnsParsedFailures()
    {
        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(SingleFailureLog()));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var failure = Assert.Single(result!.Failures);
        Assert.Equal("Tests.Alpha.First", failure.TestName);
        Assert.Equal("alpha failure", failure.Error);
        Assert.Equal("at Alpha.First()", failure.StackTrace);
    }

    [Fact]
    public async Task FetchJobLogsAsync_NoFailedJobs_ReturnsNull()
    {
        var jobsBody = JobsJson(2, (111, "success"), (222, "skipped"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse("should not be called"));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.Null(result);
        // No job-log requests should have been issued because there were no failed jobs.
        Assert.Single(handler.Captured); // only the jobs endpoint request
    }

    [Fact]
    public async Task FetchJobLogsAsync_404OnAllJobLogs_ReturnsNull()
    {
        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => JobLogNotFoundResponse());
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchJobLogsAsync_PartialSuccess_CombinesFailuresFromSuccessfulJobs()
    {
        // Two failed jobs: the first log fetch 404s, the second succeeds with two failures.
        var jobsBody = JobsJson(2, (111, "failure"), (222, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, uri =>
        {
            if (uri.AbsolutePath.Contains("/111/", StringComparison.Ordinal))
                return JobLogNotFoundResponse();
            return LogResponse(TwoFailureLog());
        });
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Failures.Count);
        Assert.Equal("Tests.Alpha.First", result.Failures[0].TestName);
        Assert.Equal("Tests.Beta.Second", result.Failures[1].TestName);
    }

    [Fact]
    public async Task FetchJobLogsAsync_429OnJob_SkipsJobNoRetry()
    {
        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => JobLog429Response());
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        // The single job was skipped (429) so no log was fetched → null.
        Assert.Null(result);
        // Only two requests: jobs endpoint + one job-log attempt (no retry).
        Assert.Equal(2, handler.Captured.Count);
    }

    [Fact]
    public async Task FetchJobLogsAsync_403RateLimitedOnJob_SkipsJob()
    {
        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => JobLog403Response(rateLimitRemaining: "0"));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(2, handler.Captured.Count);
    }

    [Fact]
    public async Task FetchJobLogsAsync_Other403OnJob_SkipsJob()
    {
        var jobsBody = JobsJson(1, (111, "failure"));
        // A 403 WITHOUT X-RateLimit-Remaining: 0 is the "other 403" path.
        var handler = new JobsAndLogsHandler(jobsBody, _ => JobLog403Response());
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(2, handler.Captured.Count);
    }

    [Fact]
    public async Task FetchJobLogsAsync_NullRunId_ReturnsNull()
    {
        var handler = new JobsAndLogsHandler("{}", _ => LogResponse("unused"));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync(null, "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(handler.Captured); // no HTTP requests issued
    }

    [Fact]
    public async Task FetchJobLogsAsync_EmptyRunId_ReturnsNull()
    {
        var handler = new JobsAndLogsHandler("{}", _ => LogResponse("unused"));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(handler.Captured);
    }

    /// <summary>
    /// A failure block that lies PAST the old 4 MiB prefix cap is still parsed and retained.
    /// </summary>
    /// <remarks>
    /// The old implementation kept only the FIRST 4 MiB of the log body, so a block beyond that
    /// boundary was never even read and the issue carried no diagnostics at all. The streaming
    /// parser consumes the WHOLE body, so a block anywhere in it survives.
    /// <para>
    /// The control inside the test reconstructs the old prefix and proves the late block lies
    /// OUTSIDE it (and that parsing that prefix alone yields nothing), so the assertion is
    /// discriminating rather than incidental.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_LargeLog_FailureAfterOldPrefixCap_IsRetained()
    {
        const int OldPrefixCapBytes = 4 * 1024 * 1024;
        const string LateTestName = "Tests.Late.MtpFailure";

        // Filler past the old 4 MiB prefix cap, then a TIMESTAMPED MTP failure block late in the
        // stream. Every filler line stays far below the per-line cap, so only the streaming
        // geometry is under test here.
        var sb = new StringBuilder();
        while (sb.Length < OldPrefixCapBytes + 4_096)
            sb.Append(new string('z', 1_023)).Append('\n');
        sb.Append("2026-08-22T09:47:38.1197374Z failed Tests.Late.MtpFailure (12ms)\n");
        sb.Append("2026-08-22T09:47:38.1197375Z   from /repo/bin/Release/net10.0/Tests.dll (net10.0|x64)\n");
        sb.Append("2026-08-22T09:47:38.1197376Z   late mtp failure\n");
        sb.Append("2026-08-22T09:47:38.1197377Z     at Late.MtpFailure() in /src/Late.MtpFailure.cs:line 7\n");
        sb.Append("2026-08-22T09:47:38.1197378Z Test run summary: Failed!\n");
        var fullLog = sb.ToString();

        Assert.True(fullLog.Length > OldPrefixCapBytes, "The log must exceed the old 4 MiB prefix cap.");

        // CONTROL: the old reader kept only this prefix — it never carried the late block, and
        // parsing it alone yields no failure at all.
        var oldPrefix = fullLog[..OldPrefixCapBytes];
        Assert.DoesNotContain(LateTestName, oldPrefix, StringComparison.Ordinal);
        Assert.Empty(CiMonitorService.ParseTestFailuresFromLogs(oldPrefix));

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(fullLog));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);

        // The late block is retained in full: name, error, and stack trace.
        var failure = Assert.Single(result!.Failures);
        Assert.Equal(LateTestName, failure.TestName);
        Assert.Equal("late mtp failure", failure.Error);
        Assert.Equal("at Late.MtpFailure() in /src/Late.MtpFailure.cs:line 7", failure.StackTrace);
        Assert.Null(result.Overflow); // nothing was dropped
    }

    /// <summary>
    /// A failure block in the MIDDLE of a large log is retained too — the streaming parser has no
    /// special position, so a block past the old 200 KB tail ring and a block past the old 4 MiB
    /// prefix cap are both ordinary.
    /// </summary>
    /// <remarks>
    /// The block is closed by a blank line (the VSTest block terminator), so the filler that
    /// follows it can never leak into the parsed stack trace.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_LargeLog_MiddleFailure_IsRetained()
    {
        const int OldRingCapBytes = 200_000;
        const string MiddleTestName = "Tests.Mid.Failure";

        // The block sits near the middle of the log: the old 200 KB tail ring kept only the LAST
        // 200,000 bytes, so the block must be more than that far from the end to be evicted by it.
        const int fullLogTargetLength = OldRingCapBytes * 3;

        var sb = new StringBuilder();
        while (sb.Length < OldRingCapBytes + 4_096)
            sb.Append(new string('y', 199)).Append('\n');
        sb.Append("Failed Tests.Mid.Failure [1 ms]\n");
        sb.Append("  Error Message:\n");
        sb.Append("    mid failure\n");
        sb.Append("  Stack Trace:\n");
        sb.Append("    at Mid.Failure()\n");
        sb.Append("\n");
        // Trailing filler so the block lies OUTSIDE the old 200 KB tail ring window.
        while (sb.Length < fullLogTargetLength)
            sb.Append(new string('t', 199)).Append('\n');
        var fullLog = sb.ToString();

        // CONTROL: the old 200 KB tail ring would have evicted the block entirely.
        Assert.True(fullLog.Length > OldRingCapBytes, "The log must exceed the old 200 KB ring cap.");
        var oldRingWindow = fullLog[^OldRingCapBytes..];
        Assert.DoesNotContain(MiddleTestName, oldRingWindow, StringComparison.Ordinal);

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(fullLog));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var failure = Assert.Single(result!.Failures);
        Assert.Equal(MiddleTestName, failure.TestName);
        Assert.Equal("mid failure", failure.Error);
        Assert.Equal("at Mid.Failure()", failure.StackTrace);
        Assert.Null(result.Overflow);
    }

    /// <summary>
    /// A multi-byte UTF-8 sequence split across a stream-chunk boundary decodes leniently, and a
    /// genuinely truncated final sequence decodes to the replacement character instead of throwing.
    /// </summary>
    /// <remarks>
    /// The chunk boundary is now an ARBITRARY point in the stream (there is no byte cap), so the
    /// test places the three bytes of U+20AC (€) exactly across the 16 KiB chunk boundary — the
    /// lead byte is the last byte of chunk 1 and its continuations open chunk 2.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_LargeLog_Utf8SequenceSplitAtChunkBoundary_DecodesLeniently()
    {
        const int ChunkBytes = CiLogStreamParser.StreamChunkBytes;
        var euro = Encoding.UTF8.GetBytes("€");
        const int LineBytes = 200; // 199 'a' + '\n', well inside the per-line cap

        // Fill exactly ChunkBytes - 1 bytes with short lines so the euro's lead byte is the final
        // byte of the first chunk. Every line stays far below the per-line character cap, so nothing
        // but chunk-boundary decoding is under test here.
        var filler = new StringBuilder();
        var remaining = ChunkBytes - 1;
        while (remaining >= LineBytes)
        {
            filler.Append(new string('a', LineBytes - 1)).Append('\n');
            remaining -= LineBytes;
        }

        filler.Append(new string('a', remaining - 1)).Append('\n');
        Assert.Equal(ChunkBytes - 1, Encoding.UTF8.GetByteCount(filler.ToString()));

        using var body = new MemoryStream();
        var fillerBytes = Encoding.UTF8.GetBytes(filler.ToString());
        body.Write(fillerBytes);
        body.Write(euro);                             // straddles the chunk boundary
        body.Write("\n"u8);
        body.Write(Enumerable.Repeat((byte)'b', 10).ToArray());
        body.Write([0xE2]);                           // lone lead byte at the end of the log
        body.Position = 0;

        Assert.Equal(3, euro.Length);

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(body));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        // Must not throw on the chunk-split sequence or the truncated final one.
        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result!.Failures); // filler only → no parseable failures

        Assert.NotNull(result.FallbackSnippet);
        // The chunk-split sequence decoded to the real character...
        Assert.Contains("€", result.FallbackSnippet, StringComparison.Ordinal);
        // ...and the truncated final sequence decoded to the replacement character, not a throw.
        Assert.EndsWith("\uFFFD", result.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", result.FallbackSnippet[..^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The streaming read consumes the WHOLE body while every individual read REQUEST stays inside
    /// the documented chunk size, so the parser's working memory stays bounded no matter how large
    /// the log is.
    /// </summary>
    /// <remarks>
    /// The stream serves irregular partial reads (never more than 8,191 bytes per call), so the
    /// reader's running total lands on unaligned offsets — the geometry the old clamped-prefix
    /// reader was built around. The marker text sits at the END of the log: it reaches the
    /// fallback excerpt only if the whole body was streamed.
    /// <para>
    /// The oversized single line in the body exercises the per-line cap at the same time: it is
    /// retained only up to <see cref="MaxLogLineChars"/> with an explicit truncation marker
    /// reporting the DECODED characters dropped.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_PartialReads_ConsumesWholeStreamWithBoundedRequests()
    {
        const int BodyBytes = 4 * 1024 * 1024;
        const string EndMarker = "END_OF_LOG_MARKER_AFTER_OLD_CAP";

        // One 4 MiB line (no newline) exercises the per-line cap too: it is retained only up to
        // MaxLogLineChars with an explicit truncation marker reporting the DECODED characters dropped.
        var body = Encoding.UTF8.GetBytes(new string('y', BodyBytes) + "\n" + EndMarker + "\n");

        var stream = new PartialReadRecordingStream(body);
        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(stream));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);

        // Every byte of the stream was consumed — nothing was cut off at a cap...
        Assert.Equal(body.Length, stream.TotalBytesRead);
        // ...and no single read requested more than the documented chunk size.
        Assert.True(stream.MaxRequestedSize <= StreamChunkBytes,
            $"A read requested {stream.MaxRequestedSize} bytes, past the {StreamChunkBytes}-byte chunk size.");

        Assert.Empty(result!.Failures); // filler only → no parseable failures
        Assert.NotNull(result.FallbackSnippet);

        // The end-of-log marker (far past the old cap) reaches the tail of the excerpt...
        Assert.Contains(EndMarker, result.FallbackSnippet!, StringComparison.Ordinal);
        // ...and the 4 MiB middle of the oversized line is reported by the head/tail gap marker, whose
        // size counts DECODED characters of the whole stream: everything except the two windows.
        // The marker is exact: head window + omitted + tail window accounts for every decoded character.
        var totalChars = (long)BodyBytes + 1 + EndMarker.Length + 1;
        var snippet = result.FallbackSnippet!;
        var match = System.Text.RegularExpressions.Regex.Match(
            snippet, @"…\[truncated: ([\d,]+) characters omitted between the head and the tail of the log\]…\n");
        Assert.True(match.Success, "The gap marker is missing.");
        var gap = long.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var headLength = snippet.IndexOf("\n…[truncated", StringComparison.Ordinal);
        var tailLength = snippet.Length - match.Index - match.Length;
        Assert.Equal(totalChars, headLength + gap + tailLength);
    }

    /// <summary>
    /// The log-fetch cache preserves the overflow summary: a cached result is the SAME result, so
    /// the summary is neither lost nor recomputed on the cache hit, and no extra HTTP request is
    /// issued.
    /// </summary>
    /// <remarks>
    /// Removal-proof: if the cache stored only the retained failures (dropping the overflow field),
    /// the cached result's summary would be <c>null</c> while the first result's was not.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_CachePreservesOverflowSummary()
    {
        var failureCount = MaxRetainedFailureCount + 7;
        var failureLog = string.Concat(Enumerable.Range(0, failureCount).Select(i =>
            $"Failed Tests.Cache.Overflow{i:D4} [1 ms]\n  Error Message:\n    boom {i}\n  Stack Trace:\n    at Cache.Overflow{i:D4}()\n\n"));

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(failureLog));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var first = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.NotNull(first!.Overflow);
        var requestCountAfterFirst = handler.Captured.Count;

        var second = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        // The cache returns the same result object: same retained failures AND same overflow summary.
        Assert.Same(first, second);
        Assert.Equal(first.Overflow, second!.Overflow);
        Assert.Equal(MaxRetainedFailureCount, second.Failures.Count);
        Assert.Contains($"7 of {failureCount}", second.Overflow, StringComparison.Ordinal);
        // No new HTTP traffic: the summary came from the cache, not a re-fetch.
        Assert.Equal(requestCountAfterFirst, handler.Captured.Count);
    }

    [Fact]
    public async Task FetchJobLogsAsync_CacheHit_ReturnsCachedResultWithoutHttp()
    {
        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(SingleFailureLog()));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        // First call fetches via HTTP.
        var first = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        var firstRequestCount = handler.Captured.Count;
        Assert.True(firstRequestCount >= 2); // jobs + job-log

        // Second call with the same runId must hit the cache and issue NO HTTP requests.
        var second = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(second);
        Assert.Same(first, second);
        Assert.Equal(firstRequestCount, handler.Captured.Count); // no new requests
    }

    [Fact]
    public async Task FetchJobLogsAsync_CacheEvictionAt20_OldestEvicted()
    {
        // Each runId gets a distinct failure log (TestName encodes the runId) so a cache hit
        // vs. miss is observable: a cached result carries its original TestName, while a
        // re-fetch carries the new handler's TestName.
        string FailureLogFor(string testName) => $$"""
            Failed {{testName}} [1 ms]
              Error Message:
                failure for {{testName}}
              Stack Trace:
                at {{testName}}()
            """;

        HttpClient ClientFor(string runId) =>
            new(new JobsAndLogsHandler(
                JobsJson(1, (100, "failure")),
                _ => LogResponse(FailureLogFor($"Test.{runId}"))),
                disposeHandler: false);

        var service = CreateServiceForLogFetch();

        // Fill the cache with exactly 20 distinct runIds.
        for (var i = 0; i < 20; i++)
        {
            var runId = $"run-{i:D2}";
            using var client = ClientFor(runId);
            var r = await service.FetchJobLogsAsync(runId, "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);
            Assert.NotNull(r);
            Assert.Equal($"Test.run-{i:D2}", r!.Failures[0].TestName);
        }

        // Insert a 21st distinct runId — this must evict the oldest (run-00).
        using (var client21 = ClientFor("run-20"))
        {
            var result21 = await service.FetchJobLogsAsync("run-20", "org", "test-repo", "test-token", client21, TestContext.Current.CancellationToken);
            Assert.NotNull(result21);
            Assert.Equal("Test.run-20", result21!.Failures[0].TestName);
        }

        // The evicted run-00 must now re-fetch via HTTP (cache miss). The new handler returns a
        // DIFFERENT TestName ("Evicted.run-00"), proving the stale cached result was evicted.
        var refetchHandler = new JobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("Evicted.run-00")));
        using var refetchClient = new HttpClient(refetchHandler, disposeHandler: false);
        var refetched = await service.FetchJobLogsAsync("run-00", "org", "test-repo", "test-token", refetchClient, TestContext.Current.CancellationToken);

        Assert.NotNull(refetched);
        Assert.Equal("Evicted.run-00", refetched!.Failures[0].TestName);
        // HTTP requests were issued for the re-fetch (cache miss).
        Assert.True(refetchHandler.Captured.Count >= 2);

        // The most recent entry (run-20) must still be cached — a second call must NOT issue
        // HTTP requests. Removal-proof: if run-20 were also evicted, this would re-fetch.
        var run20Handler = new JobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("ShouldNotBeFetched.run-20")));
        using var run20Client = new HttpClient(run20Handler, disposeHandler: false);
        var run20Cached = await service.FetchJobLogsAsync("run-20", "org", "test-repo", "test-token", run20Client, TestContext.Current.CancellationToken);
        Assert.NotNull(run20Cached);
        Assert.Equal("Test.run-20", run20Cached!.Failures[0].TestName); // original cached value, not re-fetched
        Assert.Empty(run20Handler.Captured); // no HTTP requests — cache hit
    }

    [Fact]
    public async Task FetchJobLogsAsync_CacheConcurrency_NeverExceedsBoundAndEvictsOldest()
    {
        // A REAL rendezvous: the cache is filled to capacity (20), then two concurrent calls
        // for two DISTINCT runIds are forced to both reach their cache miss before EITHER
        // response completes. The first call's handler parks on a gate; the second call's
        // handler signals that gate — so the second call cannot proceed until the first is
        // already past its (missed) cache lookup, and the first cannot proceed until the
        // second is also past its own. Both then fetch and both attempt to insert, so the
        // cache lock must serialize the two inserts: the bound must still hold at 20 and the
        // two oldest entries must be the ones evicted.
        string FailureLogFor(string testName) => $$"""
            Failed {{testName}} [1 ms]
              Error Message:
                failure for {{testName}}
              Stack Trace:
                at {{testName}}()
            """;

        var service = CreateServiceForLogFetch();

        // Fill the cache to exactly its capacity of 20 with distinct runIds.
        for (var i = 0; i < 20; i++)
        {
            var runId = $"concurrent-run-{i:D2}";
            var fillHandler = new JobsAndLogsHandler(
                JobsJson(1, (100, "failure")),
                _ => LogResponse(FailureLogFor($"Test.{runId}")));
            using var fillClient = new HttpClient(fillHandler, disposeHandler: false);
            var filled = await service
                .FetchJobLogsAsync(runId, "org", "test-repo", "test-token", fillClient, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotNull(filled);
        }

        // The rendezvous gate. RunContinuationsAsynchronously keeps the signalling call from
        // inlining the parked call's continuation onto its own thread.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // First call: parks on the gate before producing any response — it has already missed
        // the cache at this point.
        var handlerA = new GatedJobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("Test.race-run-a")),
            async () => await gate.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // Second call: reaching its own (missed) cache lookup releases the first call.
        var handlerB = new GatedJobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("Test.race-run-b")),
            () =>
            {
                gate.TrySetResult(true);
                return Task.CompletedTask;
            });

        using var clientA = new HttpClient(handlerA, disposeHandler: false);
        using var clientB = new HttpClient(handlerB, disposeHandler: false);

        var task1 = service.FetchJobLogsAsync("race-run-a", "org", "test-repo", "test-token", clientA, TestContext.Current.CancellationToken);
        var task2 = service.FetchJobLogsAsync("race-run-b", "org", "test-repo", "test-token", clientB, TestContext.Current.CancellationToken);

#pragma warning disable xUnit1051 // Timeout-only WaitAsync is intentional: the bound must surface a TimeoutException if the rendezvous deadlocks
        var raceResults = await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1051

        Assert.NotNull(raceResults[0]);
        Assert.NotNull(raceResults[1]);
        Assert.Equal("Test.race-run-a", raceResults[0]!.Failures[0].TestName);
        Assert.Equal("Test.race-run-b", raceResults[1]!.Failures[0].TestName);

        // 22 inserts against a bound of 20: the two oldest must have been evicted. A re-fetch
        // of concurrent-run-00 must therefore MISS and return the new handler's value.
        var evictedHandler = new JobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("Evicted.concurrent-run-00")));
        using var evictedClient = new HttpClient(evictedHandler, disposeHandler: false);
        var evictedResult = await service
            .FetchJobLogsAsync("concurrent-run-00", "org", "test-repo", "test-token", evictedClient, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(evictedResult);
        Assert.Equal("Evicted.concurrent-run-00", evictedResult!.Failures[0].TestName);
        Assert.True(evictedHandler.Captured.Count >= 2); // re-fetched — cache miss

        var evicted2Handler = new JobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("Evicted.concurrent-run-01")));
        using var evicted2Client = new HttpClient(evicted2Handler, disposeHandler: false);
        var evicted2Result = await service
            .FetchJobLogsAsync("concurrent-run-01", "org", "test-repo", "test-token", evicted2Client, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(evicted2Result);
        Assert.Equal("Evicted.concurrent-run-01", evicted2Result!.Failures[0].TestName);
        Assert.True(evicted2Handler.Captured.Count >= 2); // re-fetched — cache miss

        // Both racing inserts are the newest entries and must still be cached: no HTTP at all.
        // Had the lock failed to serialize the inserts (e.g. one insert lost, or a duplicate
        // queue entry evicting a live key), one of these would re-fetch.
        foreach (var (runId, cachedName) in new[]
                 {
                     ("race-run-a", "Test.race-run-a"),
                     ("race-run-b", "Test.race-run-b"),
                 })
        {
            var retainedHandler = new JobsAndLogsHandler(
                JobsJson(1, (100, "failure")),
                _ => LogResponse(FailureLogFor($"ShouldNotBeFetched.{runId}")));
            using var retainedClient = new HttpClient(retainedHandler, disposeHandler: false);
            var retainedResult = await service
                .FetchJobLogsAsync(runId, "org", "test-repo", "test-token", retainedClient, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotNull(retainedResult);
            Assert.Equal(cachedName, retainedResult!.Failures[0].TestName); // original cached value
            Assert.Empty(retainedHandler.Captured); // no HTTP requests — cache hit
        }
    }

    [Fact]
    public async Task FetchJobLogsAsync_CacheConcurrentDuplicateInsert_NoDuplicateQueueEntries()
    {
        // The exact race the production lock guards: two concurrent misses for the SAME runId.
        // A real rendezvous forces both calls past their cache lookup (both miss) before either
        // response completes — the first call's handler parks on a gate, the second call's
        // handler signals it. Both then fetch and both attempt to insert the same key. The
        // lock must serialize the inserts so the key is enqueued exactly ONCE: a duplicate
        // queue entry would later make eviction dequeue a stale key and leave the real oldest
        // entry alive past the bound.
        string FailureLogFor(string testName) => $$"""
            Failed {{testName}} [1 ms]
              Error Message:
                failure for {{testName}}
              Stack Trace:
                at {{testName}}()
            """;

        var service = CreateServiceForLogFetch();
        var duplicateRunId = "dup-run-1";

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // First call parks on the gate (already past its missed cache lookup).
        var handler1 = new GatedJobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("Test.dup-run-1.v1")),
            async () => await gate.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // Second call signals the gate once it too has missed the cache.
        var handler2 = new GatedJobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("Test.dup-run-1.v2")),
            () =>
            {
                gate.TrySetResult(true);
                return Task.CompletedTask;
            });

        using var client1 = new HttpClient(handler1, disposeHandler: false);
        using var client2 = new HttpClient(handler2, disposeHandler: false);

        var task1 = service.FetchJobLogsAsync(duplicateRunId, "org", "test-repo", "test-token", client1, TestContext.Current.CancellationToken);
        var task2 = service.FetchJobLogsAsync(duplicateRunId, "org", "test-repo", "test-token", client2, TestContext.Current.CancellationToken);

#pragma warning disable xUnit1051 // Timeout-only WaitAsync is intentional: the bound must surface a TimeoutException if the rendezvous deadlocks
        var results = await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1051

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        // Both calls genuinely missed the cache and fetched: each handler issued its own
        // jobs + job-log requests. (A cache hit would have issued none.)
        Assert.True(handler1.Captured.Count >= 2);
        Assert.True(handler2.Captured.Count >= 2);
        // Each caller sees its own fetched value; the cache keeps whichever insert ran last,
        // overwritten in place without a duplicate queue entry.
        Assert.Equal("Test.dup-run-1.v1", results[0]!.Failures[0].TestName);
        Assert.Equal("Test.dup-run-1.v2", results[1]!.Failures[0].TestName);

        // Now fill the cache to exactly 20 entries (19 more distinct runIds + the duplicate).
        for (var i = 2; i <= 20; i++)
        {
            var runId = $"dup-run-{i}";
            var handler = new JobsAndLogsHandler(
                JobsJson(1, (100, "failure")),
                _ => LogResponse(FailureLogFor($"Test.dup-run-{i}")));
            using var client = new HttpClient(handler, disposeHandler: false);
            await service
                .FetchJobLogsAsync(runId, "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        // Insert a 21st DISTINCT entry to trigger eviction of the oldest (dup-run-1). If the
        // duplicate-insert race had left a stale queue entry, the eviction loop could dequeue a
        // non-existent key and dup-run-1 would NOT be evicted.
        var triggerHandler = new JobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("Trigger.dup-run-21")));
        using var triggerClient = new HttpClient(triggerHandler, disposeHandler: false);
        await service
            .FetchJobLogsAsync("dup-run-21", "org", "test-repo", "test-token", triggerClient, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The evicted dup-run-1 must now re-fetch via HTTP (cache miss) — proving it was evicted
        // and the duplicate-insert race did not leave a stale queue entry.
        var evictedHandler = new JobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("EvictedAfterDup.dup-run-1")));
        using var evictedClient = new HttpClient(evictedHandler, disposeHandler: false);
        var evicted = await service
            .FetchJobLogsAsync("dup-run-1", "org", "test-repo", "test-token", evictedClient, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(evicted);
        Assert.Equal("EvictedAfterDup.dup-run-1", evicted!.Failures[0].TestName);
        Assert.True(evictedHandler.Captured.Count >= 2); // re-fetched — cache miss, proving eviction worked

        // The last inserted (dup-run-20) must still be cached.
        var lastHandler = new JobsAndLogsHandler(
            JobsJson(1, (100, "failure")),
            _ => LogResponse(FailureLogFor("ShouldNotBeFetched.dup-run-20")));
        using var lastClient = new HttpClient(lastHandler, disposeHandler: false);
        var lastCached = await service
            .FetchJobLogsAsync("dup-run-20", "org", "test-repo", "test-token", lastClient, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("Test.dup-run-20", lastCached!.Failures[0].TestName);
        Assert.Empty(lastHandler.Captured); // cache hit
    }

    [Fact]
    public async Task FetchJobLogsAsync_AuthPresentOnApiAbsentOnRedirect()
    {
        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, uri =>
        {
            // The job-log endpoint returns a 302 to a cross-host URL.
            if (uri.AbsolutePath.Contains("/logs", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri($"https://logs.example.com/{Guid.NewGuid():N}") }
                };
            }
            return LogResponse(SingleFailureLog());
        });
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        // The first two requests (jobs endpoint + job-log endpoint) are to api.github.com and
        // must carry the bearer token. The third request (the redirect target) is cross-host
        // and must NOT carry any Authorization header.
        var apiRequests = handler.Captured
            .Where(c => c.Url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var redirectRequest = handler.Captured
            .FirstOrDefault(c => !c.Url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase));

        Assert.NotEmpty(apiRequests);
        Assert.All(apiRequests, c =>
        {
            Assert.Equal("Bearer", c.AuthorizationScheme);
            Assert.Equal("test-token", c.AuthorizationParameter);
        });
        Assert.NotNull(redirectRequest);
        Assert.Null(redirectRequest!.AuthorizationScheme);
        Assert.Null(redirectRequest.AuthorizationParameter);
    }

    [Fact]
    public async Task FetchJobLogsAsync_Cancellation_PropagatesOperationCanceledException()
    {
        var jobsBody = JobsJson(1, (111, "failure"));
        using var cts = new CancellationTokenSource();
        var handler = new JobsAndLogsHandler(jobsBody, _ =>
        {
            // Cancel before the job-log fetch can complete, simulating a cancellation that
            // arrives during the log download.
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, cts.Token));
    }

    /// <summary>
    /// The no-failure fallback excerpt is a bounded HEAD+TAIL of the redacted log with an explicit
    /// decoded-character gap marker — not a last-500 slice.
    /// </summary>
    /// <remarks>
    /// The old implementation took the last 500 characters of the sanitized log, so the reader
    /// never saw the beginning of the log at all. The new excerpt keeps the first
    /// <see cref="CiLogStreamParser.FallbackHeadChars"/> and the last
    /// <see cref="CiLogStreamParser.FallbackTailChars"/> characters and states exactly how many
    /// characters were dropped in between.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_FallbackSnippet_IsHeadPlusTailWithGapMarker()
    {
        // Short lines keep every line inside the per-line cap, so only the head/tail excerpt
        // arithmetic is under test here.
        const int LineChars = 99; // 98 'w' + '\n'
        const int LineCount = 200;
        var log = "HEAD_MARKER_START\n"
            + string.Concat(Enumerable.Repeat(new string('w', LineChars - 1) + "\n", LineCount))
            + "TAIL_MARKER_END\n";

        Assert.True(log.Length > CiLogStreamParser.FallbackHeadChars + CiLogStreamParser.FallbackTailChars,
            "The log must exceed the head+tail excerpt so the excerpt really truncates.");

        // CONTROL: the old last-500 slice would start at the END of the log — the head marker and
        // the whole head window would be missing entirely.
        var oldLast500 = log[^500..];
        Assert.DoesNotContain("HEAD_MARKER_START", oldLast500, StringComparison.Ordinal);

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result!.Failures);
        Assert.NotNull(result.FallbackSnippet);

        var snippet = result.FallbackSnippet!;
        var omittedChars = log.Length - CiLogStreamParser.FallbackHeadChars - CiLogStreamParser.FallbackTailChars;
        var marker = $"…[truncated: {omittedChars:N0} characters omitted between the head and the tail of the log]…";

        // Head window + explicit gap marker + tail window, nothing else.
        Assert.Equal(
            log[..CiLogStreamParser.FallbackHeadChars]
                + "\n" + marker + "\n"
                + log[(log.Length - CiLogStreamParser.FallbackTailChars)..],
            snippet);
        Assert.DoesNotContain("last 500", snippet, StringComparison.Ordinal);
        // The head window is present: the log's beginning is no longer dropped.
        Assert.StartsWith(log[..CiLogStreamParser.FallbackHeadChars], snippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The no-failure fallback excerpt is taken from the REDACTED text: a secret positioned inside
    /// the retained tail window is redacted, never carried through verbatim.
    /// </summary>
    /// <remarks>
    /// Removal-proof: if the excerpt were taken from the raw text first, the raw secret would
    /// survive in the retained tail. The head window is present too, so the excerpt really is a
    /// head+tail excerpt and not just the tail.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_FallbackSnippet_SanitizedBeforeExcerpt()
    {
        var token = "ghp_" + new string('a', 36);
        const string TailText = "tail-";

        // Short lines keep every line inside the per-line cap, so only the head/tail excerpt
        // arithmetic is under test here.
        var filler = string.Concat(Enumerable.Repeat("zzzzzzzzzz\n", 400)); // 4,000 chars
        var log = filler + TailText + token + "-end\n";

        Assert.True(log.Length > CiLogStreamParser.FallbackHeadChars + CiLogStreamParser.FallbackTailChars,
            "The log must exceed the head+tail excerpt so the excerpt really truncates.");

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result!.Failures); // no parseable failures → fallback excerpt
        Assert.NotNull(result.FallbackSnippet);

        // The token is redacted in the retained tail, and the tail itself survives.
        Assert.DoesNotContain(token, result.FallbackSnippet, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", result.FallbackSnippet, StringComparison.Ordinal);
        Assert.Contains(TailText + "***REDACTED***-end", result.FallbackSnippet, StringComparison.Ordinal);
        // The head window is present and the gap between the windows is reported explicitly.
        Assert.StartsWith(log[..CiLogStreamParser.FallbackHeadChars], result.FallbackSnippet, StringComparison.Ordinal);
        Assert.Contains("characters omitted between the head and the tail of the log", result.FallbackSnippet, StringComparison.Ordinal);

        // The excerpt is taken from the REDACTED text: the tail window ends the excerpt exactly.
        var redacted = CiMonitorService.SanitizeLogContent(log);
        Assert.EndsWith(redacted[^CiLogStreamParser.FallbackTailChars..], result.FallbackSnippet, StringComparison.Ordinal);
        Assert.EndsWith("-end\n", result.FallbackSnippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchJobLogsAsync_LogDerivedFields_SanitizedWithoutTruncation()
    {
        // Removal-proof: a parsed test name, error message, and stack trace are each >500 chars
        // and contain a ghp_ token. The resulting issue fields must contain the COMPLETE
        // sanitized content (full length, secret redacted) — proving these fields are sanitized
        // but NOT truncated. If truncation were applied to these fields, the length would be
        // capped at 500 and the content would be cut.
        var token = "ghp_" + new string('a', 36);
        var longName = "Tests." + new string('N', 600);
        var longError = "Error: " + token + " " + new string('E', 600);
        var longStack = "at " + token + " " + new string('S', 600);

        var log = $$"""
            Failed {{longName}} [1 ms]
              Error Message:
                {{longError}}
              Stack Trace:
                {{longStack}}
            """;

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var failure = Assert.Single(result!.Failures);

        // The test name is sanitized (token redacted) but NOT truncated — full length preserved.
        var expectedTestName = CiMonitorService.SanitizeLogContent(longName);
        Assert.Equal(expectedTestName, failure.TestName);
        Assert.True(failure.TestName.Length > 500);
        Assert.DoesNotContain(token, failure.TestName);

        // The error message is sanitized but NOT truncated.
        var expectedError = CiMonitorService.SanitizeLogContent(longError);
        Assert.Equal(expectedError, failure.Error);
        Assert.True(failure.Error.Length > 500);
        Assert.DoesNotContain(token, failure.Error);
        Assert.Contains("***REDACTED***", failure.Error);

        // The stack trace is sanitized but NOT truncated.
        var expectedStack = CiMonitorService.SanitizeLogContent(longStack);
        Assert.Equal(expectedStack, failure.StackTrace);
        Assert.True(failure.StackTrace.Length > 500);
        Assert.DoesNotContain(token, failure.StackTrace);
        Assert.Contains("***REDACTED***", failure.StackTrace);
    }

    [Fact]
    public void SanitizeLogContent_LongInput_FullLengthWithSecretsRedacted()
    {
        // Boundary: SanitizeLogContent does NOT truncate. A very long input is returned at
        // full length with only secrets redacted. The expected length is the input length minus
        // each secret's length plus the replacement length.
        var padding = new string('x', 50_000);
        var token = "ghp_" + new string('b', 36);
        var input = $"start {padding} {token} end {padding}";

        var sanitized = CiMonitorService.SanitizeLogContent(input);

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("***REDACTED***", sanitized);
        var expectedLength = input.Length - token.Length + "***REDACTED***".Length;
        Assert.Equal(expectedLength, sanitized.Length);
    }

    [Fact]
    public async Task FetchJobLogsAsync_SecretSplitAcrossChunks_IsRedacted()
    {
        // A ghp_ token split across two stream chunks must not leak. Redaction runs on each COMPLETE
        // line: the parser carries the incomplete tail of a chunk (the partial line) and only hands it
        // to the redactor once the line's newline arrives, so both halves of the token are redacted
        // together. The token starts exactly at the chunk boundary here — its "ghp" prefix ends chunk
        // N and its 36-character suffix opens chunk N+1.
        var chunk = CiLogStreamParser.StreamChunkBytes;
        var token = "ghp_" + new string('c', 36);

        // Pad to the chunk boundary minus 3 so the token's first three bytes ("ghp") are the last
        // bytes of the first chunk. Short lines keep the per-line cap out of the picture.
        var fillerLines = string.Concat(Enumerable.Repeat("0123456789\n", (chunk - 3) / 11));
        var prefix = fillerLines + new string('x', chunk - 3 - fillerLines.Length);
        Assert.Equal(chunk - 3, Encoding.UTF8.GetByteCount(prefix));

        var log = prefix + token + "\nrest of the log\n";
        var body = Encoding.UTF8.GetBytes(log);

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(body));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result!.Failures); // no parseable failures → fallback excerpt
        Assert.NotNull(result.FallbackSnippet);

        // The raw token never appears, and its redaction marker does.
        Assert.DoesNotContain(token, result.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", result.FallbackSnippet, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", result.FallbackSnippet, StringComparison.Ordinal);
        Assert.Contains("rest of the log", result.FallbackSnippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchJobLogsAsync_SecretCutByLineCap_IsRedacted()
    {
        // Truncation-boundary redaction: a ghp_ token straddling the per-line cap is INCOMPLETE in
        // the retained prefix, so the fixed-length token pattern cannot match it. The redactor's
        // unredactable-tail trim replaces everything from the surviving trigger to the end of the
        // retained line, so no fragment of the secret reaches the excerpt.
        var token = "ghp_" + new string('d', 36);

        // Start the token so the line cap cuts it roughly in half.
        var line = new string('q', MaxLogLineChars - 20) + token + new string('e', 500);
        Assert.True(line.Length > MaxLogLineChars, "The line must exceed the per-line cap.");

        var log = line + "\nafter truncation\n";

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result!.Failures);
        Assert.NotNull(result.FallbackSnippet);

        var snippet = result.FallbackSnippet!;
        // No fragment of the token survives, in any prefix form.
        Assert.DoesNotContain("ghp_", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('d', 36), snippet, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", snippet, StringComparison.Ordinal);
        // The fallback excerpt is bounded only by its own head/tail windows, not by the failure-extraction
        // line cap, so this 8,712-character line is shown whole (its 520 dropped characters fit the tail
        // ring) — with the cut token still redacted — and the log continues after it.
        Assert.Contains(new string('e', 500), snippet, StringComparison.Ordinal);
        Assert.Contains("after truncation", snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchJobLogsAsync_RetentionOverflow_CarriesVisibleSummary()
    {
        // More parseable failures than the retention bound: the earliest ones are retained, and the
        // result carries an overflow summary counting the omitted OCCURRENCES. The summary lives on
        // LogFetchResult.Overflow (not in the unused FallbackSnippet) so the caller always renders it.
        // The cap is read into a runtime local first so the addition below is evaluated at
        // runtime: a compile-time constant expression would overflow while building a mutant
        // that raises the cap, which would turn a test failure into a build failure.
        var retainedCap = CiLogStreamParser.MaxRetainedFailureCount;
        var failureCount = retainedCap + 3;
        var failureLog = string.Concat(Enumerable.Range(0, failureCount).Select(i =>
            $"Failed Tests.Overflow.Case{i:D4} [1 ms]\n  Error Message:\n    boom {i}\n  Stack Trace:\n    at Case{i:D4}()\n\n"));

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(failureLog));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(result!.Overflow);

        // The EARLIEST failures as fit are retained, in log order...
        Assert.Equal(CiLogStreamParser.MaxRetainedFailureCount, result.Failures.Count);
        Assert.Equal("Tests.Overflow.Case0000", result.Failures[0].TestName);
        Assert.Equal($"Tests.Overflow.Case{CiLogStreamParser.MaxRetainedFailureCount - 1:D4}", result.Failures[^1].TestName);

        // ...and the summary reports the omitted OCCURRENCE count (parsed - retained).
        Assert.Contains($"3 of {failureCount}", result.Overflow, StringComparison.Ordinal);
        Assert.Contains("occurrence(s) were not retained", result.Overflow, StringComparison.Ordinal);
        // No fallback excerpt is used when failures parsed.
        Assert.Null(result.FallbackSnippet);
    }

    /// <summary>
    /// A partial fixed-length GitHub token that is cut by the END of the log (the log simply stops
    /// mid-token) is redacted, not carried through as a fragment.
    /// </summary>
    /// <remarks>
    /// The fixed-length <c>ghp_</c>/<c>gho_</c>/<c>ghs_</c> patterns only match when all 36 trailing
    /// characters are present, so an INCOMPLETE token never matches them. Redaction therefore runs
    /// <see cref="CiLogRedactor.TrimUnredactableTail"/> on <em>every</em> complete line, not only on
    /// lines cut by the per-line cap: the last line of a log can end early simply because the log
    /// ends there.
    /// <para>
    /// Removal-proof: with the trim restricted to the truncation path (the previous behaviour) the
    /// trigger and its partial suffix survive into the excerpt. The value-bounded categories
    /// (<c>Bearer</c>, secret environment variables, <c>Password=</c>) were never affected because
    /// they redact to the end of the line.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ghp")]
    [InlineData("gho")]
    [InlineData("ghs")]
    public async Task FetchJobLogsAsync_PartialFixedLengthTokenAtEndOfLog_IsRedacted(string category)
    {
        var partial = category + "_" + new string('a', 10);
        var log = "some log text\n" + partial;

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(result.FallbackSnippet);

        // The partial token is gone, replaced by the redaction marker; the rest of the log survives.
        Assert.DoesNotContain(partial, result.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(category + "_", result.FallbackSnippet, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", result.FallbackSnippet, StringComparison.Ordinal);
        Assert.Contains("some log text", result.FallbackSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The end-to-end consequence: a partial fixed-length token at the end of the log must not reach
    /// the created issue's body, which is user- and model-visible.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_PartialFixedLengthTokenAtEndOfLog_DoesNotReachIssueBody()
    {
        var partial = "ghp_" + new string('a', 10);
        var log = "some log text\n" + partial;

        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => log);
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(issueStore.Issues.Values);

        // The partial token never reaches the issue body; its redaction marker does.
        Assert.DoesNotContain(partial, issue.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", issue.Description, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", issue.Description, StringComparison.Ordinal);
        Assert.Contains("some log text", issue.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A read-only stream that serves a sequence of (bytes, repeat-count) segments WITHOUT materialising
    /// them: a segment is one small byte array handed out <c>repeat</c> times. This lets a test stream
    /// more than 2^31 decoded characters through the parser using a few kilobytes of memory. When
    /// <c>failAtEnd</c> is set the stream throws <see cref="IOException"/> instead of reporting the end
    /// of the body, simulating a download that breaks part-way.
    /// </summary>
    private sealed class SegmentedStream : Stream
    {
        private readonly (byte[] Bytes, long Repeat)[] _segments;
        private readonly bool _failAtEnd;
        private int _segment;
        private long _repeatsDone;
        private int _offsetInRepeat;

        public SegmentedStream(bool failAtEnd, params (byte[] Bytes, long Repeat)[] segments)
        {
            _segments = segments;
            _failAtEnd = failAtEnd;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            while (_segment < _segments.Length && _repeatsDone >= _segments[_segment].Repeat)
            {
                _segment++;
                _repeatsDone = 0;
                _offsetInRepeat = 0;
            }

            if (_segment >= _segments.Length)
            {
                if (_failAtEnd)
                    throw new IOException("simulated broken log download");
                return ValueTask.FromResult(0);
            }

            var bytes = _segments[_segment].Bytes;
            var count = Math.Min(destination.Length, bytes.Length - _offsetInRepeat);
            bytes.AsSpan(_offsetInRepeat, count).CopyTo(destination.Span);
            _offsetInRepeat += count;
            if (_offsetInRepeat == bytes.Length)
            {
                _offsetInRepeat = 0;
                _repeatsDone++;
            }

            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string FailureBlockText(string name, string error) =>
        $"Failed {name} [1 ms]\n  Error Message:\n    {error}\n  Stack Trace:\n    at {name}()\n\n";

    private async Task<LogFetchResult?> FetchSingleLogAsync(string body)
    {
        var handler = new JobsAndLogsHandler(JobsJson(1, (111, "failure")), _ => LogResponse(body));
        using var client = new HttpClient(handler, disposeHandler: false);
        return await CreateServiceForLogFetch().FetchJobLogsAsync(
            "run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);
    }

    // ── Review-repair regressions (iteration 2) ─────────────────────────────

    private const string StampPrefix = "2026-08-22T09:47:38.1197374Z ";

    /// <summary>
    /// Finding 1: a secret environment variable on a raw, TIMESTAMPED Actions line is redacted. Redaction
    /// runs before the timestamp is stripped, and the env pattern is anchored at the line start, so the
    /// timestamp used to hide the key from the pattern and the secret survived verbatim.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_TimestampedSecretEnvVar_IsRedacted()
    {
        var log = StampPrefix + "Starting\n" + StampPrefix + "MY_API_TOKEN=SuperSecretValue123\n" + StampPrefix + "done\n";

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.DoesNotContain("SuperSecretValue123", result!.FallbackSnippet!, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecret", result.FallbackSnippet!, StringComparison.Ordinal);
        Assert.Contains(StampPrefix + "MY_API_TOKEN=***REDACTED***", result.FallbackSnippet!, StringComparison.Ordinal);
        Assert.Contains("done", result.FallbackSnippet!, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeLogContent_TimestampedSecretEnvVar_RedactsValueKeepsTimestampAndKey()
    {
        var sanitized = CiMonitorService.SanitizeLogContent(StampPrefix + "DB_PASSWORD=hunter2\nplain line");

        Assert.Equal(StampPrefix + "DB_PASSWORD=***REDACTED***\nplain line", sanitized);
    }

    /// <summary>Finding 1, chunk-split variant: the timestamped env line straddles the 16 KiB read boundary.</summary>
    [Fact]
    public async Task FetchJobLogsAsync_TimestampedSecretEnvVar_SplitAcrossChunkBoundary_IsRedacted()
    {
        const string line = "MY_API_TOKEN=SuperSecretValue123";
        var start = StreamChunkBytes - StampPrefix.Length - 20; // boundary falls 20 chars into the value
        var sb = new StringBuilder();
        AppendFillerTo(sb, start);
        sb.Append(StampPrefix).Append(line).Append('\n').Append(StampPrefix).Append("end of log\n");
        Assert.True(start + StampPrefix.Length < StreamChunkBytes && StreamChunkBytes < start + StampPrefix.Length + line.Length);

        var result = await FetchSingleLogAsync(sb.ToString());

        Assert.NotNull(result?.FallbackSnippet);
        Assert.DoesNotContain("SuperSecret", result!.FallbackSnippet!, StringComparison.Ordinal);
        Assert.Contains("MY_API_TOKEN=***REDACTED***", result.FallbackSnippet!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 1, line-cap variant: 9,000 characters appended to the timestamped assignment used to leak it
    /// through the truncation boundary (the secret stays in the retained head).
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_TimestampedSecretEnvVar_CutByLineCap_IsRedacted()
    {
        var log = StampPrefix + "MY_API_TOKEN=SuperSecretValue123" + new string('z', 9_000) + "\n" + StampPrefix + "after\n";

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.DoesNotContain("SuperSecret", result!.FallbackSnippet!, StringComparison.Ordinal);
        Assert.DoesNotContain("zzzzzzzzzz", result.FallbackSnippet!, StringComparison.Ordinal);
        Assert.Contains("MY_API_TOKEN=***REDACTED***", result.FallbackSnippet!, StringComparison.Ordinal);
        Assert.Contains("after", result.FallbackSnippet!, StringComparison.Ordinal);
    }

    /// <summary>Finding 1, parsed-failure variant: a timestamped env line inside a failure's error text.</summary>
    [Fact]
    public async Task FetchJobLogsAsync_TimestampedSecretEnvVar_InsideFailureError_IsRedacted()
    {
        var log = string.Join('\n',
            StampPrefix + "failed Tests.Env.Case (1ms)",
            StampPrefix + "  from /repo/Tests.dll (net10.0|x64)",
            StampPrefix + "MY_API_TOKEN=SuperSecretValue123",
            StampPrefix + "    at Env.Case()",
            StampPrefix + "Test run summary: Failed!") + "\n";

        var result = await FetchSingleLogAsync(log);

        var failure = Assert.Single(result!.Failures);
        Assert.DoesNotContain("SuperSecret", failure.Error + failure.StackTrace, StringComparison.Ordinal);
        Assert.Equal("MY_API_TOKEN=***REDACTED***", failure.Error);
    }

    /// <summary>Finding 1, end to end: the secret reaches neither a created issue nor a dedup append.</summary>
    [Fact]
    public async Task MonitorMergeAsync_TimestampedSecretEnvVar_NeverReachesIssueOrDedupAppend()
    {
        var log = StampPrefix + "building\n" + StampPrefix + "MY_API_TOKEN=SuperSecretValue123\n";

        // Created issue.
        var created = new FakeIssueStore();
        var service = CreateService(
            new CiFailureRoutingHandler(CheckRunsJsonWithDetailsUrl("123", summary: null), _ => JobsJsonOneFailedJob(111), _ => log),
            issueStore: created, eventBus: new RecordingEventBus());
        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        var issue = Assert.Single(created.Issues.Values);
        Assert.DoesNotContain("SuperSecret", issue.Description, StringComparison.Ordinal);
        Assert.Contains("MY_API_TOKEN=***REDACTED***", issue.Description, StringComparison.Ordinal);

        // Dedup append onto an existing issue.
        var deduped = new FakeIssueStore();
        var clock = new ControlledTimeProvider();
        var dedupService = CreateService(
            new CiFailureRoutingHandler(CheckRunsJsonWithDetailsUrl("123", summary: null), _ => JobsJsonOneFailedJob(111), _ => log),
            issueStore: deduped, eventBus: new RecordingEventBus(), clock: clock);
        SeedExistingIssue(deduped, "CI failure: build", "Original.");
        await dedupService.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        var existing = Assert.Single(deduped.Issues.Values);
        Assert.DoesNotContain("SuperSecret", existing.Description, StringComparison.Ordinal);
        Assert.Contains("MY_API_TOKEN=***REDACTED***", existing.Description, StringComparison.Ordinal);
        Assert.Contains($"\n\n---\n{ExpectedStamp(clock)}\n", existing.Description);
    }

    /// <summary>
    /// Finding 2: the fallback tail is the ACTUAL end of the stream even when the last line is far longer
    /// than the failure-extraction line cap. The excerpt windows are bounded only by their own sizes.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_SingleHugeLine_FallbackTailIsTheActualStreamTail()
    {
        var log = "HEAD_MARKER" + new string('x', 20_000) + "ACTUAL_TAIL_MARKER";
        Assert.True(log.Length > MaxLogLineChars);

        var result = await FetchSingleLogAsync(log);

        Assert.Empty(result!.Failures);
        var snippet = result.FallbackSnippet!;
        Assert.StartsWith("HEAD_MARKER", snippet, StringComparison.Ordinal);
        Assert.EndsWith("ACTUAL_TAIL_MARKER", snippet, StringComparison.Ordinal);
        // Bounded: head window + gap marker + tail window, never the whole 20 KB line.
        Assert.True(snippet.Length < CiLogStreamParser.FallbackHeadChars + CiLogStreamParser.FallbackTailChars + 200,
            $"The excerpt is {snippet.Length} characters — the tail capture must stay bounded.");
        // The gap is stated in decoded characters of the actual stream.
        var gap = log.Length - CiLogStreamParser.FallbackHeadChars - (snippet.Length - snippet.IndexOf("]…\n", StringComparison.Ordinal) - "]…\n".Length);
        Assert.Contains($"…[truncated: {gap:N0} characters omitted between the head and the tail of the log]…", snippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 2, secret-fragment variant: the tail window of an oversized line begins INSIDE a GitHub token
    /// whose start was dropped, so no fragment of the token may be shown.
    /// </summary>
    [Theory]
    [InlineData("ghp")]
    [InlineData("gho")]
    [InlineData("ghs")]
    public async Task FetchJobLogsAsync_OversizedLine_TailStartingInsideToken_ShowsNoFragment(string prefix)
    {
        var token = prefix + "_" + new string('a', 36);
        // The last FallbackTailChars of the line start 20 characters into the token body.
        var log = new string('q', 9_000) + token + new string('.', CiLogStreamParser.FallbackTailChars - 20) + "\nend\n";

        var result = await FetchSingleLogAsync(log);

        var snippet = result!.FallbackSnippet!;
        Assert.DoesNotContain("aaaaaaaa", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(prefix + "_", snippet, StringComparison.Ordinal);
        Assert.Contains("end", snippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 2, value-bounded secrets: when a Bearer / Password= / secret-env trigger passes through an
    /// oversized line — including inside the dropped middle — the line's tail is not shown, because its
    /// start may lie inside the secret value.
    /// </summary>
    [Theory]
    [InlineData("bearer")]
    [InlineData("password-in-dropped-middle")]
    [InlineData("envvar")]
    [InlineData("timestamped-envvar")]
    public async Task FetchJobLogsAsync_OversizedLine_ValueBoundedSecretNeverReachesTail(string category)
    {
        var line = category switch
        {
            "bearer" => "Authorization: Bearer " + new string('B', 20_000) + "TAILSECRET",
            "password-in-dropped-middle" => new string('x', 9_000) + "Password=" + new string('p', 20_000) + "TAILSECRET",
            "envvar" => "MY_API_TOKEN=" + new string('S', 20_000) + "TAILSECRET",
            _ => StampPrefix + "MY_API_TOKEN=" + new string('S', 20_000) + "TAILSECRET",
        };

        var result = await FetchSingleLogAsync(line + "\nafter\n");

        var snippet = result!.FallbackSnippet!;
        Assert.DoesNotContain("TAILSECRET", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("BBBBBBBB", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("pppppppp", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("SSSSSSSS", snippet, StringComparison.Ordinal);
        Assert.Contains("after", snippet, StringComparison.Ordinal);
    }

    /// <summary>Finding 2: a token straddling the head-window edge of the excerpt is already redacted whole.</summary>
    [Fact]
    public async Task FetchJobLogsAsync_TokenStraddlingExcerptHeadEdge_IsRedactedWhole()
    {
        var token = "ghp_" + new string('c', 36);
        var sb = new StringBuilder();
        AppendFillerTo(sb, CiLogStreamParser.FallbackHeadChars - 10);
        sb.Append("   ").Append(token).Append('\n');
        AppendFillerTo(sb, sb.Length + 4_000);

        var result = await FetchSingleLogAsync(sb.ToString());

        var snippet = result!.FallbackSnippet!;
        Assert.DoesNotContain("ghp_", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("cccccccc", snippet, StringComparison.Ordinal);
    }

    private static string BlockLinesLog(string header, IEnumerable<string> body, string closing) =>
        header + "\n" + string.Join('\n', body) + "\n" + closing;

    /// <summary>
    /// Finding 3 (VSTest): a block whose stack runs past the block cap and is then closed by a blank line
    /// reports the truncation marker and the omitted count. The blank line used to be admitted after the
    /// longer lines were rejected, which ended the stack inside the retained lines and hid the truncation.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_VstestBlockBeyondCapClosedByBlankLine_ReportsTruncation()
    {
        var frames = Enumerable.Range(0, 1_000).Select(i => $"    at Big.Block.Frame{i:D4}()").ToList();
        var lines = new List<string> { "  Error Message:", "    big error", "  Stack Trace:" };
        lines.AddRange(frames);
        lines.Add(string.Empty); // the closing blank line
        var log = "Failed Tests.Big.Block [1 ms]\n" + string.Join('\n', lines) + "\n";

        var (retainedCount, omitted) = StrictPrefixSplit(lines);
        Assert.True(retainedCount < lines.Count - 1, "The cap must bind before the closing blank line.");

        var failure = Assert.Single((await FetchSingleLogAsync(log))!.Failures);

        var marker = $" …[truncated: {omitted:N0} characters omitted]…";
        Assert.EndsWith(marker, failure.StackTrace, StringComparison.Ordinal);
        Assert.Contains("Frame0000", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("Frame0999", failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>Finding 3 (MTP): same defect — trailing blanks changed bodyEnd and suppressed the marker.</summary>
    [Fact]
    public async Task FetchJobLogsAsync_MtpBlockBeyondCapFollowedByBlankAndFooter_ReportsTruncation()
    {
        var frames = Enumerable.Range(0, 1_000).Select(i => $"    at Big.Mtp.Frame{i:D4}()").ToList();
        var lines = new List<string> { "  from /repo/Tests.dll (net10.0|x64)", "  big mtp error" };
        lines.AddRange(frames);
        lines.Add(string.Empty);
        var log = "failed Tests.Big.Mtp (12ms)\n" + string.Join('\n', lines)
            + "\n/repo/Tests.dll (net10.0|x64) failed with 1 error(s) (1s 2ms)\n";

        var (_, omitted) = StrictPrefixSplit(lines);

        var failure = Assert.Single((await FetchSingleLogAsync(log))!.Failures);

        Assert.Equal("big mtp error", failure.Error);
        Assert.EndsWith($" …[truncated: {omitted:N0} characters omitted]…", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("Frame0999", failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 3, mixed sizes: after a long line is rejected, SHORT lines that would fit the remaining room
    /// must not be admitted — the retained lines are a strict prefix and every later line is counted omitted.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_BlockLinesThatWouldFitAfterAnOmission_AreStillOmitted()
    {
        var lines = new List<string> { "  Error Message:", "    big error", "  Stack Trace:" };
        for (var i = 0; i < 60; i++)
            lines.Add("    at Long.Frame" + i.ToString("D2") + "(" + new string('p', 380) + ")");
        lines.Add("    at Short.AfterOmission()"); // far shorter than the room a skipped long line left behind
        lines.Add("    at Short.AfterOmission2()");
        var log = "Failed Tests.Mixed.Block [1 ms]\n" + string.Join('\n', lines) + "\n";

        var (retainedCount, omitted) = StrictPrefixSplit(lines);
        Assert.True(retainedCount < lines.Count - 2, "A long line must have been rejected before the short lines.");

        var failure = Assert.Single((await FetchSingleLogAsync(log))!.Failures);

        Assert.DoesNotContain("Short.AfterOmission", failure.StackTrace, StringComparison.Ordinal);
        Assert.EndsWith($" …[truncated: {omitted:N0} characters omitted]…", failure.StackTrace, StringComparison.Ordinal);
        Assert.Contains($"Long.Frame{retainedCount - 4:D2}", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain($"Long.Frame{retainedCount - 3:D2}", failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 3 (MTP), retained trailing blank: the cap binds right AFTER a blank line that fit, so the
    /// retained lines END in a blank and the body trimming makes bodyEnd differ from the retained count.
    /// Deriving truncation from that position suppressed the marker although later lines were omitted.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_MtpBlockWhoseRetainedLinesEndInBlank_StillReportsTruncation()
    {
        var lines = new List<string> { "  from /repo/Tests.dll (net10.0|x64)", "  big mtp error" };
        long used = lines.Sum(l => l.Length + 1L);
        var i = 0;
        string Frame() => $"    at Blank.Edge.Frame{i:D4}()";
        while (used + Frame().Length + 1 <= MaxFailureBlockChars - 1)
        {
            lines.Add(Frame());
            used += Frame().Length + 1;
            i++;
        }

        lines.Add(string.Empty);          // fits: it is the LAST retained line
        lines.Add("    at Omitted.AfterBlank()");
        lines.Add("    at Omitted.AfterBlank2()");
        var (retainedCount, omitted) = StrictPrefixSplit(lines);
        Assert.Equal(lines.Count - 2, retainedCount);
        Assert.Equal(string.Empty, lines[retainedCount - 1]);

        var log = "failed Tests.Blank.Edge (12ms)\n" + string.Join('\n', lines)
            + "\n/repo/Tests.dll (net10.0|x64) failed with 1 error(s) (1s 2ms)\n";

        var failure = Assert.Single((await FetchSingleLogAsync(log))!.Failures);

        Assert.EndsWith($" …[truncated: {omitted:N0} characters omitted]…", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("Omitted.AfterBlank", failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 3 / iteration-1 note: a VSTest stack that a blank line CLOSED inside the retained lines is a
    /// complete failure; the many lines omitted after it are unrelated trailing output, so no marker.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_VstestStackClosedInsideRetainedLines_HasNoTruncationMarker()
    {
        var lines = new List<string> { "  Error Message:", "    err", "  Stack Trace:", "    at Done.Early()", string.Empty };
        lines.AddRange(Enumerable.Range(0, 1_000).Select(n => $"    unrelated trailing output line {n:D4} {new string('u', 40)}"));
        var log = "Failed Tests.Closed.Early [1 ms]\n" + string.Join('\n', lines) + "\n";
        Assert.True(StrictPrefixSplit(lines).OmittedChars > 0, "Lines must have been omitted after the closing blank.");

        var failure = Assert.Single((await FetchSingleLogAsync(log))!.Failures);

        Assert.Equal("at Done.Early()", failure.StackTrace);
        Assert.DoesNotContain("truncated", failure.StackTrace + failure.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The documented accounting rule: lines are retained in order until one does not fit the block cap;
    /// that line and EVERY later line are omitted. A line costs its characters plus one separator.
    /// </summary>
    private static (int RetainedCount, long OmittedChars) StrictPrefixSplit(List<string> lines)
    {
        long used = 0;
        var retained = 0;
        long omitted = 0;
        var truncated = false;
        foreach (var line in lines)
        {
            var cost = line.Length + 1;
            if (!truncated && used + cost <= MaxFailureBlockChars)
            {
                used += cost;
                retained++;
                continue;
            }

            truncated = true;
            omitted += cost;
        }

        return (retained, omitted);
    }

    /// <summary>
    /// Finding 4: a job whose download breaks after its blocks were already offered to the shared retention
    /// contributes NOTHING — it cannot displace the next job's failures — and the occurrence accounting
    /// stays exact (no overflow summary invented by a broken download).
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_FailedDownloadDoesNotPoisonSharedRetention()
    {
        var broken = Utf8(string.Concat(Enumerable.Range(0, MaxRetainedFailureCount + 1)
            .Select(i => FailureBlockText($"Tests.BrokenDownload.Case{i:D4}", "boom"))));
        var good = FailureBlockText("Tests.GoodDownload", "good boom");

        var handler = new JobsAndLogsHandler(JobsJson(2, (111, "failure"), (222, "failure")), uri =>
            uri.AbsolutePath.Contains("/111/", StringComparison.Ordinal)
                ? LogResponse(new SegmentedStream(failAtEnd: true, (broken, 1)))
                : LogResponse(good));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        var failure = Assert.Single(result!.Failures);
        Assert.Equal("Tests.GoodDownload", failure.TestName);
        Assert.Equal("good boom", failure.Error);
        Assert.Null(result.Overflow);

        // The cache keeps exactly this result: same object, no extra HTTP.
        var requests = handler.Captured.Count;
        var again = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);
        Assert.Same(result, again);
        Assert.Equal(requests, handler.Captured.Count);
    }

    /// <summary>
    /// Finding 4, ordering and accounting: a good job BEFORE the broken one keeps its failures, the broken
    /// job's partial failures are rolled back, and a later overflow summary counts only completed jobs.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_FailedDownloadAfterGoodJob_RollsBackOnlyTheBrokenJob()
    {
        var good = string.Concat(Enumerable.Range(0, MaxRetainedFailureCount - 1)
            .Select(i => FailureBlockText($"Tests.Good.Case{i:D4}", "ok")));
        var broken = Utf8(string.Concat(Enumerable.Range(0, 5).Select(i => FailureBlockText($"Tests.Broken.Case{i}", "boom"))));
        var tailJob = string.Concat(Enumerable.Range(0, 3).Select(i => FailureBlockText($"Tests.Tail.Case{i}", "tail")));

        var handler = new JobsAndLogsHandler(JobsJson(3, (111, "failure"), (222, "failure"), (333, "failure")), uri =>
            uri.AbsolutePath.Contains("/222/", StringComparison.Ordinal)
                ? LogResponse(new SegmentedStream(failAtEnd: true, (broken, 1)))
                : uri.AbsolutePath.Contains("/111/", StringComparison.Ordinal) ? LogResponse(good) : LogResponse(tailJob));
        using var client = new HttpClient(handler, disposeHandler: false);

        var result = await CreateServiceForLogFetch().FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        // 199 good + 1 of the 3 tail failures fit; the broken job's 5 are gone and not counted.
        Assert.Equal(MaxRetainedFailureCount, result!.Failures.Count);
        Assert.DoesNotContain(result.Failures, f => f.TestName.StartsWith("Tests.Broken", StringComparison.Ordinal));
        Assert.Equal("Tests.Tail.Case0", result.Failures[^1].TestName);
        Assert.Contains($"2 of {MaxRetainedFailureCount - 1 + 3}", result.Overflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 5: a newline-free stream of exactly 2^31 decoded characters (131,072 x one 16,384-character
    /// chunk) wrapped the narrow line counter negative, skipped the final-line flush, and lost the excerpt.
    /// The log is never materialised: one chunk's bytes are repeated.
    /// </summary>
    [Theory]
    [InlineData(131_072L)]
    [InlineData(131_073L)]
    public async Task FetchJobLogsAsync_NewlineFreeStreamOfTwoPow31Chars_IsFlushedWithExactGapSize(long repeats)
    {
        // Keep the exact original 2^31 vector and cross the marker's own int boundary too.
        var Repeats = repeats;
        var chunk = Utf8(new string('.', 16_384));
        var total = Repeats * chunk.Length;
        Assert.Equal((1L << 31) + (repeats - 131_072) * chunk.Length, total);

        var handler = new JobsAndLogsHandler(JobsJson(1, (111, "failure")), _ => LogResponse(new SegmentedStream(false, (chunk, Repeats))));
        using var client = new HttpClient(handler, disposeHandler: false);

        var result = await CreateServiceForLogFetch().FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result!.Failures);
        Assert.NotNull(result.FallbackSnippet); // the narrow counter skipped the flush: null
        var gap = total - CiLogStreamParser.FallbackHeadChars - CiLogStreamParser.FallbackTailChars;
        Assert.Contains($"…[truncated: {gap:N0} characters omitted between the head and the tail of the log]…", result.FallbackSnippet, StringComparison.Ordinal);
        Assert.True(result.FallbackSnippet!.Length < 4_200);
    }

    /// <summary>
    /// Finding 5, line-marker variant: the same 2^31-character line sits inside a failure's error text, so
    /// its capped form carries the per-line omission marker, whose DECODED-character count is exact.
    /// </summary>
    [Theory]
    [InlineData(131_072L)]
    [InlineData(131_073L)]
    public async Task FetchJobLogsAsync_TwoPow31CharLineInsideFailure_MarkerCountsDecodedCharsExactly(long repeats)
    {
        // At exactly 2^31 chars, unchecked int subtraction wraps BACK to the expected positive
        // omission count. The extra chunk makes the omission itself exceed int.MaxValue.
        var Repeats = repeats;
        var chunk = Utf8(new string('.', 16_384));
        var handler = new JobsAndLogsHandler(JobsJson(1, (111, "failure")), _ => LogResponse(new SegmentedStream(false,
            (Utf8("Failed Tests.Wide.Counter [1 ms]\n  Error Message:\n"), 1),
            (chunk, Repeats),
            (Utf8("\n  Stack Trace:\n    at Wide.Counter()\n\n"), 1))));
        using var client = new HttpClient(handler, disposeHandler: false);

        var result = await CreateServiceForLogFetch().FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        var failure = Assert.Single(result!.Failures);
        var omitted = Repeats * chunk.Length - MaxLogLineChars;
        Assert.EndsWith($" …[truncated: {omitted:N0} characters omitted]…", failure.Error, StringComparison.Ordinal);
        Assert.Equal("at Wide.Counter()", failure.StackTrace);
    }

    /// <summary>Block omission is visible on the error field when there are no retained stack frames.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchJobLogsAsync_ErrorOnlyBlockBeyondCap_ReportsTruncationInError(bool mtp)
    {
        var lines = new List<string> { mtp ? "  from /repo/Tests.dll (net10.0|x64)" : "  Error Message:" };
        lines.AddRange(Enumerable.Range(0, 4).Select(i => $"ERROR{i}:" + new string('e', 7_900)));
        lines.Add(string.Empty);
        var (retained, omitted) = StrictPrefixSplit(lines);
        Assert.True(retained > 1 && retained < lines.Count - 1);
        var log = BlockLinesLog(mtp ? "failed Tests.ErrorOnly.Case (1ms)" : "Failed Tests.ErrorOnly.Case [1 ms]",
            lines, mtp ? "Test run summary: Failed!\n" : string.Empty);

        var result = await FetchSingleLogAsync(log);

        var failure = Assert.Single(result!.Failures);
        Assert.Equal(string.Empty, failure.StackTrace);
        Assert.Equal(string.Join('\n', lines.Skip(1).Take(retained - 1))
            + $" …[truncated: {omitted:N0} characters omitted]…", failure.Error);
        Assert.DoesNotContain("ERROR3:", failure.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Block-omitted characters can exceed int.MaxValue even though each individual line is small.
    /// A huge single line does not test this counter, since it is capped before block accounting.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_BlockOmissionBeyondIntMax_ReportsExactPositiveCount()
    {
        const long repeats = 262_146;
        var line = new string('E', 8_191) + "\n";
        // Header plus one line fits; the second line exceeds the block cap and closes retention.
        Assert.True("  Error Message:".Length + 1 + line.Length <= MaxFailureBlockChars);
        Assert.True("  Error Message:".Length + 1 + line.Length * 2 > MaxFailureBlockChars);
        var omitted = (repeats - 1) * line.Length + 1; // includes the closing blank line
        Assert.True(omitted > int.MaxValue);
        var handler = new JobsAndLogsHandler(JobsJson(1, (111, "failure")), _ =>
            LogResponse(new SegmentedStream(false,
                (Utf8("Failed Tests.Wide.Block [1 ms]\n  Error Message:\n"), 1),
                (Utf8(line), repeats), (Utf8("\n"), 1))));
        using var client = new HttpClient(handler, disposeHandler: false);

        var result = await CreateServiceForLogFetch().FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        var failure = Assert.Single(result!.Failures);
        Assert.Equal(new string('E', 8_191) + $" …[truncated: {omitted:N0} characters omitted]…", failure.Error);
        Assert.Equal(string.Empty, failure.StackTrace);
    }

    /// <summary>
    /// A complete token begins within the leading token-character run of the raw tail. Removing
    /// exactly 40 leading characters must not bisect that token and expose its suffix.
    /// </summary>
    [Theory]
    [InlineData("ghp_")]
    [InlineData("gho_")]
    [InlineData("ghs_")]
    public async Task FetchJobLogsAsync_OversizedTailTrimMustNotBisectACompleteToken(string prefix)
    {
        var token = prefix + new string('a', 36);
        const string tailMarker = "SAFE_TAIL_MARKER";
        var safeSuffix = new string('.', CiLogStreamParser.FallbackTailChars - 70 - tailMarker.Length) + tailMarker;
        var rawTail = new string('q', 30) + token + safeSuffix;
        Assert.Equal(CiLogStreamParser.FallbackTailChars, rawTail.Length);
        Assert.Equal(token, rawTail.Substring(30, token.Length));
        // The current leading-run limit (40) falls SIX chars into the token's secret suffix.
        Assert.True(30 < 40 && 40 < 30 + token.Length);
        var log = new string('.', 12_000) + rawTail;

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result);
        Assert.Empty(result.Failures);
        Assert.NotNull(result.FallbackSnippet);
        Assert.DoesNotContain(new string('a', 8), result.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(prefix, result.FallbackSnippet, StringComparison.Ordinal);
        Assert.EndsWith(safeSuffix, result.FallbackSnippet, StringComparison.Ordinal);
        Assert.True(result.FallbackSnippet.Length < 4_200);
    }

    /// <summary>
    /// The same single complete token must not leak a suffix into either a created issue or a
    /// dedup append. No multiple-partial-trigger case is involved.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorMergeAsync_OversizedTailTrimTokenSuffixNeverReachesIssue(bool dedup)
    {
        const string tailMarker = "SAFE_TAIL_MARKER";
        var log = new string('.', 12_000) + new string('q', 30)
            + "ghp_" + new string('a', 36) + new string('.', 1_930 - tailMarker.Length) + tailMarker;
        var store = new FakeIssueStore();
        var clock = new ControlledTimeProvider();
        if (dedup)
            SeedExistingIssue(store, "CI failure: build", "Original safe description.");
        var handler = new CiFailureRoutingHandler(CheckRunsJsonWithDetailsUrl("123", summary: null),
            _ => JobsJsonOneFailedJob(111), _ => log);
        var service = CreateService(handler, issueStore: store, eventBus: new RecordingEventBus(), clock: clock);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(store.Issues.Values);
        if (dedup)
        {
            Assert.Equal("issue-existing", issue.Id);
            Assert.Contains($"\n\n---\n{ExpectedStamp(clock)}\n", issue.Description, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(new string('a', 8), issue.Description, StringComparison.Ordinal);
        Assert.Contains(tailMarker, issue.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The capped raw prefix may stop exactly at the assignment delimiter. An env value that
    /// begins just past the cap is still secret and cannot be shown in the fallback tail.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchJobLogsAsync_SecretEnvValueStartsAfterLineCap_TailIsSuppressed(bool timestamped)
    {
        var stamp = timestamped ? StampPrefix : string.Empty;
        const string keyStart = "MY_API_TOKEN";
        var key = keyStart + new string('_', MaxLogLineChars - stamp.Length - keyStart.Length - 1);
        var assignment = stamp + key + "=";
        Assert.Equal(MaxLogLineChars, assignment.Length);
        var log = assignment + new string('S', 3_000) + "TAILSECRET";
        // Positive control: this complete line is accepted by the supported secret-env pattern.
        Assert.Equal(assignment + "***REDACTED***", CiMonitorService.SanitizeLogContent(log));

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.DoesNotContain("TAILSECRET", result!.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 8), result.FallbackSnippet, StringComparison.Ordinal);
        Assert.True(result.FallbackSnippet!.Length < 4_200);
    }

    /// <summary>
    /// Defect-2 sibling: the capped prefix is a secret-env KEY that runs exactly to the cap, so even the
    /// <c>=</c> lies past the cut. The unseen remainder may be the delimiter and the value, so the tail is
    /// suppressed (fail closed) — a classifier that needs the <c>=</c> inside the prefix would publish it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchJobLogsAsync_SecretEnvKeyEndsAtLineCap_EqualsAndValuePastCap_TailIsSuppressed(bool timestamped)
    {
        var stamp = timestamped ? StampPrefix : string.Empty;
        const string keyStart = "MY_API_TOKEN";
        var key = keyStart + new string('_', MaxLogLineChars - stamp.Length - keyStart.Length);
        Assert.Equal(MaxLogLineChars, (stamp + key).Length);
        var log = stamp + key + "=" + new string('S', 3_000) + "TAILSECRET";
        // Positive control: the complete line is a supported secret assignment.
        Assert.EndsWith("=***REDACTED***", CiMonitorService.SanitizeLogContent(log), StringComparison.Ordinal);

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.DoesNotContain("TAILSECRET", result!.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 8), result.FallbackSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cut prefix ending at '=' is not automatically a secret assignment: safe prose, including
    /// prose mentioning TOKEN, must still show its actual tail. Both the prefix and the full line
    /// end with '=', so recognizing a pending secret value must remain key/assignment-specific.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchJobLogsAsync_ProsePrefixEndingAtEquals_DoesNotSuppressSafeTail(bool timestamped)
    {
        var start = (timestamped ? StampPrefix : string.Empty)
            + "Build progress: TOKEN names a documentation label, not a credential. ";
        var prefix = start + new string('x', MaxLogLineChars - start.Length - 1) + "=";
        Assert.Equal(MaxLogLineChars, prefix.Length);
        Assert.EndsWith("=", prefix, StringComparison.Ordinal);
        var log = prefix + new string('.', 3_000) + "ACTUAL_SAFE_TAIL_MARKER=";
        Assert.Equal(log, CiMonitorService.SanitizeLogContent(log));

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result);
        Assert.Empty(result.Failures);
        var gap = log.Length - CiLogStreamParser.FallbackHeadChars - CiLogStreamParser.FallbackTailChars;
        Assert.Equal(log[..CiLogStreamParser.FallbackHeadChars]
            + $"\n…[truncated: {gap:N0} characters omitted between the head and the tail of the log]…\n"
            + log[^CiLogStreamParser.FallbackTailChars..], result.FallbackSnippet);
        Assert.EndsWith("ACTUAL_SAFE_TAIL_MARKER=", result.FallbackSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The secret-env KEYWORD itself is cut by, or lies wholly beyond, the line cap. The retained prefix is
    /// only a run of key characters (it may end in "TOK", or not contain the keyword at all), yet the FULL
    /// line is a supported secret assignment, so its tail — which starts inside the value — must never be shown.
    /// <paramref name="keywordCut"/> is how many characters of TOKEN lie inside the prefix: 3 = "TOK" ends the
    /// prefix, 0 = the keyword begins exactly at the cut, 6 = the whole of "TOKEN=" is dropped (the prefix is
    /// pure padding), and so on across the boundary.
    /// </summary>
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 5)]
    [InlineData(false, -10)]
    [InlineData(true, -500)]
    public async Task FetchJobLogsAsync_SecretEnvKeywordCutOrBeyondLineCap_TailIsSuppressed(bool timestamped, int keywordCut)
    {
        var stamp = timestamped ? StampPrefix : string.Empty;
        var padding = new string('A', MaxLogLineChars - stamp.Length - keywordCut);
        var log = stamp + padding + "TOKEN=" + new string('S', 3_000) + "TAILSECRET";
        Assert.True(log.Length > MaxLogLineChars);
        // Positive control: the COMPLETE line is a supported secret assignment.
        Assert.EndsWith("=***REDACTED***", CiMonitorService.SanitizeLogContent(log), StringComparison.Ordinal);

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.DoesNotContain("TAILSECRET", result!.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 8), result.FallbackSnippet, StringComparison.Ordinal);
        Assert.True(result.FallbackSnippet!.Length < 4_200);
    }

    /// <summary>
    /// Completes the TOKEN= boundary sweep without duplicating coder rows: T|OKEN for timestamped
    /// input, TO|KEN in both forms, TOKE|N timestamped, and TOKEN|= untimestamped. The last row is
    /// a continuity control for a position the old prefix classifier already recognized. The
    /// between-'='-and-value position is already pinned by SecretEnvValueStartsAfterLineCap.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(true, 4)]
    [InlineData(false, 5)]
    public async Task FetchJobLogsAsync_SecretEnvKeywordCutBoundarySweep_MissingPositionsSuppressTail(bool timestamped, int keywordCut)
    {
        var start = (timestamped ? StampPrefix : string.Empty)
            + new string('A', MaxLogLineChars - (timestamped ? StampPrefix.Length : 0) - keywordCut);
        const string assignment = "TOKEN=";
        Assert.Equal(MaxLogLineChars, start.Length + keywordCut);
        var completeLine = start + assignment + new string('S', 3_000) + "TAILSECRET";
        Assert.EndsWith("=***REDACTED***", CiMonitorService.SanitizeLogContent(completeLine), StringComparison.Ordinal);
        var handler = new JobsAndLogsHandler(JobsJson(1, (111, "failure")), _ =>
            LogResponse(new SegmentedStream(false, (Utf8(start), 1), (Utf8(assignment), 1),
                (Utf8(new string('S', 1_000)), 3), (Utf8("TAILSECRET"), 1))));
        using var client = new HttpClient(handler, disposeHandler: false);

        var result = await CreateServiceForLogFetch().FetchJobLogsAsync(
            "run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.Empty(result!.Failures);
        Assert.DoesNotContain("TAILSECRET", result.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 8), result.FallbackSnippet, StringComparison.Ordinal);
        var omitted = completeLine.Length - CiLogStreamParser.FallbackHeadChars;
        Assert.Equal(completeLine[..CiLogStreamParser.FallbackHeadChars]
            + $"\n…[truncated: {omitted:N0} characters omitted between the head and the tail of the log]…\n",
            result.FallbackSnippet);
    }

    /// <summary>
    /// The new tracker handles all supported keywords, not only TOKEN. In particular PASSWOR|D
    /// requires the full seven-character seed carry; the bounded stream never holds a whole log.
    /// </summary>
    [Theory]
    [InlineData(false, "KEY", 2)]
    [InlineData(true, "KEY", 2)]
    [InlineData(false, "SECRET", 5)]
    [InlineData(true, "SECRET", 5)]
    [InlineData(false, "PASSWORD", 7)]
    [InlineData(true, "PASSWORD", 7)]
    public async Task FetchJobLogsAsync_SecretEnvOtherKeywordCrossesLineCap_SuppressesTail(bool timestamped, string keyword, int keywordCut)
    {
        var start = (timestamped ? StampPrefix : string.Empty)
            + new string('A', MaxLogLineChars - (timestamped ? StampPrefix.Length : 0) - keywordCut);
        var assignment = keyword + "=";
        Assert.Equal(MaxLogLineChars, start.Length + keywordCut);
        var completeLine = start + assignment + new string('S', 3_000) + "TAILSECRET";
        Assert.EndsWith("=***REDACTED***", CiMonitorService.SanitizeLogContent(completeLine), StringComparison.Ordinal);
        var handler = new JobsAndLogsHandler(JobsJson(1, (111, "failure")), _ =>
            LogResponse(new SegmentedStream(false, (Utf8(start), 1), (Utf8(assignment), 1),
                (Utf8(new string('S', 1_000)), 3), (Utf8("TAILSECRET"), 1))));
        using var client = new HttpClient(handler, disposeHandler: false);

        var result = await CreateServiceForLogFetch().FetchJobLogsAsync(
            "run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.Empty(result!.Failures);
        Assert.DoesNotContain("TAILSECRET", result.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 8), result.FallbackSnippet, StringComparison.Ordinal);
        var omitted = completeLine.Length - CiLogStreamParser.FallbackHeadChars;
        Assert.Equal(completeLine[..CiLogStreamParser.FallbackHeadChars]
            + $"\n…[truncated: {omitted:N0} characters omitted between the head and the tail of the log]…\n",
            result.FallbackSnippet);
    }

    /// <summary>The keyword split lands exactly across a stream-chunk boundary in the dropped middle.</summary>
    [Fact]
    public async Task FetchJobLogsAsync_SecretEnvKeywordSplitAcrossChunkInDroppedMiddle_TailIsSuppressed()
    {
        var log = new string('A', StreamChunkBytes - 3) + "TOKEN=" + new string('S', 3_000) + "TAILSECRET";
        Assert.Equal("TOK", log.Substring(StreamChunkBytes - 3, 3));
        Assert.True(StreamChunkBytes - 3 > MaxLogLineChars);
        Assert.EndsWith("=***REDACTED***", CiMonitorService.SanitizeLogContent(log), StringComparison.Ordinal);

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.DoesNotContain("TAILSECRET", result!.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 8), result.FallbackSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end for the reviewer's exact vectors (cut inside the keyword; keyword and '=' wholly beyond the
    /// cap), timestamped and not: the secret reaches neither a CREATED issue nor a DEDUP append.
    /// </summary>
    [Theory]
    [InlineData(false, 3, false)]
    [InlineData(false, 3, true)]
    [InlineData(false, 0, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, 3, false)]
    [InlineData(true, 3, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 0, true)]
    public async Task MonitorMergeAsync_SecretEnvKeywordCutByLineCap_NeverReachesIssueOrDedupAppend(
        bool timestamped, int keywordCut, bool dedup)
    {
        var stamp = timestamped ? StampPrefix : string.Empty;
        var log = stamp + new string('A', MaxLogLineChars - stamp.Length - keywordCut)
            + "TOKEN=" + new string('S', 3_000) + "TAILSECRET";
        var store = new FakeIssueStore();
        var clock = new ControlledTimeProvider();
        if (dedup)
            SeedExistingIssue(store, "CI failure: build", "Original safe description.");
        var handler = new CiFailureRoutingHandler(CheckRunsJsonWithDetailsUrl("123", summary: null),
            _ => JobsJsonOneFailedJob(111), _ => log);
        var service = CreateService(handler, issueStore: store, eventBus: new RecordingEventBus(), clock: clock);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(store.Issues.Values);
        if (dedup)
            Assert.Contains($"\n\n---\n{ExpectedStamp(clock)}\n", issue.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("TAILSECRET", issue.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 8), issue.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opposite direction (blanket suppression is NOT acceptable): oversized lines that are not secret
    /// assignments keep their actual tail — a key run that holds NO secret keyword, a keyword separated from
    /// the '=' by a space, and a keyword-free run followed by '='.
    /// </summary>
    [Theory]
    [InlineData("no-keyword", false)]
    [InlineData("no-keyword", true)]
    [InlineData("space-breaks-key", false)]
    [InlineData("space-breaks-key", true)]
    [InlineData("lowercase-keyword", false)]
    public async Task FetchJobLogsAsync_OversizedNonSecretLineStartingWithKeyRun_KeepsActualTail(string shape, bool timestamped)
    {
        var stamp = timestamped ? StampPrefix : string.Empty;
        var middle = shape switch
        {
            "no-keyword" => new string('A', MaxLogLineChars - stamp.Length) + "NAME=",
            "space-breaks-key" => new string('A', MaxLogLineChars - stamp.Length - 3) + "TOKEN =",
            _ => new string('A', MaxLogLineChars - stamp.Length - 3) + "token=",
        };
        var log = stamp + middle + new string('.', 3_000) + "ACTUAL_SAFE_TAIL_MARKER";
        Assert.Equal(log, CiMonitorService.SanitizeLogContent(log)); // not a secret assignment at all

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.EndsWith("ACTUAL_SAFE_TAIL_MARKER", result!.FallbackSnippet, StringComparison.Ordinal);
    }

    private const string DistinctiveSecret = "DISTINCTIVE_TAIL_SECRET_9XZ";
    private const string StampCore = "2026-08-22T09:47:38.1197374Z"; // a complete Actions timestamp, no separator

    /// <summary>
    /// A supported secret assignment whose Actions-timestamp separator ends <paramref name="separatorEndsPastCap"/>
    /// characters past the line cap (so the separator CROSSES the cap), followed by the key, '=' and a long value.
    /// </summary>
    private static string SeparatorCrossingAssignment(char separator, string keyword, int separatorEndsPastCap) =>
        StampCore + new string(separator, MaxLogLineChars - StampCore.Length + separatorEndsPastCap)
        + keyword + "=" + new string('s', 5_000) + DistinctiveSecret;

    /// <summary>
    /// A supported secret assignment whose TIMESTAMP crosses the cap: the 'Z' ends up <paramref name="zPastCap"/>
    /// characters past the cap boundary (negative = before it), because the fraction is that long.
    /// </summary>
    private static string TimestampCrossingAssignment(char separator, string keyword, int zPastCap)
    {
        const string DateHead = "2026-08-22T09:47:38.";
        var fractionDigits = MaxLogLineChars + zPastCap - DateHead.Length;
        return DateHead + new string('1', fractionDigits) + "Z" + separator + keyword + "=" + new string('s', 5_000) + DistinctiveSecret;
    }

    /// <summary>
    /// The shared security assertions: the input really IS a supported assignment (the complete-line sanitizer
    /// redacts it), the fallback shows no part of the secret value, and the gap is exact — the head window, one
    /// gap marker accounting for EVERY other decoded character, and no tail at all.
    /// </summary>
    private async Task AssertEnvAssignmentTailSuppressedAsync(string log)
    {
        Assert.True(log.Length > MaxLogLineChars);
        var sanitized = CiMonitorService.SanitizeLogContent(log);
        Assert.DoesNotContain(DistinctiveSecret, sanitized, StringComparison.Ordinal);
        Assert.EndsWith("=***REDACTED***", sanitized, StringComparison.Ordinal);

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        var snippet = result!.FallbackSnippet!;
        Assert.DoesNotContain(DistinctiveSecret, snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('s', 8), snippet, StringComparison.Ordinal);
        Assert.Equal(
            log[..CiLogStreamParser.FallbackHeadChars]
            + $"\n…[truncated: {log.Length - CiLogStreamParser.FallbackHeadChars:N0} characters omitted between the head and the tail of the log]…\n",
            snippet);
    }

    /// <summary>
    /// The reviewer's separator-crossing leak: the whitespace between the timestamp and the key straddles the
    /// cap. The sanitizer's separator is <c>[ \t]+</c>, so the line is a supported assignment however long the
    /// separator is; the tracker used to mistake the next dropped whitespace for the end of an empty key and
    /// stop tracking. Separator endings 0, 1, 3, 4096 and 8192 characters past the cap, spaces and tabs, all four
    /// keywords.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeparatorSweepRows))]
    public async Task FetchJobLogsAsync_SecretEnvSeparatorCrossesLineCap_TailIsSuppressed(char separator, string keyword, int separatorEndsPastCap) =>
        await AssertEnvAssignmentTailSuppressedAsync(SeparatorCrossingAssignment(separator, keyword, separatorEndsPastCap));

    public static IEnumerable<object[]> SeparatorSweepRows()
    {
        foreach (var separator in new[] { ' ', '\t' })
        foreach (var keyword in new[] { "TOKEN", "KEY", "SECRET", "PASSWORD" })
        foreach (var past in new[] { 0, 1, 3, 4_096, MaxLogLineChars })
            yield return [separator, keyword, past];
    }

    /// <summary>
    /// The reviewer's timestamp-crossing leak: a long fraction puts the 'Z' (and so the separator and key) at,
    /// just before, or beyond the cap, so the retained prefix holds only an INCOMPLETE timestamp. The tracker
    /// must follow the timestamp itself across the cut.
    /// </summary>
    [Theory]
    [MemberData(nameof(TimestampCrossingRows))]
    public async Task FetchJobLogsAsync_SecretEnvTimestampCrossesLineCap_TailIsSuppressed(char separator, string keyword, int zPastCap) =>
        await AssertEnvAssignmentTailSuppressedAsync(TimestampCrossingAssignment(separator, keyword, zPastCap));

    public static IEnumerable<object[]> TimestampCrossingRows()
    {
        foreach (var separator in new[] { ' ', '\t' })
        foreach (var keyword in new[] { "TOKEN", "PASSWORD" })
        foreach (var zPastCap in new[] { -1, 0, 1, 2, 500 })
            yield return [separator, keyword, zPastCap];
    }

    /// <summary>
    /// Timestamp CUT positions inside the fixed template itself, which a streaming reader sees split across
    /// stream chunks: the cap is shrunk to nothing by feeding the line in 1-character fragments, so every
    /// position of the timestamp, separator, key and '=' is a fragment boundary.
    /// </summary>
    [Fact]
    public void ParserFedOneCharacterAtATime_SecretEnvAssignmentTailIsStillSuppressed()
    {
        var log = SeparatorCrossingAssignment('\t', "SECRET", 3);
        var retention = CiFailureRetention.CreateProduction();
        var parser = CiLogStreamParser.CreateProduction(retention);

        foreach (var ch in log)
            parser.Feed(ch.ToString());
        parser.Complete();

        Assert.NotNull(parser.FallbackDiagnostics);
        Assert.DoesNotContain(DistinctiveSecret, parser.FallbackDiagnostics!, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('s', 8), parser.FallbackDiagnostics!, StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end for the reviewer's vectors: separator-crossing (all four keywords, space and tab) and
    /// timestamp-crossing, through BOTH a created issue and a dedup append.
    /// </summary>
    [Theory]
    [MemberData(nameof(IssuePathRows))]
    public async Task MonitorMergeAsync_SecretEnvSeparatorOrTimestampCrossesCap_NeverReachesIssueOrDedupAppend(
        string shape, char separator, string keyword, bool dedup)
    {
        var log = shape == "separator"
            ? SeparatorCrossingAssignment(separator, keyword, 1)
            : TimestampCrossingAssignment(separator, keyword, 0);
        Assert.EndsWith("=***REDACTED***", CiMonitorService.SanitizeLogContent(log), StringComparison.Ordinal);
        var store = new FakeIssueStore();
        var clock = new ControlledTimeProvider();
        if (dedup)
            SeedExistingIssue(store, "CI failure: build", "Original safe description.");
        var handler = new CiFailureRoutingHandler(CheckRunsJsonWithDetailsUrl("123", summary: null),
            _ => JobsJsonOneFailedJob(111), _ => log);
        var service = CreateService(handler, issueStore: store, eventBus: new RecordingEventBus(), clock: clock);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(store.Issues.Values);
        if (dedup)
            Assert.Contains($"\n\n---\n{ExpectedStamp(clock)}\n", issue.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(DistinctiveSecret, issue.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('s', 8), issue.Description, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> IssuePathRows()
    {
        foreach (var dedup in new[] { false, true })
        {
            foreach (var separator in new[] { ' ', '\t' })
            {
                foreach (var keyword in new[] { "TOKEN", "KEY", "SECRET", "PASSWORD" })
                    yield return ["separator", separator, keyword, dedup];
                yield return ["timestamp", separator, "TOKEN", dedup];
            }
        }
    }

    /// <summary>
    /// The sanitizer's fraction is OPTIONAL (<c>(\.\d+)?</c>): a whole-second timestamp (<c>...:38Z</c>) followed
    /// by a separator that crosses the cap is a supported assignment too, and must be tracked through the
    /// no-fraction path of the grammar.
    /// </summary>
    [Theory]
    [InlineData(' ', 0)]
    [InlineData('\t', 1)]
    [InlineData(' ', 4_096)]
    public async Task FetchJobLogsAsync_WholeSecondTimestampSeparatorCrossesLineCap_TailIsSuppressed(char separator, int separatorEndsPastCap)
    {
        const string stamp = "2026-08-22T09:47:38Z";
        var log = stamp + new string(separator, MaxLogLineChars - stamp.Length + separatorEndsPastCap)
            + "SECRET=" + new string('s', 5_000) + DistinctiveSecret;

        await AssertEnvAssignmentTailSuppressedAsync(log);
    }

    /// <summary>
    /// Whole-second timestamp with NO separator is not a supported assignment (the sanitizer requires
    /// <c>[ \t]+</c> after the 'Z'), so it must stay unsuppressed on the no-fraction path as well.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_WholeSecondTimestampWithoutSeparator_IsNotSuppressed()
    {
        var log = "2026-08-22T09:47:38Z" + new string('A', MaxLogLineChars) + "TOKEN=" + new string('s', 3_000) + "ACTUAL_SAFE_TAIL_MARKER";
        Assert.Equal(log, CiMonitorService.SanitizeLogContent(log));

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.EndsWith("ACTUAL_SAFE_TAIL_MARKER", result!.FallbackSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opposite direction: timestamp + a separator that crosses the cap + SAFE prose. The line never forms an
    /// assignment (the sanitizer leaves it untouched), so its actual tail must still be shown — pending state
    /// after whitespace must not suppress.
    /// </summary>
    [Theory]
    [InlineData(' ', "Build progress: TOKEN names a documentation label.")]
    [InlineData('\t', "Build progress: TOKEN names a documentation label.")]
    [InlineData(' ', "TOKEN = not an assignment because a space breaks the key")]
    [InlineData('\t', "lowercase_token=not an assignment")]
    [InlineData(' ', "NAME=a key run with no secret keyword")]
    public async Task FetchJobLogsAsync_TimestampAndLongSeparatorThenSafeProse_KeepsActualTail(char separator, string prose)
    {
        var log = StampCore + new string(separator, MaxLogLineChars - StampCore.Length + 7) + prose
            + new string('.', 3_000) + "ACTUAL_SAFE_TAIL_MARKER";
        Assert.Equal(log, CiMonitorService.SanitizeLogContent(log)); // not a supported assignment at all

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.EndsWith("ACTUAL_SAFE_TAIL_MARKER", result!.FallbackSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opposite direction, per what the sanitizer actually ACCEPTS: an UNTIMESTAMPED key preceded by whitespace
    /// is not a supported assignment (the pattern anchors the key, or a timestamp, at the line start), nor is a
    /// timestamp with no separator or with a non-space/tab separator. The sanitizer leaves each line untouched,
    /// so the suppression must not be broader than that: the tail is shown.
    /// </summary>
    [Theory]
    [InlineData("untimestamped-leading-space")]
    [InlineData("untimestamped-leading-tab")]
    [InlineData("timestamp-no-separator")]
    [InlineData("timestamp-nbsp-separator")]
    [InlineData("timestamp-trailing-dot-no-fraction")]
    public async Task FetchJobLogsAsync_UnsupportedWhitespaceOrTimestampShape_IsNotSuppressed(string shape)
    {
        var assignment = "TOKEN=" + new string('s', 3_000) + "ACTUAL_SAFE_TAIL_MARKER";
        var log = shape switch
        {
            "untimestamped-leading-space" => new string(' ', MaxLogLineChars + 10) + assignment,
            "untimestamped-leading-tab" => new string('\t', MaxLogLineChars + 10) + assignment,
            "timestamp-no-separator" => StampCore + new string('A', MaxLogLineChars) + assignment,
            "timestamp-nbsp-separator" => StampCore + new string('\u00a0', MaxLogLineChars) + assignment,
            _ => "2026-08-22T09:47:38." + new string(' ', MaxLogLineChars) + assignment,
        };
        Assert.Equal(log, CiMonitorService.SanitizeLogContent(log)); // the sanitizer does NOT accept this shape

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.EndsWith("ACTUAL_SAFE_TAIL_MARKER", result!.FallbackSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds only separator shapes missing from SeparatorSweepRows: literal short/mid lengths are
    /// continuity controls, as is a separator ending exactly at the cap. Separators extending
    /// PAST the cap are security rows. Every keyword is covered without duplicating pure runs.
    /// </summary>
    [Theory]
    [MemberData(nameof(AdditionalEnvSeparatorRows))]
    public async Task FetchJobLogsAsync_AdditionalEnvSeparatorBoundaries_SuppressOnlySupportedSecretTail(
        string shape, int separatorLength, string keyword, bool security)
    {
        var separator = EnvSeparatorText(shape, separatorLength);
        Assert.Equal(security, StampCore.Length + separator.Length > MaxLogLineChars);
        const string valueUnit = "ssssssssss";
        var value = string.Concat(Enumerable.Repeat(valueUnit, 2_000));
        var assignment = keyword + "=";
        var log = StampCore + separator + assignment + value + DistinctiveSecret;
        Assert.Equal(StampCore + separator + assignment + "***REDACTED***", CiMonitorService.SanitizeLogContent(log));

        var result = await FetchEnvSegmentsAsync(StampCore, separator, assignment,
            valueUnit, 2_000, DistinctiveSecret);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.Empty(result!.Failures);
        Assert.DoesNotContain(DistinctiveSecret, result.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('s', 8), result.FallbackSnippet, StringComparison.Ordinal);
        // If the value already starts before the cap, that retained prefix redacts to a short
        // known string. Otherwise the retained prefix is just timestamp/separator text.
        var prefix = StampCore.Length + separator.Length + assignment.Length < MaxLogLineChars
            ? StampCore + separator + assignment + "***REDACTED***"
            : log[..MaxLogLineChars];
        var head = prefix[..Math.Min(prefix.Length, CiLogStreamParser.FallbackHeadChars)];
        var excerptChars = (long)prefix.Length + log.Length - MaxLogLineChars;
        Assert.Equal(head + $"\n…[truncated: {excerptChars - head.Length:N0} characters omitted between the head and the tail of the log]…\n",
            result.FallbackSnippet);
    }

    public static IEnumerable<object[]> AdditionalEnvSeparatorRows()
    {
        foreach (var keyword in new[] { "TOKEN", "KEY", "SECRET", "PASSWORD" })
        {
            foreach (var shape in new[] { "space", "tab" })
            {
                foreach (var length in new[] { 1, 3, 4_096 })
                    yield return [shape, length, keyword, false];
                // The old sweep used an END offset of 8192, not this literal separator length.
                yield return [shape, MaxLogLineChars, keyword, true];
            }
            foreach (var length in new[] { 3, 4_096 })
                yield return ["mixed", length, keyword, false];
            foreach (var length in new[] { MaxLogLineChars - StampCore.Length,
                MaxLogLineChars - StampCore.Length + 1, MaxLogLineChars, 2 * MaxLogLineChars + 3 })
                yield return ["mixed", length, keyword, StampCore.Length + length > MaxLogLineChars];
        }
    }

    private static string EnvSeparatorText(string shape, int length) => shape switch
    {
        "space" => new string(' ', length),
        "tab" => new string('\t', length),
        "mixed" => new string(Enumerable.Range(0, length).Select(i => i % 2 == 0 ? ' ' : '\t').ToArray()),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown separator shape."),
    };

    /// <summary>Segments bound the fixture's buffers and force grammar state to survive real stream reads.</summary>
    private async Task<LogFetchResult?> FetchEnvSegmentsAsync(string stamp, string separator, string assignment,
        string valueUnit, long repeats, string tail)
    {
        var segments = new List<(byte[] Bytes, long Repeat)>();
        foreach (var text in new[] { stamp, separator, assignment })
        {
            if (text.Length > 0)
                segments.Add((Utf8(text), 1));
        }
        if (valueUnit.Length > 0 && repeats > 0)
            segments.Add((Utf8(valueUnit), repeats));
        if (tail.Length > 0)
            segments.Add((Utf8(tail), 1));
        var handler = new JobsAndLogsHandler(JobsJson(1, (111, "failure")), _ =>
            LogResponse(new SegmentedStream(false, segments.ToArray())));
        using var client = new HttpClient(handler, disposeHandler: false);
        return await CreateServiceForLogFetch().FetchJobLogsAsync(
            "run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);
    }

    /// <summary>The timestamp sweep previously covered TOKEN/PASSWORD only; fill KEY/SECRET at the same offsets.</summary>
    [Theory]
    [MemberData(nameof(AdditionalTimestampKeywordRows))]
    public async Task FetchJobLogsAsync_TimestampCrossesCap_KeyAndSecretTailIsSuppressed(char separator, string keyword, int zPastCap)
    {
        const string dateHead = "2026-08-22T09:47:38.";
        var fraction = new string('1', MaxLogLineChars + zPastCap - dateHead.Length);
        var log = dateHead + fraction + "Z" + separator + keyword + "=" + new string('s', 5_000) + DistinctiveSecret;
        Assert.EndsWith("=***REDACTED***", CiMonitorService.SanitizeLogContent(log), StringComparison.Ordinal);
        var handler = new JobsAndLogsHandler(JobsJson(1, (111, "failure")), _ =>
            LogResponse(new SegmentedStream(false, (Utf8(dateHead), 1), (Utf8("1"), fraction.Length),
                (Utf8("Z" + separator), 1), (Utf8(keyword + "="), 1), (Utf8("s"), 5_000), (Utf8(DistinctiveSecret), 1))));
        using var client = new HttpClient(handler, disposeHandler: false);

        var result = await CreateServiceForLogFetch().FetchJobLogsAsync(
            "run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.DoesNotContain(DistinctiveSecret, result!.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('s', 8), result.FallbackSnippet, StringComparison.Ordinal);
        Assert.Equal(log[..CiLogStreamParser.FallbackHeadChars]
            + $"\n…[truncated: {log.Length - CiLogStreamParser.FallbackHeadChars:N0} characters omitted between the head and the tail of the log]…\n",
            result.FallbackSnippet);
    }

    public static IEnumerable<object[]> AdditionalTimestampKeywordRows()
    {
        foreach (var separator in new[] { ' ', '\t' })
        foreach (var keyword in new[] { "KEY", "SECRET" })
        foreach (var offset in new[] { -1, 0, 1, 2, 500 })
            yield return [separator, keyword, offset];
    }

    /// <summary>Pending grammar state or an '=' alone must not suppress a supported key with an EMPTY value.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchJobLogsAsync_TimestampLongMixedSeparator_EmptyValueKeepsActualTail(bool wholeSeconds)
    {
        var stamp = wholeSeconds ? "2026-08-22T09:47:38Z" : StampCore;
        // Force a real dropped-middle gap: if the ring held the whole dropped suffix, complete-line
        // redaction would mask a broken suppression decision and make this control vacuous.
        var separator = EnvSeparatorText("mixed", 2 * MaxLogLineChars + 3);
        var log = stamp + separator + "TOKEN=";
        Assert.Equal(log, CiMonitorService.SanitizeLogContent(log)); // .+ requires at least one value character

        var result = await FetchEnvSegmentsAsync(stamp, separator, "TOKEN=", string.Empty, 0, string.Empty);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.Equal(log[..CiLogStreamParser.FallbackHeadChars]
            + $"\n…[truncated: {log.Length - CiLogStreamParser.FallbackHeadChars - CiLogStreamParser.FallbackTailChars:N0} characters omitted between the head and the tail of the log]…\n"
            + log[^CiLogStreamParser.FallbackTailChars..], result!.FallbackSnippet);
        Assert.EndsWith("TOKEN=", result.FallbackSnippet, StringComparison.Ordinal);
    }

    /// <summary>Mixed pending separators may lead to safe prose rather than an assignment.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchJobLogsAsync_TimestampLongMixedSeparator_SafeProseKeepsActualTail(bool wholeSeconds)
    {
        var stamp = wholeSeconds ? "2026-08-22T09:47:38Z" : StampCore;
        var separator = EnvSeparatorText("mixed", MaxLogLineChars + 3);
        const string prose = "Build progress: TOKEN refers to a documentation label, not an assignment. ";
        const string tail = "ACTUAL_SAFE_TAIL_MARKER";
        var log = stamp + separator + prose + new string('.', 3_000) + tail;
        Assert.Equal(log, CiMonitorService.SanitizeLogContent(log));

        var result = await FetchEnvSegmentsAsync(stamp, separator, prose, ".", 3_000, tail);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.EndsWith(tail, result!.FallbackSnippet, StringComparison.Ordinal);
        Assert.True(result.FallbackSnippet!.Length < 4_200);
    }

    /// <summary>The trigger itself, not just its value, straddles a stream-chunk boundary in the dropped middle.</summary>
    [Theory]
    [InlineData("Password=", 4)]
    [InlineData("Bearer ", 3)]
    public async Task FetchJobLogsAsync_OversizedSecretTriggerSplitInDroppedMiddle_SuppressesTail(string trigger, int split)
    {
        var log = new string('.', StreamChunkBytes - split) + trigger + new string('p', 3_000) + "TAILSECRET";
        Assert.Equal(trigger[..split], log.Substring(StreamChunkBytes - split, split));
        Assert.True(StreamChunkBytes - split > MaxLogLineChars);

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        var expectedGap = log.Length - CiLogStreamParser.FallbackHeadChars;
        Assert.Equal(new string('.', CiLogStreamParser.FallbackHeadChars)
            + $"\n…[truncated: {expectedGap:N0} characters omitted between the head and the tail of the log]…\n",
            result!.FallbackSnippet);
        Assert.DoesNotContain("TAILSECRET", result.FallbackSnippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchJobLogsAsync_OversizedSecretThenOversizedSafeLine_DoesNotCarrySuppression()
    {
        var log = "Bearer " + new string('p', 20_000) + "TAILSECRET\n"
            + new string('.', 12_000) + "ACTUAL_TAIL_MARKER";

        var result = await FetchSingleLogAsync(log);

        Assert.NotNull(result?.FallbackSnippet);
        Assert.DoesNotContain("TAILSECRET", result!.FallbackSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('p', 8), result.FallbackSnippet, StringComparison.Ordinal);
        Assert.EndsWith("ACTUAL_TAIL_MARKER", result.FallbackSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rollback must restore character charges exactly, not leave the broken job's usage in place
    /// or zero the successful prefix's usage. The count cap is not binding in either row.
    /// </summary>
    [Theory]
    [InlineData(10, 20, 20)]
    [InlineData(25, 2, 20)]
    public async Task FetchJobLogsAsync_FailedDownload_RestoresCharacterBudgetExactly(int beforeCount, int brokenCount, int afterCount)
    {
        const int errorLength = 6_000;
        var error = new string('e', errorLength);
        string Name(string job, int i) => $"Tests.{job}.Case{i:D4}";
        string JobLog(string job, int count) => string.Concat(Enumerable.Range(0, count)
            .Select(i => FailureBlockText(Name(job, i), error)));
        var successful = Enumerable.Range(0, beforeCount).Select(i => Name("BeforeBudget", i))
            .Concat(Enumerable.Range(0, afterCount).Select(i => Name("AfterBudget", i))).ToList();
        var expected = new List<(string TestName, string Error, string StackTrace)>();
        long used = 0;
        foreach (var name in successful)
        {
            var frame = $"at {name}()";
            var cost = name.Length + error.Length + frame.Length;
            if (used + cost > MaxRetainedFailureChars)
                break;
            expected.Add((name, error, frame));
            used += cost;
        }
        Assert.True(successful.Count < MaxRetainedFailureCount);
        Assert.True(expected.Count >= beforeCount && expected.Count < MaxRetainedFailureCount);

        var handler = new JobsAndLogsHandler(JobsJson(3, (111, "failure"), (222, "failure"), (333, "failure")), uri =>
            uri.AbsolutePath.Contains("/222/", StringComparison.Ordinal)
                ? LogResponse(new SegmentedStream(true, (Utf8(JobLog("BrokenBudget", brokenCount)), 1)))
                : LogResponse(uri.AbsolutePath.Contains("/111/", StringComparison.Ordinal)
                    ? JobLog("BeforeBudget", beforeCount) : JobLog("AfterBudget", afterCount)));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Failures);
        Assert.DoesNotContain(result.Failures, f => f.TestName.Contains("BrokenBudget", StringComparison.Ordinal));
        if (expected.Count == successful.Count)
            Assert.Null(result.Overflow);
        else
            Assert.Contains($"Omitted failures: {successful.Count - expected.Count} of {successful.Count} parsed test failure occurrence(s)", result.Overflow, StringComparison.Ordinal);
        var requests = handler.Captured.Count;
        var again = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);
        Assert.Same(result, again);
        Assert.Equal(requests, handler.Captured.Count);
    }

    /// <summary>A previously exhausted successful budget remains closed after a later job fails.</summary>
    [Fact]
    public async Task FetchJobLogsAsync_FailedDownload_PreservesAlreadyExhaustedCharacterBudget()
    {
        var good = string.Concat(Enumerable.Range(0, 26).Select(i =>
            FailureBlockText($"Tests.InitialBudget.Case{i:D3}", new string('e', 8_000))));
        var broken = FailureBlockText("Tests.BrokenBudget.Bad", "broken")
            + "Failed Tests.BrokenBudget.Final [1 ms]\n";
        var tail = FailureBlockText("Tests.Tiny.End", "x");
        var handler = new JobsAndLogsHandler(JobsJson(3, (111, "failure"), (222, "failure"), (333, "failure")), uri =>
            uri.AbsolutePath.Contains("/222/", StringComparison.Ordinal)
                ? LogResponse(new SegmentedStream(true, (Utf8(broken), 1)))
                : LogResponse(uri.AbsolutePath.Contains("/111/", StringComparison.Ordinal) ? good : tail));
        using var client = new HttpClient(handler, disposeHandler: false);

        var result = await CreateServiceForLogFetch().FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(25, result.Failures.Count);
        Assert.Equal(Enumerable.Range(0, 25).Select(i => $"Tests.InitialBudget.Case{i:D3}"), result.Failures.Select(f => f.TestName));
        Assert.Contains("Omitted failures: 2 of 27 parsed test failure occurrence(s)", result.Overflow, StringComparison.Ordinal);
        var retainedCost = result.Failures.Sum(f => f.TestName.Length + f.Error.Length + f.StackTrace.Length);
        var tinyCost = "Tests.Tiny.End".Length + "x".Length + "at Tests.Tiny.End()".Length;
        Assert.True(retainedCost + tinyCost < MaxRetainedFailureChars,
            "The tiny failure would fit in the remaining characters, but retention is a strict prefix already closed by rejection.");
    }

    // ── Streaming contract: bounds, chunk boundaries, and redaction ─────────

    /// <summary>
    /// A secret-bearing log line: the text that precedes the secret on the line, the raw secret,
    /// the redacted form the parser must produce, and the fragments that must never survive into
    /// ANY output (retained failures, fallback excerpt, or overflow summary).
    /// </summary>
    /// <param name="Line">The text that precedes the secret on the same line.</param>
    /// <param name="Secret">The raw secret.</param>
    /// <param name="SplitIntoSecret">
    /// How many characters of the secret are before the boundary that cuts it.
    /// </param>
    /// <param name="ExpectedRedacted">The redacted form that must be present in the output.</param>
    /// <param name="Forbidden">Fragments that must never appear in the output.</param>
    private sealed record SecretVector(
        string Line,
        string Secret,
        int SplitIntoSecret,
        string ExpectedRedacted,
        string[] Forbidden);

    /// <summary>
    /// Builds the secret-bearing line of a category so that the CHUNK boundary (the 16 KiB read
    /// boundary) falls a fixed number of characters INSIDE the secret.
    /// </summary>
    /// <remarks>
    /// The line prefix is the text that precedes the secret on the same line; for the environment
    /// variable category it is empty because the pattern only matches a key that starts the line.
    /// </remarks>
    private static SecretVector ChunkSplitSecretVector(string category) => category switch
    {
        "ghp" => new SecretVector(
            "    ", "ghp_" + new string('a', 36), 20,
            "***REDACTED***", ["ghp_", new string('a', 36)]),
        "gho" => new SecretVector(
            "    ", "gho_" + new string('b', 36), 20,
            "***REDACTED***", ["gho_", new string('b', 36)]),
        "ghs" => new SecretVector(
            "    ", "ghs_" + new string('c', 36), 20,
            "***REDACTED***", ["ghs_", new string('c', 36)]),
        "bearer" => new SecretVector(
            "    Authorization: ", "Bearer Ab3CdEf.9-x_y", 12,
            "Bearer ***REDACTED***", ["Ab3CdEf.9-x_y", "Bearer Ab3"]),
        "envvar" => new SecretVector(
            "", "MY_API_TOKEN=SuperSecretValue123", 20,
            "MY_API_TOKEN=***REDACTED***", ["SuperSecretValue123", "SuperSecret"]),
        "password" => new SecretVector(
            "    ", "Password=s3cr3tValueAndMore", 12,
            "Password=***REDACTED***", ["s3cr3tValue", "s3cr3t"]),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown secret category."),
    };

    /// <summary>
    /// Builds the secret-bearing line of a category so that the per-LINE cap cuts it: the line is
    /// longer than <see cref="MaxLogLineChars"/> and the secret straddles the cap boundary.
    /// </summary>
    private static SecretVector TruncationSecretVector(string category) => category switch
    {
        // The trigger survives in the retained prefix but the token does not: the unredactable-tail
        // trim replaces everything from the trigger to the end of the retained line.
        "ghp" => new SecretVector(
            new string('x', MaxLogLineChars - 10), "ghp_" + new string('a', 36), 0,
            "***REDACTED***", ["ghp_", new string('a', 36)]),
        "gho" => new SecretVector(
            new string('x', MaxLogLineChars - 10), "gho_" + new string('b', 36), 0,
            "***REDACTED***", ["gho_", new string('b', 36)]),
        "ghs" => new SecretVector(
            new string('x', MaxLogLineChars - 10), "ghs_" + new string('c', 36), 0,
            "***REDACTED***", ["ghs_", new string('c', 36)]),
        // The value is cut INSIDE the value: the retained prefix ends with "Bearer Ab3", so the
        // value-bounded pattern matches the retained fragment and redacts it.
        "bearer" => new SecretVector(
            new string('x', MaxLogLineChars - 24), "Authorization: Bearer Ab3CdEf.9-x_y", 0,
            "Bearer ***REDACTED***", ["Ab3CdEf.9-x_y", "Bearer Ab3"]),
        // The key must start the line for the environment-variable pattern to match.
        "envvar" => new SecretVector(
            "", "MY_API_TOKEN=SuperSecretValue" + new string('s', 9_000), 0,
            "MY_API_TOKEN=***REDACTED***", ["SuperSecretValue"]),
        "password" => new SecretVector(
            new string('x', MaxLogLineChars - 20), "Password=s3cr3tValueAndMore", 0,
            "Password=***REDACTED***", ["s3cr3tValue", "s3cr3t"]),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown secret category."),
    };

    /// <summary>
    /// Asserts that neither the secret nor any of its fragments survives in a piece of output.
    /// </summary>
    private static void AssertNoSecretFragment(string output, SecretVector vector)
    {
        Assert.DoesNotContain(vector.Secret, output, StringComparison.Ordinal);
        foreach (var forbidden in vector.Forbidden)
            Assert.DoesNotContain(forbidden, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asserts that neither the secret nor any of its fragments survives in a piece of output, and
    /// that the redacted form is present instead.
    /// </summary>
    private static void AssertSecretRedacted(string output, SecretVector vector)
    {
        AssertNoSecretFragment(output, vector);
        Assert.Contains(vector.ExpectedRedacted, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A secret split across a stream-chunk boundary must not leak: the parser carries the partial
    /// line across the boundary and redacts the COMPLETE line, so neither the secret nor any
    /// fragment of it reaches the retained failure or the fallback excerpt.
    /// </summary>
    /// <remarks>
    /// Every supported secret category is covered: the three fixed-length GitHub token prefixes,
    /// the HTTP <c>Bearer</c> header, a secret environment variable, and a <c>Password=</c>
    /// connection-string fragment.
    /// </remarks>
    [Theory]
    [InlineData("ghp")]
    [InlineData("gho")]
    [InlineData("ghs")]
    [InlineData("bearer")]
    [InlineData("envvar")]
    [InlineData("password")]
    public async Task FetchJobLogsAsync_SecretSplitAcrossChunkBoundary_IsRedacted(string category)
    {
        var vector = ChunkSplitSecretVector(category);
        const string header = "Failed Tests.Secret.Case [1 ms]\n  Error Message:\n";

        // ── Variant 1: the secret sits inside a parsed failure's error text ──
        // The chunk boundary must fall SplitIntoSecret characters inside the secret.
        var blockStart = StreamChunkBytes - header.Length - vector.Line.Length - vector.SplitIntoSecret;
        Assert.True(blockStart > 0, "The failure block must start inside the first chunk.");

        var blockLog = new StringBuilder();
        AppendFillerTo(blockLog, blockStart);
        blockLog.Append(header).Append(vector.Line).Append(vector.Secret)
            .Append("\n  Stack Trace:\n    at Secret.Case()\n\n");
        var blockText = blockLog.ToString();

        // The boundary really falls inside the secret (sanity check on the geometry).
        var secretStart = blockStart + header.Length + vector.Line.Length;
        Assert.True(secretStart < StreamChunkBytes && StreamChunkBytes < secretStart + vector.Secret.Length,
            "The chunk boundary must fall inside the secret.");

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(blockText));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var blockResult = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(blockResult);
        var failure = Assert.Single(blockResult!.Failures);
        AssertSecretRedacted(failure.Error, vector);
        AssertNoSecretFragment(failure.StackTrace, vector);
        AssertNoSecretFragment(failure.TestName, vector);

        // ── Variant 2: the same secret in plain log text (no failure) → fallback excerpt ──
        // The secret must land inside the retained TAIL window, otherwise the excerpt would drop
        // it and the assertion would be vacuous.
        var plainTotal = CiLogStreamParser.FallbackHeadChars + CiLogStreamParser.FallbackTailChars + 1_000;
        var plainStart = plainTotal - vector.Line.Length - vector.Secret.Length - "end of log\n".Length;
        Assert.True(plainStart > 0, "The secret line must start inside the log.");
        var plainLog = new StringBuilder();
        AppendFillerTo(plainLog, plainStart);
        plainLog.Append(vector.Line).Append(vector.Secret).Append("\nend of log\n");
        var plainText = plainLog.ToString();

        // The secret really is inside the retained tail window.
        var secretOffset = plainStart + vector.Line.Length;
        Assert.True(secretOffset >= plainTotal - CiLogStreamParser.FallbackTailChars,
            "The secret must lie inside the retained tail window of the fallback excerpt.");

        var plainHandler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(plainText));
        using var plainClient = new HttpClient(plainHandler, disposeHandler: false);
        var plainService = CreateServiceForLogFetch();

        var plainResult = await plainService.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", plainClient, TestContext.Current.CancellationToken);

        Assert.NotNull(plainResult);
        Assert.Empty(plainResult!.Failures);
        Assert.NotNull(plainResult.FallbackSnippet);
        AssertSecretRedacted(plainResult.FallbackSnippet!, vector);
    }

    /// <summary>
    /// A secret cut by the per-line cap must not leak: the retained prefix is redacted first and
    /// truncated afterwards, so neither the secret nor any fragment of it reaches the retained
    /// failure or the fallback excerpt.
    /// </summary>
    /// <remarks>
    /// Every supported secret category is covered: the three fixed-length GitHub token prefixes
    /// (whose unredactable tail must be trimmed), the HTTP <c>Bearer</c> header, a secret
    /// environment variable, and a <c>Password=</c> connection-string fragment (whose values are
    /// bounded by the end of the line, so a cut value is still fully redacted).
    /// </remarks>
    [Theory]
    [InlineData("ghp")]
    [InlineData("gho")]
    [InlineData("ghs")]
    [InlineData("bearer")]
    [InlineData("envvar")]
    [InlineData("password")]
    public async Task FetchJobLogsAsync_SecretCutByLineCap_EveryCategoryIsRedacted(string category)
    {
        var vector = TruncationSecretVector(category);
        var line = vector.Line + vector.Secret;

        Assert.True(line.Length > MaxLogLineChars, "The line must exceed the per-line cap.");

        // ── Variant 1: the secret sits inside a parsed failure's error text ──
        const string header = "Failed Tests.Secret.Case [1 ms]\n  Error Message:\n";
        var blockLog = new StringBuilder();
        AppendFillerTo(blockLog, 101);
        blockLog.Append(header).Append(line).Append("\n  Stack Trace:\n    at Secret.Case()\n\n");
        var blockText = blockLog.ToString();

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(blockText));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var blockResult = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(blockResult);
        var failure = Assert.Single(blockResult!.Failures);
        AssertSecretRedacted(failure.Error, vector);
        AssertNoSecretFragment(failure.StackTrace, vector);

        // ── Variant 2: the same secret in plain log text (no failure) → fallback excerpt ──
        var plainText = line + "\nend of log\n";
        var plainHandler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(plainText));
        using var plainClient = new HttpClient(plainHandler, disposeHandler: false);
        var plainService = CreateServiceForLogFetch();

        var plainResult = await plainService.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", plainClient, TestContext.Current.CancellationToken);

        Assert.NotNull(plainResult);
        Assert.Empty(plainResult!.Failures);
        Assert.NotNull(plainResult.FallbackSnippet);
        AssertSecretRedacted(plainResult.FallbackSnippet!, vector);
    }

    /// <summary>
    /// Failure blocks that straddle stream-chunk boundaries are still parsed as ONE failure each:
    /// the VSTest header and the MTP header are themselves split by the boundary.
    /// </summary>
    /// <remarks>
    /// Removal-proof: without incremental parsing across the boundary, the split header line would
    /// never match and the corresponding failure would be lost entirely.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_LargeLog_VstestAndMtpBlocksSplitAtChunkBoundaries_AreParsed()
    {
        const string vstestBlock =
            "Failed Tests.Chunk.Vstest [1 ms]\n"
            + "  Error Message:\n"
            + "    vstest chunk error\n"
            + "  Stack Trace:\n"
            + "    at Chunk.Vstest()\n"
            + "\n";

        const string mtpBlock =
            "failed Tests.Chunk.Mtp (12ms)\n"
            + "  from /repo/bin/Release/net10.0/Tests.dll (net10.0|x64)\n"
            + "  mtp chunk error\n"
            + "    at Chunk.Mtp()\n";

        // The first block's HEADER line is split by the first chunk boundary (12 characters in), and
        // the second block's header by the second boundary (20 characters in).
        var (log, starts) = BuildLogWithBlocksSplitAtChunkBoundaries(
        [
            (vstestBlock, 12),
            (mtpBlock, 20),
        ]);

        // The geometry is what makes this discriminating: each block's header really is cut by a
        // chunk boundary — the last character of chunk N and the first character of chunk N+1 fall
        // inside the header line, so a reader that never joined the two halves could never match
        // the header.
        var vstestBoundary = StreamChunkBytes;
        Assert.Equal(starts[0] + 12, vstestBoundary);
        Assert.Equal(vstestBlock[11], log[vstestBoundary - 1]);
        Assert.Equal(vstestBlock[12], log[vstestBoundary]);
        var mtpBoundary = StreamChunkBytes * 2;
        Assert.Equal(starts[1] + 20, mtpBoundary);
        Assert.Equal(mtpBlock[19], log[mtpBoundary - 1]);
        Assert.Equal(mtpBlock[20], log[mtpBoundary]);

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Failures.Count);

        Assert.Equal("Tests.Chunk.Vstest", result.Failures[0].TestName);
        Assert.Equal("vstest chunk error", result.Failures[0].Error);
        Assert.Equal("at Chunk.Vstest()", result.Failures[0].StackTrace);

        Assert.Equal("Tests.Chunk.Mtp", result.Failures[1].TestName);
        Assert.Equal("mtp chunk error", result.Failures[1].Error);
        Assert.Equal("at Chunk.Mtp()", result.Failures[1].StackTrace);

        Assert.Null(result.Overflow);
    }

    /// <summary>
    /// A single huge line is capped with an explicit truncation marker that counts DECODED
    /// characters — not bytes.
    /// </summary>
    /// <remarks>
    /// The error line is made of multi-byte characters (U+20AC, three UTF-8 bytes each), so a
    /// byte-based count would report a completely different number than the decoded-character count
    /// the contract specifies.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_HugeSingleLine_CappedWithDecodedCharacterCount()
    {
        const string euro = "€";
        const string indent = "    ";
        var errorLine = indent + string.Concat(Enumerable.Repeat(euro, 9_000));
        var log = "Failed Tests.Huge.Line [1 ms]\n  Error Message:\n"
            + errorLine
            + "\n  Stack Trace:\n    at Huge.Line()\n\n";

        // The line is 9,004 DECODED characters but 27,004 UTF-8 bytes — the two counts differ.
        Assert.Equal(9_004, errorLine.Length);
        Assert.True(Encoding.UTF8.GetByteCount(errorLine) > 27_000);

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var failure = Assert.Single(result!.Failures);
        Assert.Equal("Tests.Huge.Line", failure.TestName);

        // The retained prefix is exactly the line cap in decoded characters, and the marker reports
        // the DECODED characters that were dropped (9,004 - 8,192 = 812), not the bytes.
        var expected = new string(euro[0], MaxLogLineChars - indent.Length)
            + " …[truncated: 812 characters omitted]…";
        Assert.Equal(expected, failure.Error);
        Assert.DoesNotContain("truncated: 18,812 characters omitted", failure.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure block whose body grows past the per-block cap is truncated with an explicit marker
    /// counting the omitted characters, and its retained fields stay inside the cap.
    /// </summary>
    /// <remarks>
    /// The expected omitted count follows the documented accounting rule: a block line costs its
    /// character count plus one for the line separator.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_FailureBlockBeyondBlockCap_IsTruncatedWithOmittedCount()
    {
        const string testName = "Tests.Big.Block";
        var frames = new List<string>();
        for (var i = 0; i < 1_000; i++)
            frames.Add($"    at Big.Block.Frame{i:D4}()");

        // The block runs to the end of the log (no blank line after the stack trace) so the
        // truncation of the block body is reported on the extracted stack trace.
        var log = $"Failed {testName} [1 ms]\n  Error Message:\n    big error\n  Stack Trace:\n"
            + string.Join('\n', frames) + "\n";

        // The retained/omitted split, following the documented per-line accounting.
        var blockLines = new List<string> { "  Error Message:", "    big error", "  Stack Trace:" };
        blockLines.AddRange(frames);
        var used = 0;
        var omitted = 0;
        foreach (var line in blockLines)
        {
            var cost = line.Length + 1;
            if (used + cost > MaxFailureBlockChars)
            {
                omitted += cost;
                continue;
            }

            used += cost;
        }

        Assert.True(omitted > 0, "The block must exceed the per-block cap.");

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var failure = Assert.Single(result!.Failures);
        Assert.Equal(testName, failure.TestName);
        Assert.Equal("big error", failure.Error);

        var marker = $" …[truncated: {omitted:N0} characters omitted]…";
        Assert.EndsWith(marker, failure.StackTrace, StringComparison.Ordinal);
        var retained = failure.StackTrace[..^marker.Length];
        Assert.True(retained.Length <= MaxFailureBlockChars,
            $"The retained stack trace is {retained.Length} characters, past the {MaxFailureBlockChars}-character cap.");

        // The dropped frames are really gone, and the retained ones are still there.
        Assert.Contains("Frame0000", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("Frame0999", failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// The aggregate retained-character cap bounds the SUM of the retained failure fields, and the
    /// overflow summary reports the omitted occurrences.
    /// </summary>
    /// <remarks>
    /// The failure count is deliberately BELOW the retained-count cap, so only the aggregate
    /// character cap can be the binding constraint.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_RetainedBytesCap_BoundsAggregateAndSummarisesOccurrences()
    {
        const int failureCount = 100;
        const int errorChars = 3_000;
        var failureLog = string.Concat(Enumerable.Range(0, failureCount).Select(i =>
            $"Failed Tests.Agg.Case{i:D3} [1 ms]\n  Error Message:\n    {new string('e', errorChars)}\n  Stack Trace:\n    at Agg.Case{i:D3}()\n\n"));

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(failureLog));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(result!.Overflow);

        // The aggregate of the retained fields stays inside the cap...
        var retainedChars = result.Failures.Sum(f => f.TestName.Length + f.Error.Length + f.StackTrace.Length);
        Assert.True(retainedChars <= MaxRetainedFailureChars,
            $"The retained fields total {retainedChars} characters, past the {MaxRetainedFailureChars}-character cap.");

        // ...and the COUNT cap did not bind (fewer failures are retained than it allows).
        Assert.True(result.Failures.Count > 0);
        Assert.True(result.Failures.Count < MaxRetainedFailureCount,
            $"The count cap bound instead of the aggregate character cap ({result.Failures.Count} retained).");

        // The summary reports the omitted occurrences.
        Assert.Contains($"{failureCount - result.Failures.Count} of {failureCount}", result.Overflow, StringComparison.Ordinal);
        Assert.Contains("occurrence(s) were not retained", result.Overflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The retention bounds apply to the AGGREGATE of every failed job's log, not per job: a run
    /// with several failing jobs cannot multiply them.
    /// </summary>
    /// <remarks>
    /// Each job's log alone fits inside the count cap, but their SUM does not — so the aggregate
    /// bound is the only thing that can bind here.
    /// </remarks>
    [Fact]
    public async Task FetchJobLogsAsync_MultipleJobs_AggregateOverflowCountsAllJobsOccurrences()
    {
        const int jobCount = 4;
        const int failuresPerJob = 60; // 4 x 60 = 240 > the 200 retained-failure cap

        var jobsBody = JobsJson(jobCount, (111, "failure"), (222, "failure"), (333, "failure"), (444, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, uri =>
        {
            // Every job's log carries the same number of failures, but distinguishable names.
            var jobId = uri.AbsolutePath.Contains("/111/", StringComparison.Ordinal) ? 1
                : uri.AbsolutePath.Contains("/222/", StringComparison.Ordinal) ? 2
                : uri.AbsolutePath.Contains("/333/", StringComparison.Ordinal) ? 3
                : 4;
            var log = string.Concat(Enumerable.Range(0, failuresPerJob).Select(i =>
                $"Failed Tests.Job{jobId}.Case{i:D3} [1 ms]\n  Error Message:\n    boom {i}\n  Stack Trace:\n    at Job{jobId}.Case{i:D3}()\n\n"));
            return LogResponse(log);
        });
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(result!.Overflow);

        var totalParsed = jobCount * failuresPerJob;
        Assert.Equal(MaxRetainedFailureCount, result.Failures.Count);
        Assert.Contains($"{totalParsed - MaxRetainedFailureCount} of {totalParsed}", result.Overflow, StringComparison.Ordinal);
        Assert.Contains("occurrence(s) were not retained", result.Overflow, StringComparison.Ordinal);

        // The retained set spans the JOBS: the aggregate bound did not stop at the first job.
        Assert.Contains(result.Failures, f => f.TestName.StartsWith("Tests.Job1.", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.TestName.StartsWith("Tests.Job2.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The overflow summary counts parsed failure OCCURRENCES, not distinct tests: a log that
    /// repeats the SAME failing test is still counted once per occurrence.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_OverflowSummaryCountsOccurrencesNotDistinctTests()
    {
        const int extra = 5;
        var retainedCap = MaxRetainedFailureCount;
        var failureCount = retainedCap + extra;
        var oneFailure = "Failed Tests.Same.Case [1 ms]\n  Error Message:\n    boom\n  Stack Trace:\n    at Same.Case()\n\n";
        var failureLog = string.Concat(Enumerable.Repeat(oneFailure, failureCount));

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(failureLog));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(MaxRetainedFailureCount, result!.Failures.Count);
        Assert.NotNull(result.Overflow);

        // One DISTINCT test, but 205 parsed occurrences: the summary must count occurrences.
        Assert.Contains($"{extra} of {failureCount}", result.Overflow, StringComparison.Ordinal);
        Assert.DoesNotContain($"of 1 ", result.Overflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty or blank job log yields an empty result with no fallback excerpt and no overflow
    /// summary — the log-derived path must never invent content.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public async Task FetchJobLogsAsync_EmptyOrBlankLog_ReturnsEmptyResultWithoutSnippet(string body)
    {
        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(body));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result!.Failures);
        Assert.Null(result.FallbackSnippet);
        Assert.Null(result.Overflow);
    }

    /// <summary>
    /// A log that ends in the MIDDLE of a line still delivers that line: the parser flushes the
    /// final newline-less line when the stream ends.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_LogEndingMidLine_FlushesTrailingLine()
    {
        // The last line has no terminating newline and is cut mid-text.
        var log = "Failed Tests.Tail.Case [1 ms]\n  Error Message:\n    tail err";

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var failure = Assert.Single(result!.Failures);
        Assert.Equal("Tests.Tail.Case", failure.TestName);
        Assert.Equal("tail err", failure.Error);
    }

    /// <summary>
    /// A log that ends INSIDE an open failure block still yields the failure parsed so far.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_LogEndingInsideFailureBlock_RetainsPartialFailure()
    {
        var log = "Failed Tests.Partial.Case [1 ms]\n  Error Message:\n    partial error\n  Stack Trace:\n    at Partial.Case()";

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var failure = Assert.Single(result!.Failures);
        Assert.Equal("Tests.Partial.Case", failure.TestName);
        Assert.Equal("partial error", failure.Error);
        Assert.Equal("at Partial.Case()", failure.StackTrace);
    }

    /// <summary>
    /// A cancellation that lands in the MIDDLE of the streaming read propagates to the caller — the
    /// log fetch must not swallow it and report a partial result.
    /// </summary>
    [Fact]
    public async Task FetchJobLogsAsync_CancellationDuringStreamRead_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var jobsBody = JobsJson(1, (111, "failure"));
        var stream = new CancellingStream(
            cts,
            Encoding.UTF8.GetBytes("Failed Tests.A [1 ms]\n  Error Message:\n    x\n"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(stream));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, cts.Token));
    }

    // ── HandleCiFailureAsync integration (via MonitorMergeAsync / StartupScanAsync) ──

    /// <summary>
    /// A routing handler that simulates the full GitHub API surface for CI failure issue
    /// creation. It serves three endpoints by URL path:
    /// <list type="bullet">
    /// <item>The check-runs probe endpoint (<c>/commits/{sha}/check-runs</c>).</item>
    /// <item>The jobs endpoint (<c>/actions/runs/{runId}/jobs</c>).</item>
    /// <item>The job-log endpoint (<c>/actions/jobs/{jobId}/logs</c>), which returns a 302 to a
    /// cross-host URL where the log body is served with no auth required.</item>
    /// </list>
    /// The check-runs body, jobs body, and log body are all configurable.
    /// </summary>
    private sealed class CiFailureRoutingHandler : HttpMessageHandler
    {
        private readonly string _checkRunsBody;
        private readonly Func<long, string?> _jobsBodyForRun;
        private readonly Func<long, string?> _logBodyForJob;
        private readonly List<CapturedRequest> _captured = [];
        private readonly List<string> _jobsEndpointHits = [];
        private readonly List<long> _jobLogHits = [];
        private readonly object _lock = new();

        public IReadOnlyList<CapturedRequest> Captured
        {
            get { lock (_lock) return _captured.ToList(); }
        }

        /// <summary>Every URL that hit the jobs endpoint (<c>/actions/runs/.../jobs</c>), in order.</summary>
        public IReadOnlyList<string> JobsEndpointHits
        {
            get { lock (_lock) return _jobsEndpointHits.ToList(); }
        }

        /// <summary>Every jobId whose log endpoint (<c>/actions/jobs/.../logs</c>) was hit, in order.</summary>
        public IReadOnlyList<long> JobLogHits
        {
            get { lock (_lock) return _jobLogHits.ToList(); }
        }

        /// <param name="checkRunsBody">The JSON body for the check-runs probe response.</param>
        /// <param name="jobsBodyForRun">Returns the jobs JSON body for a given runId (as long), or null to 404.</param>
        /// <param name="logBodyForJob">Returns the log body for a given jobId (as long), or null to skip (302→404).</param>
        public CiFailureRoutingHandler(
            string checkRunsBody,
            Func<long, string?> jobsBodyForRun,
            Func<long, string?> logBodyForJob)
        {
            _checkRunsBody = checkRunsBody;
            _jobsBodyForRun = jobsBodyForRun;
            _logBodyForJob = logBodyForJob;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (_lock)
            {
                _captured.Add(new CapturedRequest(
                    uri.ToString(),
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter));
            }

            var path = uri.AbsolutePath;

            // Check-runs probe: /repos/{owner}/{repo}/commits/{sha}/check-runs
            if (path.Contains("/commits/", StringComparison.Ordinal)
                && path.EndsWith("/check-runs", StringComparison.Ordinal))
            {
                return Task.FromResult(OkResponse(_checkRunsBody));
            }

            // Jobs endpoint: /repos/{owner}/{repo}/actions/runs/{runId}/jobs
            if (path.Contains("/actions/runs/", StringComparison.Ordinal)
                && path.EndsWith("/jobs", StringComparison.Ordinal))
            {
                var runId = ExtractRunIdFromJobsPath(path);
                lock (_lock) _jobsEndpointHits.Add(uri.ToString());
                var jobsBody = _jobsBodyForRun(runId);
                if (jobsBody is null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(OkResponse(jobsBody));
            }

            // Job-log endpoint: /repos/{owner}/{repo}/actions/jobs/{jobId}/logs
            if (path.Contains("/actions/jobs/", StringComparison.Ordinal)
                && path.EndsWith("/logs", StringComparison.Ordinal))
            {
                var jobId = ExtractJobIdFromLogsPath(path);
                lock (_lock) _jobLogHits.Add(jobId);
                var logBody = _logBodyForJob(jobId);
                if (logBody is null)
                {
                    // Simulate a failed log download: 302 to a URL that returns 404.
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                    {
                        Headers = { Location = new Uri($"https://logs.example.com/{jobId}") }
                    });
                }
                // 302 to a cross-host URL where the body is served.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri($"https://logs.example.com/{jobId}") }
                });
            }

            // Redirect target (cross-host): serve the log body if one was configured.
            if (uri.Host.Equals("logs.example.com", StringComparison.OrdinalIgnoreCase))
            {
                // Extract jobId from the URL path (the last segment).
                var seg = uri.AbsolutePath.Trim('/');
                if (long.TryParse(seg, out var jobId))
                {
                    var logBody = _logBodyForJob(jobId);
                    if (logBody is null)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(logBody, Encoding.UTF8, "text/plain")
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static long ExtractRunIdFromJobsPath(string path)
        {
            // /repos/{owner}/{repo}/actions/runs/{runId}/jobs
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "runs" && long.TryParse(parts[i + 1], out var runId))
                    return runId;
            }
            return 0;
        }

        private static long ExtractJobIdFromLogsPath(string path)
        {
            // /repos/{owner}/{repo}/actions/jobs/{jobId}/logs
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "jobs" && long.TryParse(parts[i + 1], out var jobId))
                    return jobId;
            }
            return 0;
        }
    }

    /// <summary>
    /// Builds a check-runs JSON body where the single check run carries a <c>details_url</c>
    /// pointing at <c>/actions/runs/{runId}</c> (so the RunId is parsed), the given output
    /// summary/text, and the given html_url.
    /// </summary>
    private static string CheckRunsJsonWithDetailsUrl(
        string runId,
        string name = "build",
        string? summary = null,
        string? text = null,
        string? htmlUrl = null) =>
        RawCheckRunsJson(1, $$"""
            [{
              "name": "{{name}}",
              "status": "completed",
              "conclusion": "failure",
              "html_url": "{{htmlUrl ?? $"https://github.com/org/test-repo/actions/runs/{runId}"}}",
              "details_url": "https://github.com/org/test-repo/actions/runs/{{runId}}",
              "output": { "summary": {{JsonString(summary)}}, "text": {{JsonString(text)}} }
            }]
            """);

    /// <summary>
    /// Builds a check-runs JSON body with two check runs, each with a <c>details_url</c>
    /// pointing at the same <c>/actions/runs/{runId}</c>. Both have no output.
    /// </summary>
    private static string CheckRunsJsonTwoNoOutputSameRunId(string runId, string name1 = "build", string name2 = "lint") =>
        RawCheckRunsJson(2, $$"""
            [{
              "name": "{{name1}}",
              "status": "completed",
              "conclusion": "failure",
              "html_url": "https://github.com/org/test-repo/actions/runs/{{runId}}",
              "details_url": "https://github.com/org/test-repo/actions/runs/{{runId}}",
              "output": { "summary": null, "text": null }
            },
            {
              "name": "{{name2}}",
              "status": "completed",
              "conclusion": "failure",
              "html_url": "https://github.com/org/test-repo/actions/runs/{{runId}}",
              "details_url": "https://github.com/org/test-repo/actions/runs/{{runId}}",
              "output": { "summary": null, "text": null }
            }]
            """);

    /// <summary>
    /// Builds a check-runs JSON body with two check runs: the first has parseable output, the
    /// second has no output but shares the same runId.
    /// </summary>
    private static string CheckRunsJsonOutputThenNoOutputSameRunId(string runId) =>
        RawCheckRunsJson(2, $$"""
            [{
              "name": "build",
              "status": "completed",
              "conclusion": "failure",
              "html_url": "https://github.com/org/test-repo/actions/runs/{{runId}}",
              "details_url": "https://github.com/org/test-repo/actions/runs/{{runId}}",
              "output": { "summary": "✗ MyApp.Tests.FooTests.Bar", "text": null }
            },
            {
              "name": "lint",
              "status": "completed",
              "conclusion": "failure",
              "html_url": "https://github.com/org/test-repo/actions/runs/{{runId}}",
              "details_url": "https://github.com/org/test-repo/actions/runs/{{runId}}",
              "output": { "summary": null, "text": null }
            }]
            """);

    /// <summary>Renders a C# string as a JSON string literal (with quotes), or <c>null</c>.</summary>
    private static string JsonString(string? value) =>
        value is null ? "null" : JsonSerializer.Serialize(value);

    /// <summary>Jobs JSON body for one failed job with the given jobId.</summary>
    private static string JobsJsonOneFailedJob(long jobId) =>
        JsonSerializer.Serialize(new { total_count = 1, jobs = new[] { new { id = jobId, conclusion = "failure" } } });

    /// <summary>An xUnit failure log body containing one parseable failure.</summary>
    private static string LogWithOneFailure() => """
        Starting test execution...

        Failed Tests.Alpha.First [2 ms]
          Error Message:
            alpha failure
          Stack Trace:
            at Alpha.First()
        """;

    /// <summary>An xUnit failure log body with no parseable failures (a passing summary).</summary>
    private static string LogWithNoFailuresAndSnippet() => """
        Passed!  - Failed:     0, Passed:   10, Skipped:     0, Total:   10
        Total tests: 10
        """;

    /// <summary>An empty log body (whitespace only), yielding an empty snippet.</summary>
    private static string EmptyLog() => "   ";

    /// <summary>
    /// Retention overflow visibility at the issue level: when more failures parse than the retention
    /// bound allows, the created issues must SHOW the overflow summary next to the retained failure —
    /// it must never be hidden in an unused snippet.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_RetentionOverflow_SurfacesSummaryInIssues()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var retainedCap = CiLogStreamParser.MaxRetainedFailureCount;
        var failureCount = retainedCap + 1;
        var failureLog = string.Concat(Enumerable.Range(0, failureCount).Select(i =>
            $"Failed Tests.Overflow.Case{i:D4} [1 ms]\n  Error Message:\n    boom {i}\n  Stack Trace:\n    at Case{i:D4}()\n\n"));

        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => failureLog);
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // One issue per RETAINED failure, and every issue carries the overflow summary.
        Assert.Equal(CiLogStreamParser.MaxRetainedFailureCount, issueStore.Issues.Count);
        Assert.All(issueStore.Issues.Values, issue =>
        {
            Assert.Contains("Test: Tests.Overflow.Case", issue.Description, StringComparison.Ordinal);
            Assert.Contains("1 of 201", issue.Description, StringComparison.Ordinal);
            Assert.Contains("occurrence(s) were not retained", issue.Description, StringComparison.Ordinal);
            // The summary sits inside the description the reader sees, not in a snippet.
            Assert.Contains("CI run: https://github.com/org/test-repo/actions/runs/123", issue.Description, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The AGGREGATE retained-character cap is visible in the produced issues: when the retained
    /// failures' fields hit the character budget, the created issues must SHOW the overflow summary
    /// next to the retained failure.
    /// </summary>
    /// <remarks>
    /// The failure count is deliberately BELOW the retained-count cap, so only the aggregate
    /// character cap can be the binding constraint.
    /// </remarks>
    [Fact]
    public async Task MonitorMergeAsync_AggregateBytesOverflow_SurfacesSummaryInIssues()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        const int failureCount = 100;
        const int errorChars = 3_000;
        var failureLog = string.Concat(Enumerable.Range(0, failureCount).Select(i =>
            $"Failed Tests.Agg.Issue{i:D3} [1 ms]\n  Error Message:\n    {new string('e', errorChars)}\n  Stack Trace:\n    at Agg.Issue{i:D3}()\n\n"));

        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => failureLog);
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.NotEmpty(issueStore.Issues);
        Assert.All(issueStore.Issues.Values, issue =>
        {
            Assert.Contains("Test: Tests.Agg.Issue", issue.Description, StringComparison.Ordinal);
            Assert.Contains("occurrence(s) were not retained", issue.Description, StringComparison.Ordinal);
            // The summary names the aggregate character bound that actually bound.
            Assert.Contains($"{MaxRetainedFailureChars:N0} characters per run", issue.Description, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The aggregate bound applies to the WHOLE RUN across every failed job, and the overflow
    /// summary visible in the issues counts the occurrences of ALL jobs.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_MultipleJobs_AggregateOverflowSummaryCountsEveryJob()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        const int jobCount = 4;
        const int failuresPerJob = 60; // 4 x 60 = 240 > the 200 retained-failure cap
        const int totalParsed = jobCount * failuresPerJob;

        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JsonSerializer.Serialize(new
            {
                total_count = jobCount,
                jobs = new[] { 111, 222, 333, 444 }.Select(id => new { id, conclusion = "failure" }).ToArray(),
            }),
            jobId =>
            {
                var log = string.Concat(Enumerable.Range(0, failuresPerJob).Select(i =>
                    $"Failed Tests.Job{jobId}.Case{i:D3} [1 ms]\n  Error Message:\n    boom {i}\n  Stack Trace:\n    at Job{jobId}.Case{i:D3}()\n\n"));
                return log;
            });
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.NotEmpty(issueStore.Issues);
        Assert.All(issueStore.Issues.Values, issue =>
        {
            // The summary counts EVERY job's occurrences, not just the first job's.
            Assert.Contains($"{totalParsed - MaxRetainedFailureCount} of {totalParsed}", issue.Description, StringComparison.Ordinal);
            Assert.Contains("occurrence(s) were not retained", issue.Description, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The per-line cap is visible in the produced issue: an oversized failure line is truncated with
    /// an explicit marker inside the issue's error text, so the reader can see that it was cut.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_OversizedFailureLine_TruncationMarkerVisibleInIssue()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var hugeLine = "    " + new string('E', 9_000);
        var failureLog = "Failed Tests.Huge.Issue [1 ms]\n  Error Message:\n" + hugeLine
            + "\n  Stack Trace:\n    at Huge.Issue()\n\n";

        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => failureLog);
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: Tests.Huge.Issue", issue.Title);
        // The truncation marker and its decoded-character count are visible in the issue.
        var omittedChars = hugeLine.Length - MaxLogLineChars;
        Assert.Contains($"truncated: {omittedChars:N0} characters omitted", issue.Description, StringComparison.Ordinal);
        // The truncated tail really is gone from the issue text. The run of 'E's that survives is
        // bounded by the line cap (the line's own indent is part of the cap), so the cap itself is
        // the discriminator — a removed cap would leave the whole 9,000-character run in the issue.
        var longestRun = 0;
        var currentRun = 0;
        foreach (var ch in issue.Description)
        {
            currentRun = ch == 'E' ? currentRun + 1 : 0;
            if (currentRun > longestRun)
                longestRun = currentRun;
        }

        Assert.Equal(MaxLogLineChars - "    ".Length, longestRun);
        Assert.True(longestRun < hugeLine.Length, "The oversized line must have been truncated.");
    }

    [Fact]
    public async Task MonitorMergeAsync_NoOutputLogWithTestFailures_CreatesLogDerivedIssues()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => LogWithOneFailure());
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // One issue created from the log-derived test failure.
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: Tests.Alpha.First", issue.Title);
        Assert.Contains("Test: Tests.Alpha.First", issue.Description);
        Assert.Contains("Error: alpha failure", issue.Description);
        Assert.Contains("Stack Trace:", issue.Description);
        Assert.Contains("at Alpha.First()", issue.Description);
        Assert.Contains("CI run: https://github.com/org/test-repo/actions/runs/123", issue.Description);
        Assert.Equal("goal-1", issue.SourceGoalId);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }

    /// <summary>
    /// The log-fallback issue carries the bounded head+tail excerpt under the NEW label — the old
    /// "Log output (last 500 chars)" label is gone.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_NoOutputLogNoTestsWithSnippet_CreatesFallbackIssue()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => LogWithNoFailuresAndSnippet());
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // One fallback issue: no tests parsed but the log yielded a snippet.
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: build", issue.Title);
        // The literal label is pinned here (not the production constant) so the rendered format
        // cannot drift silently.
        Assert.Contains("Log output (first 2000 and last 2000 chars):", issue.Description, StringComparison.Ordinal);
        // The obsolete last-500 label is gone.
        Assert.DoesNotContain("last 500", issue.Description, StringComparison.Ordinal);
        Assert.Contains("Passed!", issue.Description);
        Assert.Contains("CI run: https://github.com/org/test-repo/actions/runs/123", issue.Description);
        Assert.Equal("goal-1", issue.SourceGoalId);
    }

    [Fact]
    public async Task MonitorMergeAsync_NoOutputLogNoTestsEmptySnippet_CreatesUrlOnlyIssue()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => EmptyLog());
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // Empty snippet → URL-only fallback (no "Log output" section).
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: build", issue.Title);
        Assert.DoesNotContain("Log output", issue.Description);
        Assert.DoesNotContain("Test:", issue.Description);
        Assert.Contains("CI run: https://github.com/org/test-repo/actions/runs/123", issue.Description);
    }

    [Fact]
    public async Task MonitorMergeAsync_NoOutputNoLogs_CreatesUrlOnlyIssue()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            // The jobs endpoint returns 404 → FetchJobLogsAsync returns null → URL-only fallback.
            runId => null,
            jobId => null);
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: build", issue.Title);
        Assert.DoesNotContain("Log output", issue.Description);
        Assert.DoesNotContain("Test:", issue.Description);
        Assert.Contains("CI run: https://github.com/org/test-repo/actions/runs/123", issue.Description);
    }

    [Fact]
    public async Task MonitorMergeAsync_TwoNoOutputSameRunId_LogFetchedOnceSecondSkipped()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonTwoNoOutputSameRunId("123"),
            runId => JobsJsonOneFailedJob(111),
            jobId => LogWithOneFailure());
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // The jobs endpoint must be hit exactly once (the second run with the same runId is skipped).
        Assert.Single(handler.JobsEndpointHits);
        // The job-log endpoint is hit once (only for the first run's failed job).
        Assert.Equal([111], handler.JobLogHits);
        // Only one issue is created (from the first run's log); the second run is skipped entirely.
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: Tests.Alpha.First", issue.Title);
    }

    [Fact]
    public async Task MonitorMergeAsync_OutputThenNoOutputSameRunId_BothProcessed()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonOutputThenNoOutputSameRunId("123"),
            runId => JobsJsonOneFailedJob(111),
            jobId => LogWithOneFailure());
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // The first run has parseable output (✗ MyApp.Tests.FooTests.Bar) → an output-derived
        // issue is created and the runId is NOT added to the processed set.
        // The second run has no output but shares the runId → it still fetches logs (output does
        // not mark the runId as processed) and creates a log-derived issue.
        Assert.Equal(2, issueStore.Issues.Count);
        var titles = issueStore.Issues.Values.Select(i => i.Title).Order().ToList();
        Assert.Contains("CI failure: MyApp.Tests.FooTests.Bar", titles);
        Assert.Contains("CI failure: Tests.Alpha.First", titles);

        // The jobs endpoint is hit once (the second run fetches logs for the same runId; the
        // FetchJobLogsAsync cache returns the cached result without a second jobs HTTP call).
        Assert.Single(handler.JobsEndpointHits);
    }

    /// <summary>
    /// Log-derived dedup (<c>BuildLogDerivedDedupAppend</c>): the append block is stamped with
    /// EXACTLY the service clock's current time.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_DedupAppends_LogDerivedAppendBlockAppended()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => LogWithOneFailure());
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, clock: clock);

        // Pre-seed an existing open issue with the same title and source goal — this is the
        // issue that CreateOrUpdateIssueAsync will find and append to.
        SeedExistingIssue(issueStore, "CI failure: Tests.Alpha.First", "Original description for the first failure.");

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // No new issue is created; the existing one is updated with the log-derived append block.
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("issue-existing", issue.Id);
        Assert.StartsWith("Original description for the first failure.", issue.Description);
        // The log-derived dedup append block is appended, stamped with the manual clock's time.
        Assert.Contains("---", issue.Description);
        Assert.Contains($"\n\n---\n{ExpectedStamp(clock)}\nTest: Tests.Alpha.First\n", issue.Description);
        Assert.Contains("Test: Tests.Alpha.First", issue.Description);
        Assert.Contains("Error: alpha failure", issue.Description);
        Assert.Contains("Stack Trace:", issue.Description);
        Assert.Contains("at Alpha.First()", issue.Description);
    }

    /// <summary>
    /// Overflow survives the DEDUP path too: when a matching open issue already exists, the
    /// log-derived dedup-append block carries BOTH the retained failure and the overflow summary,
    /// stamped with exactly the service clock's time.
    /// </summary>
    /// <remarks>
    /// Removal-proof: if the summary were only rendered into the issue DESCRIPTION and not into the
    /// dedup append, the updated issue (which keeps its original description) would lose the
    /// omitted-occurrence count entirely — the reader would see a retained failure with no hint that
    /// others were dropped.
    /// </remarks>
    [Fact]
    public async Task MonitorMergeAsync_LogDerivedDedup_OverflowSummaryAppendedWithExactClockStamp()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var failureCount = MaxRetainedFailureCount + 1;
        var failureLog = string.Concat(Enumerable.Range(0, failureCount).Select(i =>
            $"Failed Tests.Dedup.Overflow{i:D4} [1 ms]\n  Error Message:\n    boom {i}\n  Stack Trace:\n    at Dedup.Overflow{i:D4}()\n\n"));

        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => failureLog);
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, clock: clock);

        // The FIRST retained failure gets an existing open issue to append to; the others are new.
        SeedExistingIssue(issueStore, "CI failure: Tests.Dedup.Overflow0000", "Original dedup description.");

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        var existing = issueStore.Issues["issue-existing"];
        Assert.StartsWith("Original dedup description.", existing.Description);
        // The exact clock stamp, then the retained failure, then the overflow summary.
        Assert.Contains($"\n\n---\n{ExpectedStamp(clock)}\nTest: Tests.Dedup.Overflow0000\n", existing.Description);
        Assert.Contains("Error: boom 0", existing.Description);
        Assert.Contains("1 of 201", existing.Description, StringComparison.Ordinal);
        Assert.Contains("occurrence(s) were not retained", existing.Description, StringComparison.Ordinal);
        // The summary is really part of the APPENDED block, and the CI run link closes it.
        Assert.EndsWith("CI run: https://github.com/org/test-repo/actions/runs/123", existing.Description);
        Assert.Contains("CI run: https://github.com/org/test-repo/actions/runs/123", existing.Description);
    }

    /// <summary>
    /// Log-fallback dedup (<c>BuildLogFallbackDedupAppend</c>): same routing shape as
    /// <see cref="MonitorMergeAsync_NoOutputLogNoTestsWithSnippet_CreatesFallbackIssue"/>, but a
    /// matching open issue already exists, so no issue is created and the existing one gains a
    /// block stamped with EXACTLY the service clock's current time followed by the log snippet.
    /// </summary>
    [Fact]
    public async Task MonitorMergeAsync_LogFallbackDedup_AppendsExactClockStamp()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => LogWithNoFailuresAndSnippet());
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, clock: clock);
        SeedExistingIssue(issueStore, "CI failure: build", "Original fallback description.");

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // No new issue: the existing one is updated with the log-fallback append block.
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("issue-existing", issue.Id);
        Assert.StartsWith("Original fallback description.", issue.Description);
        // The exact clock stamp, then the NEW head+tail excerpt label (the old "last 500" label
        // would be a different string, so this assertion is discriminating).
        Assert.Contains($"\n\n---\n{ExpectedStamp(clock)}\nLog output (first 2000 and last 2000 chars):\n", issue.Description);
        Assert.DoesNotContain("last 500", issue.Description, StringComparison.Ordinal);
        Assert.Contains("Passed!", issue.Description);
        Assert.Contains("CI run: https://github.com/org/test-repo/actions/runs/123", issue.Description);
        Assert.Single(handler.JobLogHits);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }

    /// <summary>
    /// URL-only dedup (<c>BuildUrlOnlyDedupAppend</c>): same routing shapes as
    /// <see cref="MonitorMergeAsync_NoOutputLogNoTestsEmptySnippet_CreatesUrlOnlyIssue"/> (log
    /// fetched but empty) and <see cref="MonitorMergeAsync_NoOutputNoLogs_CreatesUrlOnlyIssue"/>
    /// (jobs endpoint 404), but a matching open issue already exists, so no issue is created
    /// and the existing one gains EXACTLY the clock-stamped URL-only block.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MonitorMergeAsync_UrlOnlyDedup_AppendsExactClockStamp(bool logFetchedButEmpty)
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var handler = logFetchedButEmpty
            ? new CiFailureRoutingHandler(
                CheckRunsJsonWithDetailsUrl("123", summary: null),
                runId => JobsJsonOneFailedJob(111),
                jobId => EmptyLog())
            : new CiFailureRoutingHandler(
                CheckRunsJsonWithDetailsUrl("123", summary: null),
                runId => null,
                jobId => null);
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, clock: clock);
        SeedExistingIssue(issueStore, "CI failure: build", "Original URL-only description.");

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // No new issue: the existing one is updated with exactly the URL-only append block.
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("issue-existing", issue.Id);
        Assert.Equal(
            $"Original URL-only description.\n\n---\n{ExpectedStamp(clock)}\nCI run: https://github.com/org/test-repo/actions/runs/123",
            issue.Description);
        Assert.Single(handler.JobsEndpointHits);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }

    [Fact]
    public async Task StartupScanAsync_FailedCiWithLogs_CreatesLogDerivedIssues()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => LogWithOneFailure());
        var service = CreateService(
            handler, issueStore: issueStore, eventBus: eventBus, goalStore: StoreWith(CompletedGoal()));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        // The startup scan probes the failed check run and, because it has no parseable output,
        // fetches the job logs and creates a log-derived issue.
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("CI failure: Tests.Alpha.First", issue.Title);
        Assert.Contains("Test: Tests.Alpha.First", issue.Description);
        Assert.Contains("Error: alpha failure", issue.Description);
        Assert.Contains("at Alpha.First()", issue.Description);
        Assert.Equal("goal-1", issue.SourceGoalId);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiFailed, evt.Type);
    }
}
