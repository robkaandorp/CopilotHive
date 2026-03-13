using CopilotHive;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// ── Server mode ────────────────────────────────────────────────────────────────
if (args.Contains("--serve"))
{
    return await RunServerAsync(args);
}

// ── Legacy CLI mode ────────────────────────────────────────────────────────────
return await RunCliAsync(args);

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
    builder.Services.AddSingleton<TaskQueue>();
    builder.Services.AddSingleton<ApiGoalSource>();
    builder.Services.AddSingleton<TaskCompletionNotifier>();
    builder.Services.AddSingleton<ImprovementAnalyzer>();

    // Agents: AGENTS.md versioning and rollback
    var agentsDir = Environment.GetEnvironmentVariable("AGENTS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "agents");
    if (!Directory.Exists(agentsDir))
        agentsDir = Path.Combine(Directory.GetCurrentDirectory(), "agents");
    builder.Services.AddSingleton(new AgentsManager(agentsDir));

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
                sp.GetService<AgentsManager>()));
    }
    else
    {
        Console.WriteLine("[Hive] Brain disabled — running in mechanical mode (no BRAIN_COPILOT_PORT set)");
    }

    builder.Services.AddSingleton<GoalDispatcher>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<GoalDispatcher>());

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
    var _checkCount = 0;
    app.MapGet("/health", () =>
    {
        var count = Interlocked.Increment(ref _checkCount);
        return Results.Ok($"Healthy (check #{count})");
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

static async Task<int> RunCliAsync(string[] args)
{
    var goal = args.FirstOrDefault(a => a.StartsWith("--goal="))?[7..];
    var workspace = args.FirstOrDefault(a => a.StartsWith("--workspace="))?[12..] ?? "./workspaces";
    var source = args.FirstOrDefault(a => a.StartsWith("--source="))?[9..];
    var maxIterations = int.TryParse(
        args.FirstOrDefault(a => a.StartsWith("--max-iterations="))?[17..], out var mi) ? mi : Constants.DefaultMaxIterations;
    var model = args.FirstOrDefault(a => a.StartsWith("--model="))?[8..] ?? Constants.DefaultModel;
    var coderModel = args.FirstOrDefault(a => a.StartsWith("--coder-model="))?[14..];
    var reviewerModel = args.FirstOrDefault(a => a.StartsWith("--reviewer-model="))?[17..];
    var testerModel = args.FirstOrDefault(a => a.StartsWith("--tester-model="))?[15..];
    var improverModel = args.FirstOrDefault(a => a.StartsWith("--improver-model="))?[17..];
    var orchestratorModel = args.FirstOrDefault(a => a.StartsWith("--orchestrator-model="))?[21..];
    var alwaysImprove = args.Contains("--always-improve");

    var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN")
        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    if (string.IsNullOrEmpty(goal))
    {
        Console.Error.WriteLine("Usage: CopilotHive --goal=\"<goal description>\" [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --serve                  Start as gRPC server (distributed mode)");
        Console.Error.WriteLine("  --port=<n>               gRPC server port (default: 5000, requires --serve)");
        Console.Error.WriteLine("  --goal=<text>            The objective for the hive to accomplish (required)");
        Console.Error.WriteLine("  --workspace=<path>       Workspace root directory (default: ./workspaces)");
        Console.Error.WriteLine("  --source=<path>          Project source directory to seed workspace with");
        Console.Error.WriteLine($"  --max-iterations=<n>     Maximum iteration count (default: {Constants.DefaultMaxIterations})");
        Console.Error.WriteLine($"  --model=<model>          Fallback model for all roles (default: {Constants.DefaultModel})");
        Console.Error.WriteLine("  --coder-model=<model>    Model for coder role (default: claude-opus-4.6)");
        Console.Error.WriteLine("  --reviewer-model=<model> Model for reviewer role (default: gpt-5.3-codex)");
        Console.Error.WriteLine("  --tester-model=<model>   Model for tester role (default: claude-sonnet-4.6)");
        Console.Error.WriteLine("  --improver-model=<model> Model for improver role (default: claude-sonnet-4.6)");
        Console.Error.WriteLine("  --orchestrator-model=<m> Model for orchestrator interpretation (default: claude-sonnet-4.6)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Environment:");
        Console.Error.WriteLine("  GH_TOKEN                 GitHub PAT with Copilot permissions (required)");
        return 1;
    }

    if (string.IsNullOrEmpty(ghToken))
    {
        Console.Error.WriteLine("Error: GH_TOKEN environment variable is required.");
        return 1;
    }

    PrintBanner();

    var config = new HiveConfiguration
    {
        Goal = goal,
        WorkspacePath = workspace,
        SourcePath = source,
        MaxIterations = maxIterations,
        Model = model,
        CoderModel = coderModel,
        ReviewerModel = reviewerModel,
        TesterModel = testerModel,
        ImproverModel = improverModel,
        OrchestratorModel = orchestratorModel,
        AlwaysImprove = alwaysImprove,
        GitHubToken = ghToken,
    };

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n[Hive] Shutting down gracefully...");
        cts.Cancel();
    };

    await using var orchestrator = new Orchestrator(config);

    try
    {
        await orchestrator.RunAsync(cts.Token);
        Console.WriteLine("\n[Hive] Done.");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Hive] Cancelled.");
        return 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"\n[Hive] Fatal error: {ex.Message}");
        return 1;
    }
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

/// <summary>Request body for updating the status of a goal via the HTTP API.</summary>
/// <param name="Status">New status string (e.g. "completed", "failed").</param>
record GoalStatusUpdate(string Status);
