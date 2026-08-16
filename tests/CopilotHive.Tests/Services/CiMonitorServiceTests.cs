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
        ILogger<CiMonitorService>? logger = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new CiMonitorService(
            issueStore: issueStore,
            eventBus: eventBus,
            config: config ?? CreateConfig(CreateRepo()),
            httpClientFactory: factory.Object,
            logger: logger ?? NullLogger<CiMonitorService>.Instance,
            pollInterval: PollInterval,
            timeoutOverride: Timeout);
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

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string? retryAfter = null, string? rateLimitRemaining = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is not null)
            response.Headers.Add("Retry-After", retryAfter);
        if (rateLimitRemaining is not null)
            response.Headers.Add("X-RateLimit-Remaining", rateLimitRemaining);
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
}
