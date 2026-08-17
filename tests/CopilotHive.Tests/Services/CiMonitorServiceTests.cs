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
}
