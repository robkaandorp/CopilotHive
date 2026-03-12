using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
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
    var port = 5000;
    var portArg = args.FirstOrDefault(a => a.StartsWith("--port="));
    if (portArg is not null && int.TryParse(portArg["--port=".Length..], out var p))
        port = p;

    var goalsFile = args.FirstOrDefault(a => a.StartsWith("--goals-file="))?["--goals-file=".Length..];
    var configRepoUrl = args.FirstOrDefault(a => a.StartsWith("--config-repo="))?["--config-repo=".Length..];
    var configRepoPath = args.FirstOrDefault(a => a.StartsWith("--config-repo-path="))?["--config-repo-path=".Length..]
        ?? "./config-repo";

    PrintBanner();
    Console.WriteLine($"[Hive] Starting gRPC server on port {port}…");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddGrpc();
    builder.Services.AddSingleton<WorkerPool>();
    builder.Services.AddSingleton<TaskQueue>();
    builder.Services.AddSingleton<ApiGoalSource>();

    if (!string.IsNullOrEmpty(configRepoUrl))
    {
        Console.WriteLine($"[Hive] Syncing config repo from {configRepoUrl}…");
        var configRepo = new ConfigRepoManager(configRepoUrl, configRepoPath);
        await configRepo.SyncRepoAsync();
        var hiveConfigFile = await configRepo.LoadConfigAsync();

        builder.Services.AddSingleton(configRepo);
        builder.Services.AddSingleton(hiveConfigFile);

        Console.WriteLine($"[Hive] Config loaded: {hiveConfigFile.Repositories.Count} repo(s), " +
            $"{hiveConfigFile.Workers.Count} worker config(s)");
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
        options.ListenAnyIP(port, listenOptions =>
            listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
    });

    var app = builder.Build();

    app.MapGrpcService<HiveOrchestratorService>();
    app.MapGet("/health", () => "ok");

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
        args.FirstOrDefault(a => a.StartsWith("--max-iterations="))?[17..], out var mi) ? mi : 10;
    var model = args.FirstOrDefault(a => a.StartsWith("--model="))?[8..] ?? "claude-opus-4.6";
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
        Console.Error.WriteLine("  --max-iterations=<n>     Maximum iteration count (default: 10)");
        Console.Error.WriteLine("  --model=<model>          Fallback model for all roles (default: claude-opus-4.6)");
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

record GoalStatusUpdate(string Status);
