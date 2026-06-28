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

namespace CopilotHive;

/// <summary>
/// Main Program.
/// </summary>
public sealed class Program
{
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

            var goalsFile = args.FirstOrDefault(a => a.StartsWith("--goals-file="))?["--goals-file=".Length..];
            var configRepoUrl = args.FirstOrDefault(a => a.StartsWith("--config-repo="))?["--config-repo=".Length..];
            var configRepoPath = args.FirstOrDefault(a => a.StartsWith("--config-repo-path="))?["--config-repo-path=".Length..]
                ?? "./config-repo";

            PrintBanner();

            var builder = WebApplication.CreateBuilder(args);

            // Suppress noisy health-check request logs and framework noise
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);

            builder.Services.AddGrpc();
            builder.Services.AddSingleton<WorkerPool>();
            builder.Services.AddSingleton<IWorkerPool>(sp => sp.GetRequiredService<WorkerPool>());
            builder.Services.AddSingleton<GrpcWorkerGateway>();
            builder.Services.AddSingleton<IWorkerGateway>(sp => sp.GetRequiredService<GrpcWorkerGateway>());
            builder.Services.AddSingleton<TaskQueue>();
            builder.Services.AddSingleton<TaskCompletionNotifier>();
            builder.Services.AddSingleton<ImprovementAnalyzer>();

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

            builder.Services.AddDbContext<CopilotHiveDbContext>(options =>
                options.UseSqlite($"Data Source={dbPath}"));

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
                    // Config file model takes precedence over env var default (includes reasoning suffix)
                    var effectiveModel = !string.IsNullOrEmpty(config?.Orchestrator.Model)
                        ? config.Orchestrator.Model
                        : brainModel;
                    var maxCtx = int.TryParse(brainContextWindowEnv, out var envCtx)
                        ? envCtx
                        : config?.TryGetContextWindowForModel(effectiveModel)
                        ?? config?.Orchestrator.BrainContextWindow
                        ?? Constants.DefaultBrainContextWindow;
                    var maxSteps = int.TryParse(brainMaxStepsEnv, out var envSteps)
                        ? envSteps
                        : config?.Orchestrator.BrainMaxSteps ?? Constants.DefaultBrainMaxSteps;

                    // Apply configured reasoning effort as a model suffix (explicit :suffix takes precedence)
                    var reasoningEffort = config?.TryGetReasoningEffortForModel(effectiveModel);
                    effectiveModel = HiveConfigFile.ApplyReasoningSuffix(effectiveModel, reasoningEffort);

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
                        hiveConfig: config);
                });
            }

            builder.Services.AddSingleton<WorkerUtilizationService>();
            builder.Services.AddSingleton<ClarificationQueueService>();

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
                    ?? composerConfig?.ContextWindow
                    ?? config?.Orchestrator.BrainContextWindow
                    ?? Constants.DefaultBrainContextWindow;
                var maxSteps = composerConfig?.MaxSteps ?? config?.Orchestrator.BrainMaxSteps ?? Constants.DefaultBrainMaxSteps;
                var availableModels = config?.GetComposerAvailableModels(model) ?? [model];

                // Apply configured reasoning effort as a model suffix (explicit :suffix takes precedence)
                var composerReasoningEffort = config?.TryGetReasoningEffortForModel(model);
                model = HiveConfigFile.ApplyReasoningSuffix(model, composerReasoningEffort);

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
                    knowledgeGraph: sp.GetService<KnowledgeGraph>());
            });
            builder.Services.AddSingleton<IClarificationRouter>(sp => sp.GetRequiredService<Composer>());

            builder.Services.AddSingleton<GoalDispatcher>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<GoalDispatcher>());

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

            // Dashboard: Blazor Server + real-time state aggregation
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

                builder.Services.AddSingleton<ConfigModelService>();
                builder.Services.AddSingleton<ModelDiscoveryService>();

                // If no explicit goals file, check config repo for goals.yaml
                if (string.IsNullOrEmpty(goalsFile))
                {
                    var configGoalsFile = Path.Combine(configRepo.LocalPath, "goals.yaml");
                    if (File.Exists(configGoalsFile))
                        goalsFile = configGoalsFile;
                }

                // Enable debug logging if verbose_logging is set in config
                if (hiveConfigFile.Orchestrator.VerboseLogging)
                {
                    builder.Logging.SetMinimumLevel(LogLevel.Debug);
                    // Keep framework noise suppressed even in verbose mode
                    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
                    builder.Logging.AddFilter("Grpc", LogLevel.Warning);
                }
            }
            // Goals: EF Core-backed goal store (primary source of truth)
            builder.Services.AddSingleton<IGoalStore>(sp =>
                new GoalStore(
                    sp.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>(),
                    sp.GetRequiredService<ILogger<GoalStore>>(),
                    sp.GetRequiredService<PipelineStore>(),
                    dbPath));

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

            try
            {
                using var scope = app.Services.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<CopilotHiveDbContext>();
                DatabaseMigration.EnsureSchemaUpToDate(dbContext, logger);
                logger.LogInformation("Database schema reconciliation completed");
                await dbContext.Database.MigrateAsync();
                logger.LogInformation("EF Core migrations applied");
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
            CopilotHive.Shared.AI.ChatClientFactory.SetTokenProvider(() =>
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
                    if (!string.IsNullOrEmpty(goalsFile))
                        logger.LogInformation("Using goals file from config repo: {GoalsFile}", goalsFile);
                    logger.LogInformation(
                        "Config loaded: {RepoCount} repo(s), {WorkerConfigCount} worker config(s)",
                        hiveConfigFile.Repositories.Count, hiveConfigFile.Workers.Count);
                    if (hiveConfigFile.Orchestrator.VerboseLogging)
                        logger.LogDebug("Verbose logging enabled (Debug level)");

                    // One-time migration: split reasoning suffixes (e.g. ":high") out of
                    // available model names into the ReasoningEffort field. Idempotent —
                    // after the first run names no longer carry a known suffix.
                    var knownReasoningLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "none", "low", "medium", "high", "extra_high"
                    };
                    var corrected = 0;
                    foreach (var entry in hiveConfigFile.Models?.AvailableModels ?? [])
                    {
                        var entryName = entry.Name;
                        if (string.IsNullOrEmpty(entryName))
                            continue;
                        var lastColon = entryName.LastIndexOf(':');
                        if (lastColon > 0 && lastColon < entryName.Length - 1)
                        {
                            var suffix = entryName.Substring(lastColon + 1);
                            if (knownReasoningLevels.Contains(suffix))
                            {
                                entry.Name = entryName.Substring(0, lastColon);
                                if (string.IsNullOrEmpty(entry.ReasoningEffort))
                                    entry.ReasoningEffort = suffix;
                                corrected++;
                            }
                        }
                    }
                    if (corrected > 0 && configRepo is not null)
                    {
                        logger.LogInformation(
                            "Migrating {Count} available model(s): stripped reasoning suffix into ReasoningEffort field",
                            corrected);
                        await configRepo.WriteConfigAsync(hiveConfigFile);
                        await configRepo.CommitFileAsync(
                            "hive-config.yaml",
                            "chore: migrate reasoning suffixes out of available model names");
                    }
                }
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

            // Bootstrap: import goals from YAML file into SQLite (one-time migration)
            if (!string.IsNullOrEmpty(goalsFile) && File.Exists(goalsFile))
            {
                var goalStore = app.Services.GetRequiredService<IGoalStore>();
                var fileSource = new FileGoalSource(goalsFile);
                var yamlGoals = await fileSource.ReadGoalsAsync();
                if (yamlGoals.Count > 0)
                {
                    var imported = await goalStore.ImportGoalsAsync(yamlGoals);
                    if (imported > 0)
                        logger.LogInformation("Imported {Count} goals from {GoalsFile} into SQLite", imported, goalsFile);
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
            app.MapClarificationEndpoints();
app.MapBackupEndpoints();

            await app.RunAsync();
            return 0;
        }

        static void PrintBanner()
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
    }
}
