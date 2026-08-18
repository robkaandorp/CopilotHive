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
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

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
        TimeSpan? timeoutOverride = null)
    {
        return new CiMonitorService(
            goalStore: goalStore,
            issueStore: issueStore,
            eventBus: eventBus,
            config: config ?? CreateConfig(CreateRepo()),
            httpClientFactory: CreateFactory(handler),
            logger: logger ?? NullLogger<CiMonitorService>.Instance,
            pollInterval: PollInterval,
            timeoutOverride: timeoutOverride ?? Timeout,
            startupScanWindow: startupScanWindow);
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
        TimeSpan? timeoutOverride = null)
    {
        return new CiMonitorService(
            goalStore: goalStore,
            issueStore: issueStore,
            eventBus: eventBus,
            config: config ?? CreateConfig(CreateRepo()),
            httpClientFactory: CreateFactory(handler),
            logger: logger ?? NullLogger<CiMonitorService>.Instance,
            pollInterval: PollInterval,
            timeoutOverride: timeoutOverride ?? Timeout,
            startupScanWindow: startupScanWindow);
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
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "in_progress", null, null, null))));
        var service = CreateService(handler, eventBus: eventBus, logger: logger);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // A no-op would issue zero requests; a premature terminal-return would issue exactly one.
        Assert.True(handler.Requests.Count > 1,
            $"Timeout must be reached by polling; got {handler.Requests.Count} request(s).");
        Assert.Empty(eventBus.Published);
        // The timeout branch is distinct from the caller-cancellation branch.
        Assert.Contains(logger.Snapshot(), e => e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Snapshot(), e => e.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MonitorMergeAsync_ZeroCheckRuns_PollsUntilTimeout()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(0)));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.True(handler.Requests.Count >= 2, $"Expected multiple polls, got {handler.Requests.Count}");
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

    [Fact]
    public async Task MonitorMergeAsync_DedupSameTitleAndGoal_AppendsDescription()
    {
        var eventBus = new RecordingEventBus();
        var issueStore = new FakeIssueStore();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "failure", "✗ MyApp.Tests.CalculatorTests.Add_TwoNumbers_ReturnsSum", "First failure"))));
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        // First call creates the issue.
        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.DoesNotContain("---", issue.Description);

        // Second call (same goal, same title) appends.
        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        Assert.Single(issueStore.Issues.Values);
        Assert.Contains("---", issueStore.Issues.Values.Single().Description);
        Assert.Contains("[Updated", issueStore.Issues.Values.Single().Description);
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

        // Exactly one request proves 401 terminates immediately. The poll interval (50ms) is
        // 10x smaller than the timeout (500ms), so a retry-until-timeout branch would issue
        // many requests and fail this assertion.
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
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
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
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.True(handler.Requests.Count > 1,
            $"A retried status must poll repeatedly; got {handler.Requests.Count} request(s).");
        Assert.Empty(eventBus.Published);
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
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    [Fact]
    public async Task MonitorMergeAsync_5xx_Retries()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.InternalServerError);
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    [Fact]
    public async Task MonitorMergeAsync_MalformedJson_Retries()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return OkResponse("not valid json");
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
    }

    [Fact]
    public async Task MonitorMergeAsync_TransportException_Retries()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("Simulated transport failure");
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.CiSucceeded, evt.Type);
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
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            // Force a retry so more than one request is inspected — the header must be
            // attached to every request, not only the first.
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.InternalServerError);
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var service = CreateService(handler, eventBus: eventBus, logger: logger);

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

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
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorGoalAsync("goal-1", "sha1,sha2", ["repo-a", "repo-b"], TestContext.Current.CancellationToken);

        Assert.True(bothInFlight.Task.IsCompletedSuccessfully,
            "Both repositories must be monitored concurrently; the rendezvous was never reached.");
        Assert.Equal(2, eventBus.Published.Count);
        Assert.All(eventBus.Published, e => Assert.Equal(EventType.CiSucceeded, e.Type));
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
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(async _ =>
        {
            callCount++;
            if (callCount == 1)
            {
                await gate.Task;
                return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
            }
            return OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null)));
        });
        var service = CreateService(handler, eventBus: eventBus);

        var first = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        // Give the first call time to add to in-flight and start the HTTP request.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);
        gate.SetResult(true);
        await first;

        Assert.Equal(1, callCount);
        Assert.Single(eventBus.Published);
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
        var service = CreateService(handler, eventBus: eventBus, logger: logger);

        using var cts = new CancellationTokenSource();
        var monitorTask = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

        // Rendezvous: proves the implementation actually issued a request (rules out a no-op).
        Assert.True(await requestStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "The service never issued a request — cancellation would be vacuous.");
        await cts.CancelAsync();
        cancelObserved.SetResult(true);
        await monitorTask;

        Assert.Empty(eventBus.Published);
        Assert.Single(handler.Requests);
        Assert.Contains(logger.Snapshot(), e => e.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        // Caller cancellation is authoritative — it must not be reported as a CI timeout.
        Assert.DoesNotContain(logger.Snapshot(), e => e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
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
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, logger: logger);

        var monitorTask = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", cts.Token);

        Assert.True(await issueCallStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "The failure path never reached the issue store — cancellation would be vacuous.");
        await cts.CancelAsync();
        cancelObserved.SetResult(true);
        await monitorTask;

        // The OCE must NOT be swallowed as a store failure and turned into a CiFailed event.
        Assert.Empty(eventBus.Published);
        Assert.Empty(issueStore.Issues);
        Assert.Contains(logger.Snapshot(), e => e.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
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
    /// Records every <see cref="CiMonitorService.MonitorMergeAsync"/> launch (the startup scan's
    /// fire-and-forget continuation) instead of performing it, so tests can assert whether
    /// background monitoring was started and with which cancellation token.
    /// </summary>
    private sealed class MonitorRecordingService : CiMonitorService
    {
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MonitorRecordingService(
            IGoalStore? goalStore,
            HiveConfigFile config,
            IHttpClientFactory httpClientFactory,
            IEventBus? eventBus = null,
            TimeSpan? startupScanWindow = null)
            : base(
                goalStore: goalStore,
                eventBus: eventBus,
                config: config,
                httpClientFactory: httpClientFactory,
                pollInterval: PollInterval,
                timeoutOverride: Timeout,
                startupScanWindow: startupScanWindow)
        {
        }

        public ConcurrentBag<(string GoalId, string Repo, string Sha, CancellationToken Token)> Monitored { get; } = [];

        /// <summary>Completes as soon as the first monitoring launch is observed.</summary>
        public Task FirstCall => _firstCall.Task;

        public override Task MonitorMergeAsync(string goalId, string repoName, string mergeCommitSha, CancellationToken ct)
        {
            Monitored.Add((goalId, repoName, mergeCommitSha, ct));
            _firstCall.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static MonitorRecordingService CreateRecordingService(
        ScriptedHttpMessageHandler handler,
        IGoalStore? goalStore,
        HiveConfigFile? config = null,
        IEventBus? eventBus = null,
        TimeSpan? startupScanWindow = null) =>
        new(goalStore, config ?? CreateConfig(CreateRepo()), CreateFactory(handler), eventBus, startupScanWindow);

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
            CompletedAt = completedAt ?? DateTime.UtcNow,
            RepositoryNames = repositoryNames.Length == 0 ? ["test-repo"] : [.. repositoryNames],
        };

    private static InMemoryGoalStore StoreWith(params Goal[] goals)
    {
        var store = new InMemoryGoalStore();
        foreach (var goal in goals)
            store.AddGoal(goal);
        return store;
    }

    /// <summary>
    /// Waits long enough for a fire-and-forget monitoring launch to have been observed, so an
    /// assertion that monitoring did NOT start is not merely winning a race.
    /// </summary>
    private static async Task AllowFireAndForgetToRunAsync() =>
        await Task.Delay(250, TestContext.Current.CancellationToken);

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
        await service.FirstCall.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var monitored = Assert.Single(service.Monitored);
        Assert.Equal(("goal-1", "test-repo", "abc123"), (monitored.GoalId, monitored.Repo, monitored.Sha));
        Assert.Equal(cts.Token, monitored.Token);
        Assert.True(monitored.Token.CanBeCanceled);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task StartupScanAsync_NoChecksAndMergedLongAgo_SkipsMonitoring()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(0)));
        var goal = CompletedGoal(completedAt: DateTime.UtcNow.AddMinutes(-10));
        var service = CreateRecordingService(handler, StoreWith(goal), eventBus: eventBus);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);
        await AllowFireAndForgetToRunAsync();

        // The commit WAS probed — the skip is a decision about the result, not a no-op.
        Assert.Single(handler.Requests);
        Assert.Empty(service.Monitored);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// Complement of the test above: the identical no-checks response for a just-merged commit
    /// must start monitoring, proving the 5-minute grace period is what decides.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_NoChecksAndRecentlyMerged_StartsMonitoring()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(CheckRunsJson(0)));
        var goal = CompletedGoal(completedAt: DateTime.UtcNow.AddMinutes(-1));
        var service = CreateRecordingService(handler, StoreWith(goal), eventBus: eventBus);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);
        await service.FirstCall.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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
        await AllowFireAndForgetToRunAsync();

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
        await service.FirstCall.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task StartupScanAsync_GoalCompletedOutsideWindow_NotScanned()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var goal = CompletedGoal(completedAt: DateTime.UtcNow.AddMinutes(-30));
        var service = CreateService(
            handler, eventBus: eventBus, goalStore: StoreWith(goal), startupScanWindow: TimeSpan.FromMinutes(10));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// The complement of the out-of-window test: the same goal inside the configured window IS
    /// scanned, so an empty result cannot come from the scan being broken outright.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_GoalCompletedInsideWindow_IsScanned()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ =>
            OkResponse(CheckRunsJson(1, ("build", "completed", "success", null, null))));
        var goal = CompletedGoal(completedAt: DateTime.UtcNow.AddMinutes(-5));
        var service = CreateService(
            handler, eventBus: eventBus, goalStore: StoreWith(goal), startupScanWindow: TimeSpan.FromMinutes(10));

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
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus, goalStore: StoreWith(CompletedGoal()));

        using var monitorCts = new CancellationTokenSource();
        var monitorTask = service.MonitorMergeAsync("goal-1", "test-repo", "abc123", monitorCts.Token);
        await monitorRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.True(requestCount >= 2, $"The scan must have probed; only {requestCount} request(s) were made.");
        Assert.Empty(issueStore.Issues);
        Assert.Empty(eventBus.Published);

        // Unblock the live monitor: cancelling first means its still-running result ends the
        // loop without publishing anything of its own.
        await monitorCts.CancelAsync();
        releaseMonitor.TrySetResult();
        await monitorTask;
        Assert.Empty(eventBus.Published);
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

    private static async Task<CiProbeResult> ProbeAsync(ScriptedHttpMessageHandler handler)
    {
        var service = CreateService(handler);
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
        var resetEpoch = DateTimeOffset.UtcNow.AddSeconds(120).ToUnixTimeSeconds();
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, rateLimitRemaining: "0",
                rateLimitReset: resetEpoch.ToString(CultureInfo.InvariantCulture)));

        var result = await ProbeAsync(handler);

        Assert.Equal(CiProbeStatus.Error, result.Status);
        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.NotNull(result.RetryAfter);
        Assert.InRange(result.RetryAfter!.Value, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(121));
    }

    /// <summary>A reset timestamp already in the past must clamp to zero, never a negative delay.</summary>
    [Fact]
    public async Task ProbeCiStatusAsync_403WithPastRateLimitReset_ClampsRetryAfterToZero()
    {
        var resetEpoch = DateTimeOffset.UtcNow.AddSeconds(-120).ToUnixTimeSeconds();
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, rateLimitRemaining: "0",
                rateLimitReset: resetEpoch.ToString(CultureInfo.InvariantCulture)));

        var result = await ProbeAsync(handler);

        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.Equal(TimeSpan.Zero, result.RetryAfter);
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
        var resetEpoch = DateTimeOffset.UtcNow.AddSeconds(120).ToUnixTimeSeconds();
        var handler = new ScriptedHttpMessageHandler(_ =>
            ErrorResponse(HttpStatusCode.Forbidden, retryAfter: "not-a-number",
                rateLimitReset: resetEpoch.ToString(CultureInfo.InvariantCulture)));

        var result = await ProbeAsync(handler);

        Assert.Equal("403-rate-limit", result.ErrorDetail);
        Assert.NotNull(result.RetryAfter);
        Assert.InRange(result.RetryAfter!.Value, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(121));
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
        var service = CreateService(handler, config: config, eventBus: eventBus);

        // Identical SHA for both repositories — only the repository dimension separates them.
        await service.MonitorGoalAsync("goal-1", "same-sha,same-sha", ["repo-a", "repo-b"], TestContext.Current.CancellationToken);

        Assert.True(bothInFlight.Task.IsCompletedSuccessfully,
            "Both repositories must be monitored for the same SHA; the in-flight key is not repository-aware.");
        Assert.Equal(2, handler.Requests.Count);
        // Event dedup must also be repository-aware: one CiSucceeded per repository.
        Assert.Equal(2, eventBus.Published.Count);
        Assert.All(eventBus.Published, e => Assert.Equal(EventType.CiSucceeded, e.Type));
        Assert.Equal(["repo-a", "repo-b"], eventBus.Published.Select(e => e.Repository).Order().ToList());
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
            """;

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

    [Fact]
    public async Task FetchJobLogsAsync_LargeLog_BoundedTo200KBytes()
    {
        // The ring buffer does NOT bound how many bytes are read — it reads the whole stream
        // and retains only the final 200,000 bytes. So the observable contract is WHICH content
        // survives, not how much was read.
        //
        // The log below is exactly 300,000 ASCII bytes with a parseable xUnit failure in the
        // dropped prefix (offset 0, well inside the first 100,000 bytes) and a DIFFERENT
        // parseable failure in the retained tail (offset 250,000, inside the last 100,000
        // bytes). The 200,000-byte ring retains bytes [100,000 .. 300,000).
        //
        // Removal-proof: if the ring buffer were removed (or its capacity raised to cover the
        // whole body), the PREFIX failure would also be parsed and the assertion that exactly
        // one failure — the tail one — is returned would fail.
        const string PrefixTestName = "Tests.PrefixDroppedByRingBuffer";
        const string TailTestName = "Tests.TailRetainedByRingBuffer";

        var prefixBlock = $"""
            Failed {PrefixTestName} [1 ms]
              Error Message:
                prefix failure lives in the dropped region
              Stack Trace:
                at {PrefixTestName}()

            """;
        var tailBlock = $"""
            Failed {TailTestName} [2 ms]
              Error Message:
                tail failure lives in the retained region
              Stack Trace:
                at {TailTestName}()

            """;
        // Normalize to '\n' so the byte offsets below hold on every platform.
        prefixBlock = prefixBlock.Replace("\r\n", "\n", StringComparison.Ordinal);
        tailBlock = tailBlock.Replace("\r\n", "\n", StringComparison.Ordinal);

        const int TotalBytes = 300_000;
        const int RingCapacity = 200_000;
        const int TailBlockOffset = 250_000;

        // Filler is a run of 'y' terminated by a newline: it contains no failure header, no
        // "Error Message:" and no "Stack Trace:" line, so it can never contribute a parsed
        // failure of its own. The trailing newline puts the tail block's "Failed ..." header at
        // the start of its own line, which the xUnit header regex requires.
        var fillerBefore = new string('y', TailBlockOffset - prefixBlock.Length - 1) + "\n";
        var fillerAfter = new string('y', TotalBytes - TailBlockOffset - tailBlock.Length);
        var fullLog = prefixBlock + fillerBefore + tailBlock + fillerAfter;

        // Guard the geometry the assertions depend on (ASCII → 1 byte per char).
        Assert.Equal(TotalBytes, fullLog.Length);
        Assert.True(prefixBlock.Length < TotalBytes - RingCapacity,
            "The prefix failure must lie entirely inside the region the ring buffer drops.");
        Assert.True(TailBlockOffset >= TotalBytes - RingCapacity,
            "The tail failure must lie entirely inside the region the ring buffer retains.");

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(fullLog));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);

        var parsedNames = result!.Failures.Select(f => f.TestName).ToList();

        // The prefix failure was overwritten by the ring buffer → it must NOT be parsed.
        Assert.DoesNotContain(PrefixTestName, parsedNames);
        // The tail failure was retained by the ring buffer → it MUST be parsed.
        Assert.Contains(TailTestName, parsedNames);
        // Exactly one failure: dropping the ring buffer would add the prefix failure back.
        var tailFailure = Assert.Single(result.Failures);
        Assert.Equal(TailTestName, tailFailure.TestName);
        Assert.Equal("tail failure lives in the retained region", tailFailure.Error);
        Assert.StartsWith($"at {TailTestName}()", tailFailure.StackTrace, StringComparison.Ordinal);
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

    [Fact]
    public async Task FetchJobLogsAsync_FallbackSnippet_SanitizedBeforeTruncation()
    {
        // Removal-proof: the fallback log is >500 chars and contains a ghp_ token positioned so
        // it lands in the retained last-500 tail. The snippet must equal exactly the last 500
        // chars of the SANITIZED full log — proving sanitization happens BEFORE the last-500
        // truncation and that the secret is redacted in the retained tail. If truncation
        // happened before sanitization (on the raw tail), the raw secret would survive.
        var token = "ghp_" + new string('a', 36);
        // Build a >500-char log whose last 500 chars contain the token.
        var prefix = new string('z', 600);
        // The token must be within the last 500 chars of the full log.
        var suffix = "tail-" + token + "-end";
        var totalLength = prefix.Length + suffix.Length;
        // Ensure suffix (which contains the token) fits entirely within the last 500 chars.
        Assert.True(suffix.Length <= 500);
        Assert.True(totalLength > 500);
        var log = prefix + suffix;

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result!.Failures); // no parseable failures → fallback snippet
        Assert.NotNull(result.FallbackSnippet);

        // The snippet must be the last 500 chars of the SANITIZED full log (sanitization first,
        // then truncation to the last 500).
        var sanitizedFull = CiMonitorService.SanitizeLogContent(log);
        var expectedSnippet = sanitizedFull[^500..];
        Assert.Equal(expectedSnippet, result.FallbackSnippet);
        // The token must be redacted in the snippet.
        Assert.DoesNotContain(token, result.FallbackSnippet);
        Assert.Contains("***REDACTED***", result.FallbackSnippet);
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
    public async Task FetchJobLogsAsync_FallbackSnippet_TruncatedToLast500AfterSanitization()
    {
        // Boundary: the fallback snippet is truncated to the last 500 chars AFTER sanitization.
        // A log with no parseable failures and >500 chars of plain text (no secrets) must yield
        // a snippet of exactly 500 chars — the last 500 of the full log.
        var log = new string('w', 800);

        var jobsBody = JobsJson(1, (111, "failure"));
        var handler = new JobsAndLogsHandler(jobsBody, _ => LogResponse(log));
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateServiceForLogFetch();

        var result = await service.FetchJobLogsAsync("run-1", "org", "test-repo", "test-token", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result!.Failures);
        Assert.NotNull(result.FallbackSnippet);
        Assert.Equal(500, result.FallbackSnippet!.Length);
        Assert.All(result.FallbackSnippet, ch => Assert.Equal('w', ch));
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
        Assert.Contains("Log output (last 500 chars):", issue.Description);
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

    [Fact]
    public async Task MonitorMergeAsync_DedupAppends_LogDerivedAppendBlockAppended()
    {
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var handler = new CiFailureRoutingHandler(
            CheckRunsJsonWithDetailsUrl("123", summary: null),
            runId => JobsJsonOneFailedJob(111),
            jobId => LogWithOneFailure());
        var service = CreateService(handler, issueStore: issueStore, eventBus: eventBus);

        // Pre-seed an existing open issue with the same title and source goal — this is the
        // issue that CreateOrUpdateIssueAsync will find and append to.
        var existingIssue = new Issue
        {
            Id = "issue-existing",
            Type = IssueType.Bug,
            Title = "CI failure: Tests.Alpha.First",
            Description = "Original description for the first failure.",
            Severity = IssueSeverity.High,
            Status = IssueStatus.Open,
            RepositoryNames = ["test-repo"],
            SourceGoalId = "goal-1",
            SourceRole = "ci",
        };
        issueStore.Issues[existingIssue.Id] = existingIssue;

        await service.MonitorMergeAsync("goal-1", "test-repo", "abc123", TestContext.Current.CancellationToken);

        // No new issue is created; the existing one is updated with the log-derived append block.
        var issue = Assert.Single(issueStore.Issues.Values);
        Assert.Equal("issue-existing", issue.Id);
        Assert.StartsWith("Original description for the first failure.", issue.Description);
        // The log-derived dedup append block is appended.
        Assert.Contains("---", issue.Description);
        Assert.Contains("[Updated ", issue.Description);
        Assert.Contains("Test: Tests.Alpha.First", issue.Description);
        Assert.Contains("Error: alpha failure", issue.Description);
        Assert.Contains("Stack Trace:", issue.Description);
        Assert.Contains("at Alpha.First()", issue.Description);
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
