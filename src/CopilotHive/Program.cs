using System.Reflection;
using CopilotHive;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Metrics;
using CopilotHive.Knowledge;
using CopilotHive.Models;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;

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
        new PipelineStore(dbPath, sp.GetRequiredService<ILogger<PipelineStore>>()));

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
                : config?.Orchestrator.BrainContextWindow ?? Constants.DefaultBrainContextWindow;
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
                knowledgeGraph: sp.GetService<KnowledgeGraph>());
        });
    }

    builder.Services.AddSingleton<WorkerUtilizationService>();
    builder.Services.AddSingleton<ClarificationQueueService>();

    // Composer agent (optional — enabled when config has a composer section or BRAIN_MODEL is set)
    // Registered BEFORE GoalDispatcher so the IClarificationRouter forwarding is available.
    builder.Services.AddSingleton<Composer>(sp =>
    {
        var config = sp.GetService<HiveConfigFile>();
        var composerConfig = config?.Composer;

        // Model: composer-specific override → orchestrator model → env var
        var model = composerConfig?.Model;
        if (string.IsNullOrEmpty(model))
            model = config?.Orchestrator.Model;
        if (string.IsNullOrEmpty(model))
            model = brainModel ?? Constants.DefaultWorkerModel;

        var maxCtx = composerConfig?.ContextWindow ?? config?.Orchestrator.BrainContextWindow ?? Constants.DefaultBrainContextWindow;
        var maxSteps = composerConfig?.MaxSteps ?? config?.Orchestrator.BrainMaxSteps ?? Constants.DefaultBrainMaxSteps;
        var availableModels = composerConfig?.GetAvailableModels(model) ?? [model];

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
    builder.Services.AddScoped<HttpClient>(_ => new HttpClient
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
    // Goals: SQLite-backed goal store (primary source of truth)
    var goalsDbPath = Path.Combine(stateDir, "goals.db");
    builder.Services.AddSingleton(sp =>
        new SqliteGoalStore(goalsDbPath, sp.GetRequiredService<ILogger<SqliteGoalStore>>(),
            sp.GetRequiredService<PipelineStore>()));
    builder.Services.AddSingleton<IGoalStore>(sp => sp.GetRequiredService<SqliteGoalStore>());

    builder.Services.AddSingleton(sp =>
    {
        var manager = new GoalManager();
        manager.AddSource(sp.GetRequiredService<SqliteGoalStore>());
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
    app.MapComposerEndpoints(composer);

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
        var goalStore = app.Services.GetRequiredService<SqliteGoalStore>();
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
    app.MapGet("/health", async (SqliteGoalStore goalStore, WorkerPool workerPool) =>
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

    goalsApi.MapGet("/", async (SqliteGoalStore store) =>
        Results.Ok(await store.GetAllGoalsAsync()));

    goalsApi.MapGet("/{id}", async (string id, SqliteGoalStore store) =>
    {
        var goal = await store.GetGoalAsync(id);
        return goal is null ? Results.NotFound(new { error = $"Goal '{id}' not found." }) : Results.Ok(goal);
    });

    goalsApi.MapPost("/", async (Goal goal, SqliteGoalStore store) =>
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

    goalsApi.MapPatch("/{id}/status", async (string id, GoalStatusUpdate update, SqliteGoalStore store,
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
                (existing.Status == GoalStatus.Draft && status == GoalStatus.Pending) ||
                (existing.Status == GoalStatus.Pending && status == GoalStatus.Draft) ||
                (existing.Status == GoalStatus.Failed && status == GoalStatus.Draft);
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

    goalsApi.MapDelete("/{id}", async (string id, SqliteGoalStore store, IBrainRepoManager? repoManager, ILogger<Program> logger) =>
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

    goalsApi.MapPost("/{id}/cancel", async (string id, GoalDispatcher dispatcher, SqliteGoalStore store) =>
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

    goalsApi.MapGet("/search", async (string q, string? status, SqliteGoalStore store) =>
    {
        GoalStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var s))
            statusFilter = s;
        var results = await store.SearchGoalsAsync(q, statusFilter);
        return Results.Ok(results);
    });

    // ── Releases REST API ────────────────────────────────────────────────────
    var releasesApi = app.MapGroup("/api/releases");

    releasesApi.MapPost("/", async (CreateReleaseRequest request, SqliteGoalStore store) =>
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

    releasesApi.MapPatch("/{id}/status", async (string id, UpdateReleaseStatusRequest request, SqliteGoalStore store) =>
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

    releasesApi.MapPatch("/{id}/notes", async (string id, UpdateReleaseNotesRequest request, SqliteGoalStore store) =>
    {
        var existing = await store.GetReleaseAsync(id);
        if (existing is null)
            return Results.NotFound(new { error = $"Release '{id}' not found." });

        existing.Notes = request.Notes;
        await store.UpdateReleaseAsync(existing);
        return Results.Ok(existing);
    });

    releasesApi.MapPatch("/{id}/tag", async (string id, UpdateReleaseTagRequest request, SqliteGoalStore store) =>
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

    releasesApi.MapPatch("/{id}/repositories", async (string id, UpdateReleaseRepositoriesRequest request, SqliteGoalStore store) =>
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

    goalsApi.MapPatch("/{id}/release", async (string id, AssignGoalReleaseRequest request, SqliteGoalStore store) =>
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
    var version = CopilotHive.Services.VersionHelper.InformationalVersion;
    Console.WriteLine($"CopilotHive v{version}");
    Console.WriteLine($"Started at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
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

// Marker class required by WebApplicationFactory<T> in integration tests.
#pragma warning disable CS1591
public partial class Program { }
#pragma warning restore CS1591
