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
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using System.Globalization;
using System.Reflection;
using System.Text.Json;

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
                EnsureSchemaUpToDate(dbContext, logger);
                logger.LogInformation("Database schema reconciliation completed");
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

            // ── Data migration: goals.db → copilothive.db ────────────────────
            try
            {
                var legacyGoalsDbPath = Path.Combine(stateDir, "goals.db");
                if (File.Exists(legacyGoalsDbPath))
                {
                    using var scope = app.Services.CreateScope();
                    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
                    MigrateGoalsDatabase(legacyGoalsDbPath, dbContextFactory, logger);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to migrate data from goals.db to copilothive.db — continuing startup. Data can be manually migrated later.");
            }

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

            app.MapGrpcService<HiveOrchestratorService>();

            // Dashboard: Blazor Server (antiforgery keys persisted to state volume)
            app.UseStaticFiles();
            app.UseAntiforgery();
            app.MapRazorComponents<CopilotHive.Components.App>()
                .AddInteractiveServerRenderMode();
            var _serverStartTime = DateTime.UtcNow;
            var _checkCount = 0;
            var _version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";
            app.MapGet("/health", async (IGoalStore goalStore, WorkerPool workerPool) =>
            {
                var count = Interlocked.Increment(ref _checkCount);
                var uptime = DateTime.UtcNow - _serverStartTime;
                var goals = await goalStore.GetAllGoalsAsync();
                return Results.Ok(new HealthResponse
                {
                    Status = "Healthy",
                    Uptime = $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}",
                    UptimeSpan = uptime,
                    ActiveGoals = goals.Count(g => g.Status is GoalStatus.Pending or GoalStatus.InProgress),
                    CompletedGoals = goals.Count(g => g.Status == GoalStatus.Completed),
                    ConnectedWorkers = workerPool.GetAllWorkers().Count,
                    Version = _version,
                    SharpCoderVersion = typeof(SharpCoder.CodingAgent).Assembly.GetName().Version?.ToString(),
                    ServerTime = DateTime.UtcNow,
                    CheckNumber = count,
                    WorkerPool = workerPool.GetDetailedStats(),
                });
            });

            app.MapGet("/health/utilization", (WorkerUtilizationService svc) => Results.Ok(svc.GetUtilization()));

            // ── Goals REST API ───────────────────────────────────────────────────────
            var goalsApi = app.MapGroup("/api/goals");

            goalsApi.MapGet("/", async (IGoalStore store) =>
                Results.Ok(await store.GetAllGoalsAsync()));

            goalsApi.MapGet("/{id}", async (string id, IGoalStore store) =>
            {
                var goal = await store.GetGoalAsync(id);
                return goal is null ? Results.NotFound(new { error = $"Goal '{id}' not found." }) : Results.Ok(goal);
            });

            goalsApi.MapPost("/", async (Goal goal, IGoalStore store) =>
            {
                try
                {
                    var created = await store.CreateGoalAsync(goal);
                    return Results.Created($"/api/goals/{created.Id}", created);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (duplicate primary key)
                {
                    return Results.Conflict(new { error = $"Goal '{goal.Id}' already exists." });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }
            });

            goalsApi.MapPatch("/{id}/status", async (string id, GoalStatusUpdate update, IGoalStore store,
                IBrainRepoManager? repoManager, GoalDispatcher? dispatcher, ILogger<Program> endpointLogger) =>
            {
                try
                {
                    var status = Enum.Parse<GoalStatus>(update.Status, ignoreCase: true);

                    var existing = await store.GetGoalAsync(id);
                    if (existing is null)
                        return Results.NotFound(new { error = $"Goal '{id}' not found." });

                    // Allowed transitions via the public API:
                    //   Draft ↔ Pending (approve / revert)
                    //   Failed → Draft (retry — resets iteration data and cleans up feature branch)
                    var validTransition =
                        existing.Status == GoalStatus.Draft && status == GoalStatus.Pending ||
                        existing.Status == GoalStatus.Pending && status == GoalStatus.Draft ||
                        existing.Status == GoalStatus.Failed && status == GoalStatus.Draft;
                    if (!validTransition)
                        return Results.BadRequest(new { error = $"Invalid transition from {existing.Status} to {status}. Only Draft→Pending, Pending→Draft, and Failed→Draft are allowed." });

                    // Failed→Draft: reset iteration data and delete the feature branch (best-effort)
                    if (existing.Status == GoalStatus.Failed && status == GoalStatus.Draft)
                    {
                        await store.ResetGoalIterationDataAsync(id);

                        // Clear GoalDispatcher runtime state so the goal can be re-dispatched fresh
                        dispatcher?.ClearGoalRetryState(id);

                        if (repoManager is not null)
                        {
                            var branchName = $"copilothive/{id}";
                            foreach (var repoName in existing.RepositoryNames)
                            {
                                _ = await repoManager.DeleteRemoteBranchAsync(repoName, branchName);
                            }
                        }
                    }

                    await store.UpdateGoalStatusAsync(id, status);
                    var goal = await store.GetGoalAsync(id);
                    return Results.Ok(goal);
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound(new { error = $"Goal '{id}' not found." });
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { error = $"Invalid status '{update.Status}'." });
                }
            });

            goalsApi.MapDelete("/{id}", async (string id, IGoalStore store, IBrainRepoManager? repoManager, ILogger<Program> logger) =>
            {
                var goal = await store.GetGoalAsync(id);
                if (goal is null)
                    return Results.NotFound(new { error = $"Goal '{id}' not found." });

                if (goal.Status is not (GoalStatus.Draft or GoalStatus.Failed))
                    return Results.BadRequest(new { error = "Only Draft or Failed goals can be deleted" });

                var deleted = await store.DeleteGoalAsync(id);
                if (!deleted)
                    return Results.NotFound(new { error = $"Goal '{id}' not found." });

                // Best-effort cleanup of remote feature branches for Failed goals
                if (goal.Status == GoalStatus.Failed)
                {
                    if (repoManager is not null)
                    {
                        var branchName = $"copilothive/{id}";
                        foreach (var repoName in goal.RepositoryNames)
                        {
                            _ = await repoManager.DeleteRemoteBranchAsync(repoName, branchName);
                        }
                    }
                }

                return Results.NoContent();
            });

            goalsApi.MapPost("/{id}/cancel", async (string id, GoalDispatcher dispatcher, IGoalStore store) =>
            {
                var goal = await store.GetGoalAsync(id);
                if (goal is null)
                    return Results.NotFound(new { error = $"Goal '{id}' not found." });

                if (goal.Status is not (GoalStatus.InProgress or GoalStatus.Pending))
                    return Results.BadRequest(new { error = $"Goal '{id}' is {goal.Status} and cannot be cancelled. Only InProgress or Pending goals can be cancelled." });

                var cancelled = await dispatcher.CancelGoalAsync(id);
                return cancelled
                    ? Results.Ok(new { message = $"Goal '{id}' has been cancelled." })
                    : Results.BadRequest(new { error = $"Goal '{id}' could not be cancelled (it may have already completed or failed)." });
            });

            goalsApi.MapGet("/search", async (string q, string? status, IGoalStore store) =>
            {
                GoalStatus? statusFilter = null;
                if (!string.IsNullOrEmpty(status) && Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var s))
                    statusFilter = s;
                var results = await store.SearchGoalsAsync(q, statusFilter);
                return Results.Ok(results);
            });

            // ── Releases REST API ────────────────────────────────────────────────────
            var releasesApi = app.MapGroup("/api/releases");

            releasesApi.MapPost("/", async (CreateReleaseRequest request, IGoalStore store) =>
            {
                if (string.IsNullOrWhiteSpace(request.Version))
                    return Results.BadRequest(new { error = "Version is required." });

                var release = new Release
                {
                    Id = request.Version,
                    Tag = request.Version,
                    RepositoryNames = string.IsNullOrEmpty(request.Repository) ? [] : [request.Repository],
                };

                try
                {
                    var created = await store.CreateReleaseAsync(release);
                    return Results.Created($"/api/releases/{created.Id}", created);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }
            });

            releasesApi.MapPatch("/{id}/status", async (string id, UpdateReleaseStatusRequest request, IGoalStore store) =>
            {
                var existing = await store.GetReleaseAsync(id);
                if (existing is null)
                    return Results.NotFound(new { error = $"Release '{id}' not found." });

                if (string.IsNullOrEmpty(request.Status))
                    return Results.BadRequest(new { error = "Status is required." });

                if (!Enum.TryParse<ReleaseStatus>(request.Status, ignoreCase: true, out var newStatus))
                    return Results.BadRequest(new { error = $"Invalid status '{request.Status}'." });

                existing.Status = newStatus;
                if (newStatus == ReleaseStatus.Released && existing.ReleasedAt is null)
                    existing.ReleasedAt = DateTime.UtcNow;

                await store.UpdateReleaseAsync(existing);
                return Results.Ok(existing);
            });

            releasesApi.MapPatch("/{id}/notes", async (string id, UpdateReleaseNotesRequest request, IGoalStore store) =>
            {
                var existing = await store.GetReleaseAsync(id);
                if (existing is null)
                    return Results.NotFound(new { error = $"Release '{id}' not found." });

                existing.Notes = request.Notes;
                await store.UpdateReleaseAsync(existing);
                return Results.Ok(existing);
            });

            releasesApi.MapPatch("/{id}/tag", async (string id, UpdateReleaseTagRequest request, IGoalStore store) =>
            {
                if (string.IsNullOrWhiteSpace(request.Tag))
                    return Results.BadRequest(new { error = "Tag is required." });

                try
                {
                    await store.UpdateReleaseAsync(id, new ReleaseUpdateData { Tag = request.Tag.Trim() });
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound(new { error = $"Release '{id}' not found." });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                var updated = await store.GetReleaseAsync(id);
                return Results.Ok(updated);
            });

            releasesApi.MapPatch("/{id}/repositories", async (string id, UpdateReleaseRepositoriesRequest request, IGoalStore store) =>
            {
                try
                {
                    await store.UpdateReleaseAsync(id, new ReleaseUpdateData { Repositories = request.Repositories });
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound(new { error = $"Release '{id}' not found." });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                var updated = await store.GetReleaseAsync(id);
                return Results.Ok(updated);
            });

            goalsApi.MapPatch("/{id}/release", async (string id, AssignGoalReleaseRequest request, IGoalStore store) =>
            {
                var release = await store.GetReleaseAsync(request.ReleaseId);
                if (release is null)
                    return Results.NotFound(new { error = $"Release '{request.ReleaseId}' not found." });

                var goal = await store.GetGoalAsync(id);
                if (goal is null)
                    return Results.NotFound(new { error = $"Goal '{id}' not found." });

                goal.ReleaseId = request.ReleaseId;
                await store.UpdateGoalAsync(goal);
                return Results.Ok(goal);
            });

            // ── Clarifications REST API ──────────────────────────────────────────────
            var clarificationsApi = app.MapGroup("/api/clarifications");

            clarificationsApi.MapGet("/", (ClarificationQueueService queue) =>
                Results.Ok(queue.GetAllRequests()));

            clarificationsApi.MapGet("/pending", (ClarificationQueueService queue) =>
                Results.Ok(queue.GetPendingHumanRequests()));

            clarificationsApi.MapGet("/count", (ClarificationQueueService queue) =>
                Results.Ok(new { count = queue.PendingHumanCount }));

            clarificationsApi.MapPost("/{id}/answer", (string id, SubmitClarificationRequest body, ClarificationQueueService queue) =>
            {
                if (string.IsNullOrWhiteSpace(body.Answer))
                    return Results.BadRequest(new { error = "Answer is required." });

                var answered = queue.SubmitAnswer(id, body.Answer, "human");
                if (!answered)
                    return Results.NotFound(new { error = $"Clarification '{id}' not found." });

                return Results.Ok(new { message = $"Answer submitted for clarification '{id}'." });
            });

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

    private static readonly JsonSerializerOptions MigrationJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Reconciles the database schema with the EF Core model by creating any missing tables and
    /// indexes. Unlike <c>EnsureCreated()</c>, which only creates the schema when the database
    /// file does not yet exist, this method inspects the existing database and adds only the
    /// tables and indexes that are missing — making it safe to run against databases created by
    /// older versions of the code.
    /// </summary>
    /// <param name="dbContext">The EF Core DbContext whose model defines the target schema.</param>
    /// <param name="logger">Logger for reporting created tables and indexes.</param>
    internal static void EnsureSchemaUpToDate(CopilotHiveDbContext dbContext, ILogger logger)
    {
        // Full DDL EF Core would use to create the entire schema (tables, indexes, FK constraints).
        var createScript = dbContext.Database.GenerateCreateScript();

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
            openedHere = true;
        }

        try
        {
            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    existingTables.Add(reader.GetString(0));
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    existingIndexes.Add(reader.GetString(0));
            }

            // Split the generated script into individual statements. Statements are executed via a
            // raw DbCommand (NOT ExecuteSqlRaw) so that literal braces in DDL default values such as
            // '{}' are not misinterpreted as format placeholders.
            var statements = createScript.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawStatement in statements)
            {
                var statement = rawStatement.Trim();
                if (statement.Length == 0)
                    continue;

                if (statement.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    var tableName = ExtractBracketedName(statement, "CREATE TABLE");
                    if (tableName is not null && existingTables.Contains(tableName))
                        continue;

                    logger.LogInformation("Creating missing table {TableName}", tableName ?? "<unknown>");
                    ExecuteRaw(connection, statement);
                }
                else if (statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase) ||
                         statement.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase))
                {
                    var prefix = statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase)
                        ? "CREATE UNIQUE INDEX"
                        : "CREATE INDEX";
                    var indexName = ExtractBracketedName(statement, prefix);
                    if (indexName is not null && existingIndexes.Contains(indexName))
                        continue;

                    logger.LogInformation("Creating missing index {IndexName}", indexName ?? "<unknown>");
                    ExecuteRaw(connection, statement);
                }
                else
                {
                    ExecuteRaw(connection, statement);
                }
            }
        }
        finally
        {
            if (openedHere)
                connection.Close();
        }
    }

    /// <summary>
    /// Executes a single DDL statement directly against the open connection using a raw
    /// <see cref="System.Data.Common.DbCommand"/>, avoiding EF Core's parameter parsing.
    /// </summary>
    private static void ExecuteRaw(System.Data.Common.DbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Extracts the first quoted identifier following the given DDL prefix. EF Core's create
    /// script may quote identifiers with brackets (<c>[goals]</c>) or double quotes
    /// (<c>"goals"</c>) depending on the provider; both forms are supported here.
    /// Returns the unquoted name, or <c>null</c> if no quoted identifier is found.
    /// </summary>
    private static string? ExtractBracketedName(string statement, string prefix)
    {
        var afterPrefix = statement.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        var searchStart = afterPrefix >= 0 ? afterPrefix + prefix.Length : 0;

        // Find the first opening quote of either supported style.
        var bracketOpen = statement.IndexOf('[', searchStart);
        var quoteOpen = statement.IndexOf('"', searchStart);

        var useBracket = bracketOpen >= 0 && (quoteOpen < 0 || bracketOpen < quoteOpen);
        if (useBracket)
        {
            var close = statement.IndexOf(']', bracketOpen + 1);
            if (close < 0)
                return null;
            return statement.Substring(bracketOpen + 1, close - bracketOpen - 1);
        }

        if (quoteOpen >= 0)
        {
            var close = statement.IndexOf('"', quoteOpen + 1);
            if (close < 0)
                return null;
            return statement.Substring(quoteOpen + 1, close - quoteOpen - 1);
        }

        return null;
    }

    /// <summary>
    /// Migrates goals, iteration summaries, and releases from the legacy <c>goals.db</c> database
    /// into the EF Core-managed <c>copilothive.db</c> database. After a successful migration the
    /// legacy file is renamed to <c>goals.db.migrated</c> so the migration runs only once.
    /// </summary>
    /// <param name="oldDbPath">Path to the legacy goals.db SQLite database.</param>
    /// <param name="dbContextFactory">Factory for creating the target <see cref="CopilotHiveDbContext"/>.</param>
    /// <param name="logger">Logger for reporting migration progress.</param>
    private static void MigrateGoalsDatabase(
        string oldDbPath,
        IDbContextFactory<CopilotHiveDbContext> dbContextFactory,
        ILogger logger)
    {
        var goals = new List<Goal>();
        var iterations = new List<IterationSummaryEntity>();
        var releases = new List<Release>();

        using (var conn = new SqliteConnection($"Data Source={oldDbPath}"))
        {
            conn.Open();

            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var tablesCmd = conn.CreateCommand())
            {
                tablesCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var tablesReader = tablesCmd.ExecuteReader();
                while (tablesReader.Read())
                    existingTables.Add(tablesReader.GetString(0));
            }

            if (existingTables.Contains("goals"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM goals";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    goals.Add(ReadMigrationGoal(reader));
            }

            if (existingTables.Contains("goal_iterations"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM goal_iterations";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    iterations.Add(ReadMigrationIteration(reader));
            }

            if (existingTables.Contains("releases"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM releases";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    releases.Add(ReadMigrationRelease(reader));
            }
        }

        using (var db = dbContextFactory.CreateDbContext())
        {
            var importedGoals = 0;
            foreach (var goal in goals)
            {
                if (db.Goals.Any(g => g.Id == goal.Id)) continue;
                db.Goals.Add(goal);
                importedGoals++;
            }

            var importedIterations = 0;
            foreach (var iteration in iterations)
            {
                // Skip iterations whose goal is missing (preserve FK integrity).
                if (!goals.Any(g => g.Id == iteration.GoalId) && !db.Goals.Any(g => g.Id == iteration.GoalId))
                    continue;
                db.IterationSummaries.Add(iteration);
                importedIterations++;
            }

            var importedReleases = 0;
            foreach (var release in releases)
            {
                if (db.Releases.Any(r => r.Id == release.Id)) continue;
                db.Releases.Add(release);
                importedReleases++;
            }

            db.SaveChanges();

            logger.LogInformation(
                "Migrated {Goals} goals, {Iterations} iteration summaries, {Releases} releases from goals.db to copilothive.db",
                importedGoals, importedIterations, importedReleases);
        }

        var migratedPath = oldDbPath + ".migrated";
        if (File.Exists(migratedPath))
            File.Delete(migratedPath);
        File.Move(oldDbPath, migratedPath);
        logger.LogInformation("Renamed legacy database to {MigratedPath}", migratedPath);
    }

    private static bool MigrationColumnExists(SqliteDataReader reader, string columnName, out int ordinal)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                ordinal = i;
                return true;
            }
        }
        ordinal = -1;
        return false;
    }

    private static Goal ReadMigrationGoal(SqliteDataReader reader)
    {
        var scope = GoalScope.Patch;
        if (MigrationColumnExists(reader, "scope", out var scopeOrd) && !reader.IsDBNull(scopeOrd))
            Enum.TryParse<GoalScope>(reader.GetString(scopeOrd), ignoreCase: true, out scope);

        var goal = new Goal
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Description = reader.GetString(reader.GetOrdinal("description")),
            Status = Enum.Parse<GoalStatus>(reader.GetString(reader.GetOrdinal("status")), ignoreCase: true),
            Priority = Enum.Parse<GoalPriority>(reader.GetString(reader.GetOrdinal("priority")), ignoreCase: true),
            Scope = scope,
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };

        var reposOrd = reader.GetOrdinal("repositories");
        if (!reader.IsDBNull(reposOrd))
        {
            var repos = JsonSerializer.Deserialize<List<string>>(reader.GetString(reposOrd), MigrationJsonOptions);
            if (repos is not null) goal.RepositoryNames.AddRange(repos);
        }

        var metaOrd = reader.GetOrdinal("metadata");
        if (!reader.IsDBNull(metaOrd))
        {
            var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(metaOrd), MigrationJsonOptions);
            if (meta is not null)
                foreach (var kvp in meta) goal.Metadata[kvp.Key] = kvp.Value;
        }

        var startedOrd = reader.GetOrdinal("started_at");
        if (!reader.IsDBNull(startedOrd))
            goal.StartedAt = DateTime.Parse(reader.GetString(startedOrd), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var completedOrd = reader.GetOrdinal("completed_at");
        if (!reader.IsDBNull(completedOrd))
            goal.CompletedAt = DateTime.Parse(reader.GetString(completedOrd), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var iterOrd = reader.GetOrdinal("iterations");
        if (!reader.IsDBNull(iterOrd))
            goal.Iterations = reader.GetInt32(iterOrd);

        var failOrd = reader.GetOrdinal("failure_reason");
        if (!reader.IsDBNull(failOrd))
            goal.FailureReason = reader.GetString(failOrd);

        var notesOrd = reader.GetOrdinal("notes");
        if (!reader.IsDBNull(notesOrd))
        {
            var notes = JsonSerializer.Deserialize<List<string>>(reader.GetString(notesOrd), MigrationJsonOptions);
            if (notes is not null) goal.Notes = notes;
        }

        var phaseDurOrd = reader.GetOrdinal("phase_durations");
        if (!reader.IsDBNull(phaseDurOrd))
            goal.PhaseDurations = JsonSerializer.Deserialize<Dictionary<string, double>>(reader.GetString(phaseDurOrd), MigrationJsonOptions);

        var totalDurOrd = reader.GetOrdinal("total_duration_seconds");
        if (!reader.IsDBNull(totalDurOrd))
            goal.TotalDurationSeconds = reader.GetDouble(totalDurOrd);

        if (MigrationColumnExists(reader, "depends_on", out var dependsOnOrd) && !reader.IsDBNull(dependsOnOrd))
        {
            var deps = JsonSerializer.Deserialize<List<string>>(reader.GetString(dependsOnOrd), MigrationJsonOptions);
            if (deps is not null) goal.DependsOn.AddRange(deps);
        }

        if (MigrationColumnExists(reader, "merge_commit_hash", out var mergeHashOrd) && !reader.IsDBNull(mergeHashOrd))
            goal.MergeCommitHash = reader.GetString(mergeHashOrd);

        if (MigrationColumnExists(reader, "release_id", out var releaseIdOrd) && !reader.IsDBNull(releaseIdOrd))
            goal.ReleaseId = reader.GetString(releaseIdOrd);

        if (MigrationColumnExists(reader, "documents", out var documentsOrd) && !reader.IsDBNull(documentsOrd))
        {
            var docs = JsonSerializer.Deserialize<List<string>>(reader.GetString(documentsOrd), MigrationJsonOptions);
            if (docs is not null) goal.Documents = docs;
        }

        if (MigrationColumnExists(reader, "branch_cleaned_up", out var branchCleanedUpOrd) && !reader.IsDBNull(branchCleanedUpOrd))
            goal.BranchCleanedUp = reader.GetInt32(branchCleanedUpOrd) == 1;

        return goal;
    }

    private static IterationSummaryEntity ReadMigrationIteration(SqliteDataReader reader)
    {
        var entity = new IterationSummaryEntity
        {
            GoalId = reader.GetString(reader.GetOrdinal("goal_id")),
            Iteration = reader.GetInt32(reader.GetOrdinal("iteration")),
        };

        if (MigrationColumnExists(reader, "phases_json", out var phasesOrd) && !reader.IsDBNull(phasesOrd))
            entity.PhasesJson = reader.GetString(phasesOrd);

        if (MigrationColumnExists(reader, "test_total", out var ttOrd) && !reader.IsDBNull(ttOrd))
            entity.TestTotal = reader.GetInt32(ttOrd);
        if (MigrationColumnExists(reader, "test_passed", out var tpOrd) && !reader.IsDBNull(tpOrd))
            entity.TestPassed = reader.GetInt32(tpOrd);
        if (MigrationColumnExists(reader, "test_failed", out var tfOrd) && !reader.IsDBNull(tfOrd))
            entity.TestFailed = reader.GetInt32(tfOrd);

        if (MigrationColumnExists(reader, "review_verdict", out var rvOrd) && !reader.IsDBNull(rvOrd))
            entity.ReviewVerdict = reader.GetString(rvOrd);

        if (MigrationColumnExists(reader, "notes_json", out var notesOrd) && !reader.IsDBNull(notesOrd))
            entity.NotesJson = reader.GetString(notesOrd);

        if (MigrationColumnExists(reader, "phase_outputs_json", out var poOrd) && !reader.IsDBNull(poOrd))
            entity.PhaseOutputsJson = reader.GetString(poOrd);

        if (MigrationColumnExists(reader, "clarifications_json", out var clOrd) && !reader.IsDBNull(clOrd))
            entity.ClarificationsJson = reader.GetString(clOrd);

        if (MigrationColumnExists(reader, "build_success", out var bsOrd) && !reader.IsDBNull(bsOrd))
            entity.BuildSuccess = reader.GetInt32(bsOrd) == 1;

        if (MigrationColumnExists(reader, "created_at", out var caOrd) && !reader.IsDBNull(caOrd))
            entity.CreatedAt = reader.GetString(caOrd);
        else
            entity.CreatedAt = DateTime.UtcNow.ToString("O");

        return entity;
    }

    private static Release ReadMigrationRelease(SqliteDataReader reader)
    {
        var release = new Release
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Tag = reader.GetString(reader.GetOrdinal("tag")),
            Status = Enum.Parse<ReleaseStatus>(reader.GetString(reader.GetOrdinal("status")), ignoreCase: true),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };

        var notesOrd = reader.GetOrdinal("notes");
        if (!reader.IsDBNull(notesOrd))
            release.Notes = reader.GetString(notesOrd);

        var releasedOrd = reader.GetOrdinal("released_at");
        if (!reader.IsDBNull(releasedOrd))
            release.ReleasedAt = DateTime.Parse(reader.GetString(releasedOrd), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var reposOrd = reader.GetOrdinal("repositories");
        if (!reader.IsDBNull(reposOrd))
        {
            var repos = JsonSerializer.Deserialize<List<string>>(reader.GetString(reposOrd), MigrationJsonOptions);
            if (repos is not null) release.RepositoryNames.AddRange(repos);
        }

        return release;
    }
}

/// <summary>Request body for updating the status of a goal via the HTTP API.</summary>
/// <param name="Status">New status string (e.g. "completed", "failed").</param>
record GoalStatusUpdate(string Status);

/// <summary>Request body for creating a new release via the HTTP API.</summary>
/// <param name="Version">Version tag for the release (e.g. "v1.2.0").</param>
/// <param name="Repository">Optional repository name this release belongs to.</param>
record CreateReleaseRequest(string Version, string? Repository = null);

/// <summary>Request body for updating the status of a release via the HTTP API.</summary>
/// <param name="Status">New status string (e.g. "Planning" or "Released").</param>
record UpdateReleaseStatusRequest(string Status);

/// <summary>Request body for updating the notes of a release via the HTTP API.</summary>
/// <param name="Notes">Updated release notes.</param>
record UpdateReleaseNotesRequest(string? Notes);

/// <summary>Request body for updating the tag of a Planning release via the HTTP API.</summary>
/// <param name="Tag">New version tag (e.g. "v1.2.1").</param>
record UpdateReleaseTagRequest(string Tag);

/// <summary>Request body for updating the repository list of a Planning release via the HTTP API.</summary>
/// <param name="Repositories">New list of repository names. An empty list clears all repositories.</param>
record UpdateReleaseRepositoriesRequest(List<string>? Repositories);

/// <summary>Request body for assigning a goal to a release via the HTTP API.</summary>
/// <param name="ReleaseId">The release ID to assign this goal to.</param>
record AssignGoalReleaseRequest(string ReleaseId);

/// <summary>Request body for submitting an answer to a clarification request via the HTTP API.</summary>
/// <param name="Answer">The answer text to submit.</param>
record SubmitClarificationRequest(string Answer);
