using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for <see cref="HiveOrchestratorService.GetWorkerConfig"/> — the gRPC handler that
/// provisions a worker's LLM configuration.
/// <para>
/// Verifies: each field is sourced from its authoritative source (github_token from
/// <c>UserService.GetActiveAccessTokenAsync</c>; provider settings from orchestrator env);
/// absent/whitespace fields are omitted (proto3 optional presence unset); the handler logs the
/// worker_id and which field NAMES were provisioned but NEVER their values.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class GetWorkerConfigHandlerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string TestToken = "ghp_test_provisioning_token_abc";
    private const string TestWorkerId = "worker-prov-1";

    /// <summary>
    /// In-memory SQLite factory backed by a single open connection so the database
    /// survives per-call context disposal.
    /// </summary>
    private sealed class SharedConnectionFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;

        public SharedConnectionFactory()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new CopilotHiveDbContext(options);
        }

        public void Dispose() => _connection.Dispose();
    }

    /// <summary>Creates a real <see cref="UserService"/> backed by an in-memory DB with an admin user holding <paramref name="token"/>.</summary>
    private static (UserService service, IDisposable factory) CreateUserService(string? token)
    {
        var factory = new SharedConnectionFactory();
        var service = new UserService(factory, NullLogger<UserService>.Instance);
        if (token is not null)
        {
            service.CreateOrUpdateUserAsync(
                "999", "testadmin", "Test Admin", null, null,
                token, "refresh", null, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
        return (service, factory);
    }

    private static HiveOrchestratorService CreateService(
        UserService? userService,
        Func<string, string?>? readEnv = null,
        ILogger<HiveOrchestratorService>? logger = null)
    {
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var completionNotifier = new TaskCompletionNotifier();
        var goalManager = new GoalManager();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(pool),
            completionNotifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        var service = new HiveOrchestratorService(
            pool,
            taskQueue,
            pipelineManager,
            completionNotifier,
            dispatcher,
            logger ?? NullLogger<HiveOrchestratorService>.Instance,
            userService: userService);

        if (readEnv is not null)
            service._readEnv = readEnv;

        return service;
    }

    private static ServerCallContext MockContext() =>
        new Mock<ServerCallContext>().Object;

    // ── Token from UserService ─────────────────────────────────────────────────

    [Fact]
    public async Task GetWorkerConfig_TokenFromUserService_IsPresentInResponse()
    {
        var (userService, factory) = CreateUserService(TestToken);
        using var _ = factory;
        var service = CreateService(userService, readEnv: _ => null);

        var response = await service.GetWorkerConfig(
            new GetWorkerConfigRequest { WorkerId = TestWorkerId }, MockContext());

        Assert.True(response.HasGithubToken);
        Assert.Equal(TestToken, response.GithubToken);
    }

    [Fact]
    public async Task GetWorkerConfig_NoUserService_TokenOmitted()
    {
        var service = CreateService(null, readEnv: _ => null);

        var response = await service.GetWorkerConfig(
            new GetWorkerConfigRequest { WorkerId = TestWorkerId }, MockContext());

        Assert.False(response.HasGithubToken);
    }

    // ── Token whitespace/null → omitted ────────────────────────────────────────

    [Fact]
    public async Task GetWorkerConfig_NoUserInDb_TokenOmitted()
    {
        // UserService with an empty DB returns null from GetActiveAccessTokenAsync
        var (userService, factory) = CreateUserService(null);
        using var _ = factory;
        var service = CreateService(userService, readEnv: _ => null);

        var response = await service.GetWorkerConfig(
            new GetWorkerConfigRequest { WorkerId = TestWorkerId }, MockContext());

        Assert.False(response.HasGithubToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task GetWorkerConfig_WhitespaceToken_Omitted(string whitespaceToken)
    {
        // The token is stored in the DB but GetActiveAccessTokenAsync returns it;
        // the handler's IsNullOrWhiteSpace check must omit it.
        var (userService, factory) = CreateUserService(whitespaceToken);
        using var _ = factory;
        var service = CreateService(userService, readEnv: _ => null);

        var response = await service.GetWorkerConfig(
            new GetWorkerConfigRequest { WorkerId = TestWorkerId }, MockContext());

        Assert.False(response.HasGithubToken);
    }

    // ── Provider settings from orchestrator env ────────────────────────────────

    [Fact]
    public async Task GetWorkerConfig_ProviderSettingsFromEnv_ArePresentInResponse()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LLM_PROVIDER"] = "copilot",
            ["OLLAMA_URL"] = "http://ollama:11434",
            ["OLLAMA_API_KEY"] = "ollama-key-123",
            ["OLLAMA_MODEL"] = "llama3",
            ["GITHUB_MODEL"] = "gpt-5",
        };

        var service = CreateService(null, readEnv: name => env.TryGetValue(name, out var v) ? v : null);

        var response = await service.GetWorkerConfig(
            new GetWorkerConfigRequest { WorkerId = TestWorkerId }, MockContext());

        Assert.True(response.HasLlmProvider);
        Assert.Equal("copilot", response.LlmProvider);
        Assert.True(response.HasOllamaUrl);
        Assert.Equal("http://ollama:11434", response.OllamaUrl);
        Assert.True(response.HasOllamaApiKey);
        Assert.Equal("ollama-key-123", response.OllamaApiKey);
        Assert.True(response.HasOllamaModel);
        Assert.Equal("llama3", response.OllamaModel);
        Assert.True(response.HasGithubModel);
        Assert.Equal("gpt-5", response.GithubModel);
    }

    // ── Absent provider settings → omitted ─────────────────────────────────────

    [Fact]
    public async Task GetWorkerConfig_AllProviderSettingsAbsent_AllOmitted()
    {
        var service = CreateService(null, readEnv: _ => null);

        var response = await service.GetWorkerConfig(
            new GetWorkerConfigRequest { WorkerId = TestWorkerId }, MockContext());

        Assert.False(response.HasLlmProvider);
        Assert.False(response.HasOllamaUrl);
        Assert.False(response.HasOllamaApiKey);
        Assert.False(response.HasOllamaModel);
        Assert.False(response.HasGithubModel);
    }

    // ── Whitespace provider settings → omitted ─────────────────────────────────

    [Theory]
    [InlineData("LLM_PROVIDER")]
    [InlineData("OLLAMA_URL")]
    [InlineData("OLLAMA_API_KEY")]
    [InlineData("OLLAMA_MODEL")]
    [InlineData("GITHUB_MODEL")]
    public async Task GetWorkerConfig_WhitespaceProviderSetting_Omitted(string envVar)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [envVar] = "   ",
        };

        var service = CreateService(null, readEnv: name => env.TryGetValue(name, out var v) ? v : null);

        var response = await service.GetWorkerConfig(
            new GetWorkerConfigRequest { WorkerId = TestWorkerId }, MockContext());

        var hasField = envVar switch
        {
            "LLM_PROVIDER" => response.HasLlmProvider,
            "OLLAMA_URL" => response.HasOllamaUrl,
            "OLLAMA_API_KEY" => response.HasOllamaApiKey,
            "OLLAMA_MODEL" => response.HasOllamaModel,
            "GITHUB_MODEL" => response.HasGithubModel,
            _ => throw new InvalidOperationException($"Unknown env var: {envVar}"),
        };
        Assert.False(hasField);
    }

    // ── Logging: field NAMES only, never values ────────────────────────────────

    [Fact]
    public async Task GetWorkerConfig_LogsFieldNameNotValue()
    {
        var captured = new List<string>();
        var loggerFactory = new LoggerFactory(new[] { new StringCapturingLoggerProvider(captured) });
        var logger = loggerFactory.CreateLogger<HiveOrchestratorService>();

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LLM_PROVIDER"] = "copilot",
            ["OLLAMA_API_KEY"] = "secret-ollama-key",
        };

        var (userService, factory) = CreateUserService(TestToken);
        using var _f = factory;
        var service = CreateService(userService, readEnv: name => env.TryGetValue(name, out var v) ? v : null, logger: logger);

        await service.GetWorkerConfig(
            new GetWorkerConfigRequest { WorkerId = TestWorkerId }, MockContext());

        var logLine = captured.SingleOrDefault(s => s.Contains("GetWorkerConfig"));
        Assert.NotNull(logLine);

        // Worker ID is logged
        Assert.Contains(TestWorkerId, logLine);
        // Field names are logged
        Assert.Contains("github_token", logLine);
        Assert.Contains("llm_provider", logLine);
        Assert.Contains("ollama_api_key", logLine);
        // VALUES must never appear
        Assert.DoesNotContain(TestToken, logLine);
        Assert.DoesNotContain("secret-ollama-key", logLine);
    }

    [Fact]
    public async Task GetWorkerConfig_NoFieldsProvisioned_LogsNone()
    {
        var captured = new List<string>();
        var loggerFactory = new LoggerFactory(new[] { new StringCapturingLoggerProvider(captured) });
        var logger = loggerFactory.CreateLogger<HiveOrchestratorService>();

        var service = CreateService(null, readEnv: _ => null, logger: logger);

        await service.GetWorkerConfig(
            new GetWorkerConfigRequest { WorkerId = TestWorkerId }, MockContext());

        var logLine = captured.SingleOrDefault(s => s.Contains("GetWorkerConfig"));
        Assert.NotNull(logLine);
        Assert.Contains("(none)", logLine);
    }

    // ── Capturing logger helper ────────────────────────────────────────────────

    private sealed class StringCapturingLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new StringCapturingLogger(sink);
        public void Dispose() { }
    }

    private sealed class StringCapturingLogger(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink.Add(formatter(state, exception));
        }
    }
}