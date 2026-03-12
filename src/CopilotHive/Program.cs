using CopilotHive.Configuration;
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

    PrintBanner();
    Console.WriteLine($"[Hive] Starting gRPC server on port {port}…");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddGrpc();
    builder.Services.AddSingleton<WorkerPool>();
    builder.Services.AddSingleton<TaskQueue>();

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(port, listenOptions =>
            listenOptions.Protocols = HttpProtocols.Http2);
    });

    var app = builder.Build();

    app.MapGrpcService<HiveOrchestratorService>();
    app.MapGet("/health", () => "ok");

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
