using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Knowledge;
using CopilotHive.Metrics;
using CopilotHive.Models;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotHive;

/// <summary>
/// Main Program.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// The global enum converter applied to every minimal-API payload: enums serialize as
    /// snake_case (<c>ReasoningEffort.ExtraHigh</c> → <c>"extra_high"</c>) and integer wire values
    /// are rejected, so an unknown level produces a 400 instead of a silently coerced enum.
    /// <para>
    /// System.Text.Json ranks converters in <see cref="JsonSerializerOptions.Converters"/> ABOVE a
    /// type-level <c>[JsonConverter]</c> attribute, so a plain
    /// <c>JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)</c>
    /// would silently rename enums that deliberately declare their own converter
    /// (<c>LlmSessionType</c>, <c>ModelTier</c>, <c>TaskVerdict</c>, <c>ReviewVerdict</c> — all
    /// PascalCase on the wire). This subclass therefore declines those types so their own
    /// converter keeps winning.
    /// </para>
    /// </summary>
    internal sealed class GlobalStringEnumConverter : JsonConverterFactory
    {
        private readonly JsonStringEnumConverter _inner =
            new(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);

        /// <inheritdoc />
        public override bool CanConvert(Type typeToConvert)
            => _inner.CanConvert(typeToConvert) && !DeclaresOwnConverter(typeToConvert);

        /// <inheritdoc />
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => _inner.CreateConverter(typeToConvert, options);

        /// <summary>
        /// Whether the enum type carries its own type-level <c>[JsonConverter]</c> attribute,
        /// which is the authority for its wire representation.
        /// </summary>
        internal static bool DeclaresOwnConverter(Type typeToConvert)
        {
            var underlying = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
            return underlying.GetCustomAttribute<JsonConverterAttribute>(inherit: false) is not null;
        }
    }

    /// <summary>
    /// Registers the hive's global HTTP JSON options. Kept as a named helper (rather than an
    /// inline lambda at the single call site) so hosts that build their own
    /// <see cref="WebApplication"/> — notably endpoint tests — wire the exact same converter
    /// instead of re-declaring it and drifting from production behaviour.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    internal static void AddHiveJsonOptions(IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new GlobalStringEnumConverter());
        });
    }

    private static async Task<int> Main(string[] args)
    {
        // ── Server mode (only mode) ──────────────────────────────────────────────────
        return await RunServerAsync(args);

        // ────────────────────────────────────────────────────────────────────────────────

        static async Task<int> RunServerAsync(string[] args)
        {
            var port = Constants.DefaultHttpPort;
            var portArg = args.FirstOrDefault(a => a.StartsWith("--port="));
            if (portArg is not null && int.TryParse(portArg["--port=".Length..], out var p))
                port = p;

            var configRepoUrl = args.FirstOrDefault(a => a.StartsWith("--config-repo="))?["--config-repo=".Length..];
            var configRepoPath = args.FirstOrDefault(a => a.StartsWith("--config-repo-path="))?["--config-repo-path=".Length..]
                ?? "./config-repo";

            PrintBanner();

            var builder = WebApplication.CreateBuilder(args);

            // Suppress noisy health-check request logs and framework noise
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);

            // Global JSON serialization for minimal-API endpoints: every enum that does NOT
            // carry its own type-level [JsonConverter] serializes as snake_case (e.g.
            // ReasoningEffort.ExtraHigh → "extra_high"). Integer values are rejected so an
            // unknown or numeric wire value produces a 400 rather than a silently coerced enum.
            // Type-level converters (LlmSessionType, ModelTier, TaskVerdict, ReviewVerdict)
            // take precedence and keep their PascalCase representation.
            AddHiveJsonOptions(builder.Services);

            builder.Services.AddGrpc();
            builder.Services.AddSingleton<WorkerPool>();
            builder.Services.AddSingleton<IWorkerPool>(sp => sp.GetRequiredService<WorkerPool>());
            builder.Services.AddSingleton<GrpcWorkerGateway>();
            builder.Services.AddSingleton<IWorkerGateway>(sp => sp.GetRequiredService<GrpcWorkerGateway>());
            builder.Services.AddSingleton<TaskQueue>();
            builder.Services.AddSingleton<TaskCompletionNotifier>();
            builder.Services.AddSingleton<ImprovementAnalyzer>();
            builder.Services.AddSingleton<GoalReadyNotifier>();

            // Event bus: typed system events broadcast to subscribers (e.g. the Composer)
            builder.Services.AddSingleton<IEventBus, EventBus>();
            builder.Services.AddSingleton<ComposerEventSubscriber>();
            builder.Services.AddSingleton<EventBusStartupScanner>();

            // Agents: AGENTS.md versioning and rollback
            var agentsDir = Environment.GetEnvironmentVariable("AGENTS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "agents");
            if (!Directory.Exists(agentsDir))
                agentsDir = Path.Combine(Directory.GetCurrentDirectory(), "agents");
            builder.Services.AddSingleton(sp =>
                new AgentsManager(agentsDir, sp.GetRequiredService<ILogger<AgentsManager>>()));

            // Persistence: SQLite store for pipeline state (survives restarts)
            var stateDir = Environment.GetEnvironmentVariable("STATE_DIR") ?? "/app/state";
            var dbPath = Path.Combine(stateDir, "copilothive.db");
            builder.Services.AddSingleton(sp =>
                new PipelineStore(
                    sp.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>(),
                    sp.GetRequiredService<ILogger<PipelineStore>>()));

            builder.Services.AddDbContextFactory<CopilotHiveDbContext>(options =>
                options.UseSqlite($"Data Source={dbPath}"));

            // Backup service: creates tar.gz archives of runtime state
            builder.Services.AddSingleton(sp =>
                new BackupService(stateDir,
                    sp.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>(),
                    sp.GetRequiredService<ILogger<BackupService>>()));

            // Metrics: per-iteration metrics persistence
            var metricsDir = Path.Combine(stateDir, "metrics");
            Directory.CreateDirectory(metricsDir);
            builder.Services.AddSingleton(sp =>
                new MetricsTracker(metricsDir, sp.GetRequiredService<ILogger<MetricsTracker>>()));

            // Brain repo manager: persistent read-only clones for Brain file access
            builder.Services.AddSingleton<IBrainRepoManager>(sp =>
                new BrainRepoManager(stateDir, sp.GetRequiredService<ILogger<BrainRepoManager>>()));

            // Composer attachments: singleton store for chat file attachments
            builder.Services.AddSingleton(sp =>
                new ComposerAttachmentService(stateDir, sp.GetRequiredService<ILogger<ComposerAttachmentService>>()));

            builder.Services.AddSingleton(sp =>
                new GoalPipelineManager(sp.GetRequiredService<PipelineStore>()));

            // Brain: direct LLM connection via SharpCoder
            var brainModel = Environment.GetEnvironmentVariable("BRAIN_MODEL");
            var brainContextWindowEnv = Environment.GetEnvironmentVariable("BRAIN_CONTEXT_WINDOW");
            var brainMaxStepsEnv = Environment.GetEnvironmentVariable("BRAIN_MAX_STEPS");
            var ollamaApiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
            if (!string.IsNullOrEmpty(brainModel))
            {
                builder.Services.AddSingleton<IDistributedBrain>(sp =>
                {
                    var config = sp.GetService<HiveConfigFile>();
                    // Config file model takes precedence over env var default
                    var effectiveModel = !string.IsNullOrEmpty(config?.Orchestrator.Model)
                        ? config.Orchestrator.Model
                        : brainModel;
                    var maxCtx = int.TryParse(brainContextWindowEnv, out var envCtx)
                        ? envCtx
                        : config?.TryGetContextWindowForModel(effectiveModel)
                        ?? Constants.DefaultBrainContextWindow;
                    var maxSteps = int.TryParse(brainMaxStepsEnv, out var envSteps)
                        ? envSteps
                        : config?.Orchestrator.BrainMaxSteps ?? Constants.DefaultBrainMaxSteps;

                    return new DistributedBrain(effectiveModel, sp.GetRequiredService<ILogger<DistributedBrain>>(),
                        sp.GetRequiredService<MetricsTracker>(),
                        sp.GetService<AgentsManager>(),
                        maxCtx,
                        maxSteps,
                        sp.GetService<IBrainRepoManager>(),
                        stateDir,
                        sp.GetRequiredService<IGoalStore>(),
                        compactionModel: config?.Models?.CompactionModel,
                        knowledgeGraph: sp.GetService<KnowledgeGraph>(),
                        hiveConfig: config,
                        sessionRegistry: sp.GetService<LlmSessionRegistry>(),
                        configRepo: sp.GetService<ConfigRepoManager>(),
                        reasoningEffort: ParseConfiguredReasoningEffort(
                            config?.Orchestrator?.ReasoningEffort,
                            "orchestrator.reasoning_effort",
                            sp.GetService<ILogger<DistributedBrain>>()),
                        issueStore: sp.GetService<IIssueStore>(),
                        eventBus: sp.GetService<IEventBus>());
                });
            }

            builder.Services.AddSingleton<WorkerUtilizationService>();
            builder.Services.AddSingleton<ClarificationQueueService>();
            builder.Services.AddSingleton<LlmSessionRegistry>();

            // ── Authentication: GitHub OAuth (single-user admin model) ─────────────────
            // Enabled only when both OAuth env vars are set; otherwise the system runs in
            // open mode (no authentication), preserving backward compatibility.
            var oauthClientId = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID");
            var oauthClientSecret = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET");
            var authEnabled = !string.IsNullOrEmpty(oauthClientId) && !string.IsNullOrEmpty(oauthClientSecret);

            builder.Services.AddSingleton<UserService>();

            if (authEnabled)
            {
                builder.Services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
                        options.DefaultChallengeScheme = "GitHub";
                    })
                    .AddCookie(options =>
                    {
                        options.LoginPath = "/login";
                        options.LogoutPath = "/logout";
                        options.ExpireTimeSpan = TimeSpan.FromDays(30);
                        options.Cookie.HttpOnly = true;
                    })
                    .AddGitHub("GitHub", options =>
                    {
                        options.ClientId = oauthClientId!;
                        options.ClientSecret = oauthClientSecret!;
                        options.CallbackPath = "/signin-github";
                        options.Scope.Add("read:user");
                        options.Scope.Add("copilot");
                        options.Scope.Add("workflow");
                        options.SaveTokens = true;

                        options.Events.OnCreatingTicket = async context =>
                        {
                            var userService = context.HttpContext.RequestServices.GetRequiredService<UserService>();
                            var ct = context.HttpContext.RequestAborted;

                            var githubId = context.Identity?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                ?? context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                ?? string.Empty;
                            var username = context.Identity?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                                ?? string.Empty;
                            var displayName = context.Identity?.FindFirst("urn:github:name")?.Value;
                            var avatarUrl = context.Identity?.FindFirst("urn:github:avatar")?.Value;
                            var email = context.Identity?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

                            var userCount = await userService.GetUserCountAsync(ct);
                            if (userCount == 0)
                            {
                                await userService.CreateOrUpdateUserAsync(
                                    githubId, username, displayName, avatarUrl, email,
                                    context.AccessToken ?? string.Empty, context.RefreshToken,
                                    context.ExpiresIn is { } exp
                                        ? DateTime.UtcNow.Add(exp).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                                        : null,
                                    ct);
                            }
                            else
                            {
                                var admin = await userService.GetAdminUserAsync(ct);
                                if (admin is not null && admin.GitHubId == githubId)
                                {
                                    await userService.CreateOrUpdateUserAsync(
                                        githubId, username, displayName, avatarUrl, email,
                                        context.AccessToken ?? string.Empty, context.RefreshToken,
                                        context.ExpiresIn is { } exp
                                            ? DateTime.UtcNow.Add(exp).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                                            : null,
                                        ct);
                                }
                                else
                                {
                                    context.Fail("Only one user (admin) is allowed in this version.");
                                }
                            }
                        };
                    });

                // Require authenticated users by default for every endpoint. Endpoints that
                // must stay open (health, login, logout, gRPC) opt out via .AllowAnonymous().
                // The fallback policy is only set when auth is enabled, so open mode remains
                // fully accessible for backward compatibility.
                builder.Services.AddAuthorization(options =>
                {
                    options.FallbackPolicy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .Build();
                });
                builder.Services.AddCascadingAuthenticationState();
            }

            // Composer agent (optional — enabled when config has a composer section or BRAIN_MODEL is set)
            // Registered BEFORE GoalDispatcher so the IClarificationRouter forwarding is available.
            builder.Services.AddSingleton(sp =>
            {
                var config = sp.GetService<HiveConfigFile>();
                var composerConfig = config?.Composer;

                // Model: composer-specific override → orchestrator model → env var
                var model = composerConfig?.Model;
                if (string.IsNullOrEmpty(model))
                    model = config?.Orchestrator.Model;
                if (string.IsNullOrEmpty(model))
                    model = brainModel ?? Constants.DefaultWorkerModel;

                var maxCtx = config?.TryGetContextWindowForModel(model)
                    ?? Constants.DefaultBrainContextWindow;
                var maxSteps = composerConfig?.MaxSteps ?? config?.Orchestrator.BrainMaxSteps ?? Constants.DefaultBrainMaxSteps;
                var availableModels = config?.GetComposerAvailableModels(model) ?? [model];

                return new Composer(model, sp.GetRequiredService<ILogger<Composer>>(),
                    sp.GetRequiredService<IGoalStore>(),
                    maxCtx, maxSteps,
                    sp.GetService<IBrainRepoManager>(),
                    stateDir,
                    sp, // IServiceProvider — lazy resolution of GoalDispatcher to avoid circular DI
                    !string.IsNullOrWhiteSpace(ollamaApiKey) ? sp.GetRequiredService<IHttpClientFactory>() : null,
                    ollamaApiKey,
                    sp.GetService<HiveConfigFile>(),
                    sp.GetService<ConfigRepoManager>(),
                    availableModels,
                    compactionModel: config?.Models?.CompactionModel,
                    knowledgeGraph: sp.GetService<KnowledgeGraph>(),
                    goalReviewService: sp.GetService<GoalReviewService>(),
                    sessionRegistry: sp.GetService<LlmSessionRegistry>(),
                    goalReadyNotifier: sp.GetService<GoalReadyNotifier>(),
                    attachmentService: sp.GetService<ComposerAttachmentService>(),
                    reasoningEffort: ParseConfiguredReasoningEffort(
                        // Composer-specific override → orchestrator reasoning effort, mirroring
                        // the model fallback chain above.
                        !string.IsNullOrWhiteSpace(composerConfig?.ReasoningEffort)
                            ? composerConfig.ReasoningEffort
                            : config?.Orchestrator?.ReasoningEffort,
                        "composer.reasoning_effort",
                        sp.GetService<ILogger<Composer>>()),
                    issueStore: sp.GetService<IIssueStore>(),
                    eventSubscriber: sp.GetService<ComposerEventSubscriber>(),
                    eventBus: sp.GetService<IEventBus>());
            });
            builder.Services.AddSingleton<IClarificationRouter>(sp => sp.GetRequiredService<Composer>());

            // Active event injector: registered via a factory with GetService so a missing
            // Composer, event bus, or config produces a disabled (no-op) instance instead of
            // crashing DI resolution. Resolved after the Composer connection block below.
            builder.Services.AddSingleton(sp => new ActiveEventInjector(
                sp.GetService<Composer>(),
                sp.GetService<IEventBus>(),
                sp.GetService<HiveConfigFile>(),
                sp.GetRequiredService<ILogger<ActiveEventInjector>>()));

            builder.Services.AddSingleton<GoalDispatcher>();
            // The GoalDispatcher hosted loop races endpoint tests that create/delete Pending goals.
            // Keep the singleton registered so dependent services resolve, but do not start the
            // background service in the Testing environment.
            if (!builder.Environment.IsEnvironment("Testing"))
            {
                builder.Services.AddHostedService(sp => sp.GetRequiredService<GoalDispatcher>());
            }

            builder.Services.AddSingleton<GoalReviewService>(sp => new GoalReviewService(
                sp.GetService<KnowledgeGraph>(),
                sp.GetService<ConfigRepoManager>(),
                sp.GetService<HiveConfigFile>(),
                sp.GetService<IGoalStore>(),
                sp.GetService<IBrainRepoManager>(),
                stateDir,
                sp.GetRequiredService<ILogger<GoalReviewService>>(),
                sessionRegistry: sp.GetService<LlmSessionRegistry>()));

            // Dashboard: log capture (registered early so logger provider can reference it)
            var dashboardLogSink = new DashboardLogSink();
            builder.Services.AddSingleton(dashboardLogSink);
            builder.Services.AddSingleton<ProgressLog>();
            builder.Services.AddSingleton<ProgressReportService>();
            builder.Logging.AddProvider(new DashboardLoggerProvider(dashboardLogSink));

            builder.Services.AddHostedService<StaleWorkerCleanupService>();

            // HTTP client for Ollama web research tools
            builder.Services.AddHttpClient("ollama-web", client =>
            {
                client.BaseAddress = new Uri("https://ollama.com/");
                client.Timeout = TimeSpan.FromSeconds(15);
            });

            // HTTP client for GitHub API (CI monitoring)
            builder.Services.AddHttpClient("github-api", client =>
            {
                client.BaseAddress = new Uri("https://api.github.com/");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                client.DefaultRequestHeaders.Add("User-Agent", "CopilotHive");
            });

            // HTTP client for GitHub Actions log download (CI monitoring). Redirects are
            // deliberately NOT followed: the log URL redirects to a signed storage endpoint,
            // and the redirect target must be captured rather than transparently followed.
            builder.Services.AddHttpClient("github-api-logs", client =>
            {
                client.BaseAddress = new Uri("https://api.github.com/");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                client.DefaultRequestHeaders.Add("User-Agent", "CopilotHive");
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });

            // CI monitoring: polls GitHub check-runs for completed goals' merge commits.
            builder.Services.AddSingleton(sp => new CiMonitorService(
                goalStore: sp.GetService<IGoalStore>(),
                issueStore: sp.GetService<IIssueStore>(),
                eventBus: sp.GetService<IEventBus>(),
                config: sp.GetService<HiveConfigFile>(),
                userService: sp.GetService<UserService>(),
                httpClientFactory: sp.GetService<IHttpClientFactory>(),
                logger: sp.GetService<ILogger<CiMonitorService>>()));

            // HTTP client for NuGet publish monitoring (gzip decompression for the
            // registration5-gz-semver2 endpoint).
            builder.Services.AddHttpClient("nuget-api", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            });

            // NuGet publish monitoring: polls the NuGet registration API to verify packages
            // have landed after a release. Service layer only — lifecycle integration is a
            // separate follow-up goal.
            builder.Services.AddSingleton<NuGetPublishMonitorService>();

            // Dashboard: Blazor Server + real-time state aggregation
            builder.Services.AddSingleton<DashboardNotifier>();
            builder.Services.AddSingleton<DashboardStateService>();
            builder.Services.AddScoped<PageHeaderState>();
            builder.Services.AddScoped<GoalsFilterState>();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            // HttpClient for Blazor Server components to call the local REST API
            builder.Services.AddScoped(_ => new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{port + 1}")
            });
            // Persist data protection keys so antiforgery tokens survive container restarts
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(stateDir, "keys")));

            if (!string.IsNullOrEmpty(configRepoUrl))
            {
                var configRepo = new ConfigRepoManager(configRepoUrl, configRepoPath);
                await configRepo.SyncRepoAsync();
                var hiveConfigFile = await configRepo.LoadConfigAsync();

                builder.Services.AddSingleton(configRepo);
                builder.Services.AddSingleton(hiveConfigFile);

                // Knowledge graph: load from config repo on startup
                var knowledgeGraph = new KnowledgeGraph(configRepo, null /* logger resolved later */);
                try
                {
                    await knowledgeGraph.ReloadFromConfigRepoAsync(configRepo.LocalPath);
                }
                catch (Exception)
                {
                    // Best-effort: graph starts empty if knowledge/ directory doesn't exist yet
                }
                builder.Services.AddSingleton(knowledgeGraph);
                builder.Services.AddSingleton<KnowledgeDocumentCleanupService>();

                builder.Services.AddSingleton<ConfigModelService>();
                builder.Services.AddSingleton<ModelDiscoveryService>();
                builder.Services.AddSingleton(sp => new ReleaseExecutionService(
                    sp.GetRequiredService<IGoalStore>(), hiveConfigFile,
                    sp.GetRequiredService<IBrainRepoManager>(),
                    sp.GetRequiredService<ILogger<ReleaseExecutionService>>()));

                // Startup sweep of stale progress/review knowledge documents.
                // Registered even when knowledgeGraph is null — the service handles
                // a null graph gracefully by returning 0 from all methods.
                builder.Services.AddSingleton(sp => new KnowledgeDocumentCleanupService(
                    knowledgeGraph, sp.GetRequiredService<ILogger<KnowledgeDocumentCleanupService>>()));

                // Enable debug logging if verbose_logging is set in config
                if (hiveConfigFile.Orchestrator.VerboseLogging)
                {
                    builder.Logging.SetMinimumLevel(LogLevel.Debug);
                    // Keep framework noise suppressed even in verbose mode
                    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
                    builder.Logging.AddFilter("Grpc", LogLevel.Warning);
                }
            }

            // Knowledge document cleanup: best-effort deletion of transient progress/review
            // docs for released goals. Registered unconditionally — the KnowledgeGraph is
            // resolved lazily and may be null when no config repo is configured.
            builder.Services.AddSingleton<KnowledgeDocumentCleanupService>(sp =>
                new KnowledgeDocumentCleanupService(
                    sp.GetService<KnowledgeGraph>(),
                    sp.GetRequiredService<ILogger<KnowledgeDocumentCleanupService>>()));

            // Goals: EF Core-backed goal store (primary source of truth)
            builder.Services.AddSingleton<IGoalStore>(sp =>
                new GoalStore(
                    sp.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>(),
                    sp.GetRequiredService<ILogger<GoalStore>>(),
                    sp.GetRequiredService<PipelineStore>(),
                    dbPath));

            // Issues: EF Core-backed issue store
            builder.Services.AddSingleton<IIssueStore>(sp =>
                new IssueStore(
                    sp.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>(),
                    sp.GetRequiredService<ILogger<IssueStore>>()));

            builder.Services.AddSingleton(sp =>
            {
                var manager = new GoalManager();
                manager.AddSource(sp.GetRequiredService<IGoalStore>());
                return manager;
            });

            builder.WebHost.ConfigureKestrel(options =>
            {
                // HTTP/2 only for gRPC (required without TLS — prior knowledge mode)
                options.ListenAnyIP(port, listenOptions =>
                    listenOptions.Protocols = HttpProtocols.Http2);

                // HTTP/1.1 for health checks and REST API
                options.ListenAnyIP(port + 1, listenOptions =>
                    listenOptions.Protocols = HttpProtocols.Http1);
            });

            var app = builder.Build();

            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Starting gRPC server on port {GrpcPort}, HTTP on port {HttpPort}", port, port + 1);

            var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

            try
            {
                var dbContextFactory = app.Services.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
                await using var dbContext = dbContextFactory.CreateDbContext();
                DatabaseMigration.EnsureSchemaUpToDate(dbContext, logger);
                logger.LogInformation("Database schema reconciliation completed");
                await dbContext.Database.MigrateAsync();
                logger.LogInformation("EF Core migrations applied");

                await dbContext.Database.OpenConnectionAsync();
                try
                {
                    using var walCmd = dbContext.Database.GetDbConnection().CreateCommand();
                    walCmd.CommandText = "PRAGMA journal_mode = WAL;";
                    var result = await walCmd.ExecuteScalarAsync();
                    if (result is string mode && mode.Equals("wal", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation("SQLite WAL mode enabled");
                    }
                    else
                    {
                        logger.LogWarning("SQLite WAL mode not enabled: PRAGMA returned {Mode}", result);
                    }
                }
                catch (Exception walEx)
                {
                    logger.LogWarning(walEx, "Failed to enable WAL mode; continuing startup");
                }
                finally
                {
                    await dbContext.Database.CloseConnectionAsync();
                }
            }
            catch (DbUpdateConcurrencyException ex)
            {
                logger.LogError(ex, "DbUpdateConcurrencyException during schema reconciliation; continuing startup");
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Schema mismatch between EF and raw SQL stores during reconciliation; continuing startup");
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "EnsureCreated skipped due to DI scope issue; non-fatal");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected exception during CopilotHiveDbContext.EnsureCreated; continuing startup");
            }

            // Wire up the ChatClientFactory token provider BEFORE any chat clients are created
            // (Brain/Composer connect below and instantiate Copilot clients). The Copilot client
            // uses the OAuth access token stored in the database, falling back to GH_TOKEN/GITHUB_TOKEN
            // when no user has authenticated yet. Done regardless of authEnabled — the provider
            // returns null when no users exist.
            var userService = app.Services.GetRequiredService<UserService>();
            SharpCoder.Providers.ChatClientFactory.SetTokenProvider(() =>
                userService.GetActiveAccessTokenAsync(CancellationToken.None).GetAwaiter().GetResult());

            if (!string.IsNullOrEmpty(brainModel))
                logger.LogInformation("Brain enabled — model: {BrainModel}", brainModel);
            else
                logger.LogWarning("Brain disabled — running in mechanical mode (no BRAIN_MODEL set)");

            if (!string.IsNullOrEmpty(configRepoUrl))
            {
                var configRepo = app.Services.GetService<ConfigRepoManager>();
                var hiveConfigFile = app.Services.GetService<HiveConfigFile>();
                logger.LogInformation("Synced config repo from {ConfigRepoUrl}", configRepoUrl);
                if (hiveConfigFile is not null)
                {
                    logger.LogInformation(
                        "Config loaded: {RepoCount} repo(s), {WorkerConfigCount} worker config(s)",
                        hiveConfigFile.Repositories.Count, hiveConfigFile.Workers.Count);
                    if (hiveConfigFile.Orchestrator.VerboseLogging)
                        logger.LogDebug("Verbose logging enabled (Debug level)");

                    // Startup-only validation: every model assignment must carry an explicit,
                    // valid reasoning effort. Dynamic reloads (DispatcherMaintenance.ReloadFrom)
                    // intentionally do not re-validate.
                    var reasoningErrors = hiveConfigFile.ValidateReasoningEffort();
                    if (reasoningErrors.Count > 0)
                    {
                        foreach (var error in reasoningErrors)
                            await Console.Error.WriteLineAsync($"Config validation error — {error}");

                        throw new InvalidOperationException(
                            "Invalid hive configuration: reasoning effort validation failed:"
                            + Environment.NewLine
                            + string.Join(Environment.NewLine, reasoningErrors));
                    }
                }

                // Startup sweep: remove stale progress/review knowledge documents whose
                // owning goal no longer exists or belongs to a released release.
                // The helper is best-effort — it never throws, so startup is never blocked.
                await KnowledgeDocumentCleanupService.ExecuteStartupSweepAsync(
                    app.Services, logger, CancellationToken.None);
            }

            // Wire up Brain and completion event
            var brain = app.Services.GetService<IDistributedBrain>();
            if (brain is not null)
            {
                logger.LogInformation("Connecting Brain…");
                await brain.ConnectAsync();
                logger.LogInformation("Brain connected.");
            }

            // Wire up Composer
            // Force construction of the event subscriber so its subscription is active
            // before any goal lifecycle events can be published.
            app.Services.GetService<ComposerEventSubscriber>();

            // CI monitoring startup scan: reconciles CI state for goals merged while the
            // orchestrator was down. Fire-and-forget so startup is never blocked, and bound
            // to the application lifetime so shutdown stops it.
            var ciMonitor = app.Services.GetService<CiMonitorService>();
            if (ciMonitor is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await ciMonitor.StartupScanAsync(appLifetime.ApplicationStopping); }
                    catch (Exception ex) { logger.LogWarning(ex, "CI monitor startup scan failed"); }
                }, appLifetime.ApplicationStopping);
            }

            var composer = app.Services.GetService<Composer>();
            if (composer is not null)
            {
                try
                {
                    logger.LogInformation("Connecting Composer…");
                    await composer.ConnectAsync();
                    logger.LogInformation("Composer connected.");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Composer failed to connect — chat will be unavailable");
                }
            }

            // Active event injector: resolved after the Composer connection block so the
            // Composer's actor is ready to accept injected notifications. The factory uses
            // GetService for every dependency — the injector must be a no-op (disabled)
            // when any of them is missing rather than crashing startup.
            app.Services.GetService<ActiveEventInjector>();

            // Event bus startup scan: reconstructs events for state changes that happened
            // while the orchestrator was down (goals completed/failed, issues raised/resolved,
            // releases completed) so the Composer is aware of them. Fire-and-forget so startup
            // is never blocked, and bound to the application lifetime so shutdown stops it.
            // The scan runs regardless of Composer connection state. Skipped in the Testing
            // environment so integration tests are not polluted by reconstructed events.
            var startupScanner = app.Services.GetService<EventBusStartupScanner>();
            if (startupScanner is not null && !app.Environment.IsEnvironment("Testing"))
            {
                _ = Task.Run(async () =>
                {
                    try { await startupScanner.ScanAsync(appLifetime.ApplicationStopping); }
                    catch (Exception ex) { logger.LogWarning(ex, "Event bus startup scan failed"); }
                }, appLifetime.ApplicationStopping);
            }

            // NuGet publish monitoring startup scan: resumes background monitors for releases
            // marked Released while the orchestrator was down. Fire-and-forget so startup is
            // never blocked, and bound to the application lifetime so shutdown stops it.
            var nugetMonitor = app.Services.GetService<NuGetPublishMonitorService>();
            if (nugetMonitor is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await nugetMonitor.StartupScanAsync(appLifetime.ApplicationStopping); }
                    catch (Exception ex) { logger.LogWarning(ex, "NuGet publish monitor startup scan failed"); }
                }, appLifetime.ApplicationStopping);
            }

            // Composer model-management REST API
            app.MapComposerEndpoints(composer, app.Services.GetService<HiveConfigFile>());

            // Model configuration REST API
            app.MapConfigEndpoints();

            // Eager clone all configured repos at startup
            var repoManager = app.Services.GetService<IBrainRepoManager>();
            var hiveConfig = app.Services.GetService<HiveConfigFile>();
            if (repoManager is not null && hiveConfig is not null)
            {
                foreach (var repo in hiveConfig.Repositories)
                {
                    try
                    {
                        var url = PipelineHelpers.InjectTokenIntoUrl(repo.Url);
                        await repoManager.EnsureCloneAsync(repo.Name, url, repo.DefaultBranch);
                        logger.LogInformation("Cloned/updated repo '{RepoName}' at startup", repo.Name);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to clone repo '{RepoName}' at startup", repo.Name);
                    }
                }
            }

            app.MapGrpcService<HiveOrchestratorService>().AllowAnonymous();

            // Dashboard: Blazor Server (antiforgery keys persisted to state volume)
            // Static files are intentionally placed before auth/authorization middleware
            // so the fallback authorization policy does not challenge non-endpoint static
            // asset requests (css, js, _framework, favicon, etc.).
            app.UseStaticFiles();
            if (authEnabled)
            {
                app.UseAuthentication();
                app.UseAuthorization();
            }
            app.UseAntiforgery();
            app.MapRazorComponents<CopilotHive.Components.App>()
                .AddInteractiveServerRenderMode();
            var _serverStartTime = DateTime.UtcNow;
            var _version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";

            app.MapHealthEndpoints(_serverStartTime, _version);
            app.MapGoalEndpoints();
            app.MapReleaseEndpoints();
            app.MapIssueEndpoints();
            app.MapClarificationEndpoints();
            app.MapSessionEndpoints();
            app.MapBackupEndpoints();

            await app.RunAsync();
            return 0;
        }

        // Parses a configured reasoning effort leniently: an unrecognised value degrades to
        // null (unset) instead of throwing. Startup validation
        // (HiveConfigFile.ValidateReasoningEffort) is the authority for rejecting bad values;
        // these DI factories resolve lazily and must never crash the host — or a later dynamic
        // config reload — over an invalid string.
        static Microsoft.Extensions.AI.ReasoningEffort? ParseConfiguredReasoningEffort(
            string? value, string field, ILogger? logger)
        {
            try
            {
                return ReasoningEffortConverter.Parse(value);
            }
            catch (ArgumentException)
            {
                logger?.LogWarning(
                    "Invalid {Field} '{Effort}' in configuration; using reasoning effort unset.",
                    field, value);
                return null;
            }
        }

        static void PrintBanner()
        {
            try
            {
                Console.WriteLine("""

         ██████╗ ██████╗ ██████╗ ██╗██╗      ██████╗ ████████╗
        ██╔════╝██╔═══██╗██╔══██╗██║██║     ██╔═══██╗╚══██╔══╝
        ██║     ██║   ██║██████╔╝██║██║     ██║   ██║   ██║
        ██║     ██║   ██║██╔═══╝ ██║██║     ██║   ██║   ██║
        ╚██████╗╚██████╔╝██║     ██║███████╗╚██████╔╝   ██║
         ╚═════╝ ╚═════╝ ╚═╝     ╚═╝╚══════╝ ╚═════╝    ╚═╝
                             ██╗  ██╗██╗██╗   ██╗███████╗
                             ██║  ██║██║██║   ██║██╔════╝
                             ███████║██║██║   ██║█████╗
                             ██╔══██║██║╚██╗ ██╔╝██╔══╝
                             ██║  ██║██║ ╚████╔╝ ███████╗
                             ╚═╝  ╚═╝╚═╝  ╚═══╝  ╚══════╝
        """);
                var version = VersionHelper.InformationalVersion;
                Console.WriteLine($"CopilotHive v{version}");
                Console.WriteLine($"Started at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            }
            catch (ObjectDisposedException)
            {
                // Console.Out may be closed by the test runner during WebApplicationFactory
                // host creation. Banner output is cosmetic — never crash startup if the
                // console is unavailable.
            }
        }
    }
}
