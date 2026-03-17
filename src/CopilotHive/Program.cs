using System.Reflection;
using CopilotHive;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Metrics;
using CopilotHive.Models;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

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
    Console.WriteLine($"[Hive] Starting gRPC server on port {port}, HTTP on port {port + 1}…");

    var builder = WebApplication.CreateBuilder(args);

    // Suppress noisy health-check request logs and framework noise
    builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);

    builder.Services.AddGrpc();
    builder.Services.AddSingleton<WorkerPool>();
    builder.Services.AddSingleton<IWorkerPool>(sp => sp.GetRequiredService<WorkerPool>());
    builder.Services.AddSingleton<TaskQueue>();
    builder.Services.AddSingleton<ApiGoalSource>();
    builder.Services.AddSingleton<TaskCompletionNotifier>();
    builder.Services.AddSingleton<ImprovementAnalyzer>();

    // Agents: AGENTS.md versioning and rollback
    var agentsDir = Environment.GetEnvironmentVariable("AGENTS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "agents");
    if (!Directory.Exists(agentsDir))
        agentsDir = Path.Combine(Directory.GetCurrentDirectory(), "agents");
    builder.Services.AddSingleton(new AgentsManager(agentsDir));

    // Skills: framework-agnostic build/test/install instructions
    var skillsDir = Environment.GetEnvironmentVariable("SKILLS_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, ".github", "copilot", "skills");
    if (!Directory.Exists(skillsDir))
        skillsDir = Path.Combine(Directory.GetCurrentDirectory(), ".github", "copilot", "skills");
    builder.Services.AddSingleton(new CopilotHive.Skills.SkillsManager(skillsDir));

    // Persistence: SQLite store for pipeline state (survives restarts)
    var stateDir = Environment.GetEnvironmentVariable("STATE_DIR") ?? "/app/state";
    var dbPath = Path.Combine(stateDir, "copilothive.db");
    builder.Services.AddSingleton(sp =>
        new PipelineStore(dbPath, sp.GetRequiredService<ILogger<PipelineStore>>()));

    // Metrics: per-iteration metrics persistence
    var metricsDir = Path.Combine(stateDir, "metrics");
    Directory.CreateDirectory(metricsDir);
    builder.Services.AddSingleton(new MetricsTracker(metricsDir));

    builder.Services.AddSingleton(sp =>
        new GoalPipelineManager(sp.GetRequiredService<PipelineStore>()));

    // Brain: connect to Copilot CLI running alongside the orchestrator
    var brainPort = int.TryParse(Environment.GetEnvironmentVariable("BRAIN_COPILOT_PORT"), out var bp) ? bp : 0;
    if (brainPort > 0)
    {
        Console.WriteLine($"[Hive] Brain enabled — connecting to Copilot on port {brainPort}");
        builder.Services.AddSingleton<IDistributedBrain>(sp =>
            new DistributedBrain(brainPort, sp.GetRequiredService<ILogger<DistributedBrain>>(),
                sp.GetRequiredService<MetricsTracker>(),
                sp.GetService<AgentsManager>(),
                sp.GetService<CopilotHive.Skills.SkillsManager>()));
    }
    else
    {
        Console.WriteLine("[Hive] Brain disabled — running in mechanical mode (no BRAIN_COPILOT_PORT set)");
    }

    builder.Services.AddSingleton<GoalDispatcher>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<GoalDispatcher>());
    builder.Services.AddHostedService<StaleWorkerCleanupService>();

    if (!string.IsNullOrEmpty(configRepoUrl))
    {
        Console.WriteLine($"[Hive] Syncing config repo from {configRepoUrl}…");
        var configRepo = new ConfigRepoManager(configRepoUrl, configRepoPath);
        await configRepo.SyncRepoAsync();
        var hiveConfigFile = await configRepo.LoadConfigAsync();

        builder.Services.AddSingleton(configRepo);
        builder.Services.AddSingleton(hiveConfigFile);

        // If no explicit goals file, check config repo for goals.yaml
        if (string.IsNullOrEmpty(goalsFile))
        {
            var configGoalsFile = Path.Combine(configRepo.LocalPath, "goals.yaml");
            if (File.Exists(configGoalsFile))
            {
                goalsFile = configGoalsFile;
                Console.WriteLine($"[Hive] Using goals file from config repo: {configGoalsFile}");
            }
        }

        Console.WriteLine($"[Hive] Config loaded: {hiveConfigFile.Repositories.Count} repo(s), " +
            $"{hiveConfigFile.Workers.Count} worker config(s)");

        // Enable debug logging if verbose_logging is set in config
        if (hiveConfigFile.Orchestrator.VerboseLogging)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            // Keep framework noise suppressed even in verbose mode
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
            builder.Logging.AddFilter("Grpc", LogLevel.Warning);
            Console.WriteLine("[Hive] Verbose logging enabled (Debug level)");
        }
    }
    builder.Services.AddSingleton(sp =>
    {
        var manager = new GoalManager();
        manager.AddSource(sp.GetRequiredService<ApiGoalSource>());

        if (!string.IsNullOrEmpty(goalsFile))
            manager.AddSource(new FileGoalSource(goalsFile));

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

    // Wire up Brain and completion event
    var brain = app.Services.GetService<IDistributedBrain>();
    if (brain is not null)
    {
        Console.WriteLine("[Hive] Connecting Brain to Copilot…");
        await brain.ConnectAsync();
        Console.WriteLine("[Hive] Brain connected.");
    }

    app.MapGrpcService<HiveOrchestratorService>();
    var _serverStartTime = DateTime.UtcNow;
    var _checkCount = 0;
    var _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    app.MapGet("/health", (ApiGoalSource goalSource, WorkerPool workerPool) =>
    {
        var count = Interlocked.Increment(ref _checkCount);
        var uptime = DateTime.UtcNow - _serverStartTime;
        var goals = goalSource.GetAllGoals();
        return Results.Ok(new HealthResponse
        {
            Status = "Healthy",
            Uptime = FormatUptime(uptime),
            UptimeSpan = uptime,
            ActiveGoals = goals.Count(g => g.Status is GoalStatus.Pending or GoalStatus.InProgress),
            CompletedGoals = goals.Count(g => g.Status == GoalStatus.Completed),
            ConnectedWorkers = workerPool.GetAllWorkers().Count,
            Version = _version,
            ServerTime = DateTime.UtcNow,
            CheckNumber = count,
            WorkerPool = workerPool.GetDetailedStats(),
        });
    });

    // ── Goals REST API ───────────────────────────────────────────────────────
    var goalsApi = app.MapGroup("/api/goals");

    goalsApi.MapGet("/", (ApiGoalSource source) => Results.Ok(source.GetAllGoals()));

    goalsApi.MapPost("/", (Goal goal, ApiGoalSource source) =>
    {
        try
        {
            var created = source.AddGoal(goal);
            return Results.Created($"/api/goals/{created.Id}", created);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    });

    goalsApi.MapPatch("/{id}/status", async (string id, GoalStatusUpdate update, GoalManager manager) =>
    {
        try
        {
            var status = Enum.Parse<GoalStatus>(update.Status, ignoreCase: true);
            var source = manager.Sources.FirstOrDefault(s => s.Name == "api") as ApiGoalSource;
            if (source is null)
                return Results.Problem("API goal source not configured.");

            await source.UpdateGoalStatusAsync(id, status);
            return Results.Ok(source.GetGoal(id));
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
}

// Formats uptime as a human-readable string, e.g. "2d 3h 14m", "5h 2m", or "45m 3s".
static string FormatUptime(TimeSpan ts) =>
    ts.Days > 0
        ? $"{ts.Days}d {ts.Hours}h {ts.Minutes}m"
        : ts.Hours > 0
            ? $"{ts.Hours}h {ts.Minutes}m"
            : $"{ts.Minutes}m {ts.Seconds}s";

/// <summary>Request body for updating the status of a goal via the HTTP API.</summary>
/// <param name="Status">New status string (e.g. "completed", "failed").</param>
record GoalStatusUpdate(string Status);

// Marker class required by WebApplicationFactory<T> in integration tests.
#pragma warning disable CS1591
public partial class Program { }
#pragma warning restore CS1591
