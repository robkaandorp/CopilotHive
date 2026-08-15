using System.Reflection;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Source-level and reflection-based tests proving the production DI/forwarding wiring for
/// the event bus: Program.cs registrations/resolution/factory injection, and
/// GoalDispatcher's forwarding of <see cref="IEventBus"/> into
/// <see cref="GoalLifecycleService"/>.
/// </summary>
public sealed class EventBusWiringTests
{
    /// <summary>
    /// Locates the repository root by walking up from the test bin directory until a
    /// directory containing the solution file is found (same pattern as existing
    /// source-level tests such as <see cref="CopilotHive.Tests.Orchestration.SharpCoderPackageVersionTests"/>).
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null
            && !Directory.GetFiles(dir, "*.slnx").Any()
            && !Directory.Exists(Path.Combine(dir, "src", "CopilotHive")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(dir);
        Assert.True(Directory.Exists(Path.Combine(dir, "src", "CopilotHive")),
            $"Repository root not found from {AppContext.BaseDirectory}");
        return dir;
    }

    // ── 1. Program.cs wiring (source-level assertions) ──
    //
    // Every assertion below matches a FULL statement (receiver + trailing semicolon or the
    // distinctive named-argument prefix) so it can only be satisfied by its own intended
    // line. Bare substrings are deliberately avoided: for example
    // `GetService<ComposerEventSubscriber>()` occurs on BOTH the Composer factory argument
    // line and the startup-resolution line, so a bare-substring assertion would stay green
    // after the startup resolution was deleted.

    [Fact]
    public void ProgramCs_RegistersEventBusAndSubscriberAsSingletons()
    {
        var repoRoot = FindRepoRoot();
        var programPath = Path.Combine(repoRoot, "src", "CopilotHive", "Program.cs");
        Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}");

        var source = File.ReadAllText(programPath);

        // The event bus and subscriber must be registered with the DI container.
        // Full statements: no sibling line in Program.cs can satisfy either of these.
        Assert.Contains("builder.Services.AddSingleton<IEventBus, EventBus>();", source);
        Assert.Contains("builder.Services.AddSingleton<ComposerEventSubscriber>();", source);
    }

    [Fact]
    public void ProgramCs_ResolvesSubscriberAtStartup()
    {
        var repoRoot = FindRepoRoot();
        var programPath = Path.Combine(repoRoot, "src", "CopilotHive", "Program.cs");
        Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}");

        var source = File.ReadAllText(programPath);

        // The subscriber must be resolved at startup so its subscription is active
        // before any goal lifecycle events can be published.
        //
        // The receiver (`app.Services.`) and the trailing `;` are BOTH required: the
        // Composer factory line reads `eventSubscriber: sp.GetService<ComposerEventSubscriber>())`
        // — a different receiver (`sp.`) and no statement-terminating semicolon — so it
        // cannot satisfy this assertion. Deleting the startup resolution fails this test.
        Assert.Contains("app.Services.GetService<ComposerEventSubscriber>();", source);
    }

    [Fact]
    public void ProgramCs_InjectsSubscriberIntoComposerFactory()
    {
        var repoRoot = FindRepoRoot();
        var programPath = Path.Combine(repoRoot, "src", "CopilotHive", "Program.cs");
        Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}");

        var source = File.ReadAllText(programPath);

        // The Composer factory must receive the subscriber as the last argument. The
        // `eventSubscriber:` named-argument prefix plus the closing `);` of the factory
        // call is unique to that line — the startup resolution cannot satisfy it.
        Assert.Contains("eventSubscriber: sp.GetService<ComposerEventSubscriber>());", source);
    }

    /// <summary>
    /// Guards the assertions above against silent weakening: each expected source fragment
    /// must occur EXACTLY ONCE in Program.cs. If a future edit made any of them ambiguous
    /// (satisfiable by more than one line), the corresponding removal-proof test would stop
    /// detecting deletion of its intended line — this test fails first and names the culprit.
    /// </summary>
    [Fact]
    public void ProgramCs_WiringAssertionFragments_EachMatchExactlyOneLine()
    {
        var repoRoot = FindRepoRoot();
        var programPath = Path.Combine(repoRoot, "src", "CopilotHive", "Program.cs");
        Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}");

        var source = File.ReadAllText(programPath);

        string[] fragments =
        [
            "builder.Services.AddSingleton<IEventBus, EventBus>();",
            "builder.Services.AddSingleton<ComposerEventSubscriber>();",
            "app.Services.GetService<ComposerEventSubscriber>();",
            "eventSubscriber: sp.GetService<ComposerEventSubscriber>());",
        ];

        foreach (var fragment in fragments)
        {
            var occurrences = CountOccurrences(source, fragment);
            Assert.True(occurrences == 1,
                $"Expected exactly 1 occurrence of '{fragment}' in Program.cs but found {occurrences}. " +
                "An ambiguous or missing fragment breaks the removal-proof guarantee of the wiring tests.");
        }
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ── 2. GoalDispatcher → GoalLifecycleService reference forwarding ──

    [Fact]
    public async Task GoalDispatcher_ForwardsEventBusToGoalLifecycleService()
    {
        var eventBus = new EventBus();

        var goal = new Goal { Id = "wiring-test-goal", Description = "Test goal" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var dispatcher = new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new FakeRepoManager(),
            eventBus: eventBus);

        // Read the private _lifecycleService field from the dispatcher.
        var lifecycleField = typeof(GoalDispatcher).GetField("_lifecycleService",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_lifecycleService field not found on GoalDispatcher");
        var lifecycleService = lifecycleField.GetValue(dispatcher)
            ?? throw new InvalidOperationException("_lifecycleService was null");

        // Read the private _eventBus field from the lifecycle service.
        var eventBusField = lifecycleService.GetType().GetField("_eventBus",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_eventBus field not found on GoalLifecycleService");
        var forwarded = eventBusField.GetValue(lifecycleService);

        // The exact instance passed to the dispatcher must have been forwarded.
        Assert.Same(eventBus, forwarded);
    }

    [Fact]
    public async Task GoalDispatcher_WithNullEventBus_ForwardsNullToGoalLifecycleService()
    {
        var goal = new Goal { Id = "wiring-null-goal", Description = "Test goal" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        // No eventBus — backward compatibility path.
        var dispatcher = new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new FakeRepoManager());

        var lifecycleField = typeof(GoalDispatcher).GetField("_lifecycleService",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_lifecycleService field not found on GoalDispatcher");
        var lifecycleService = lifecycleField.GetValue(dispatcher)
            ?? throw new InvalidOperationException("_lifecycleService was null");

        var eventBusField = lifecycleService.GetType().GetField("_eventBus",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_eventBus field not found on GoalLifecycleService");
        var forwarded = eventBusField.GetValue(lifecycleService);

        Assert.Null(forwarded);
    }

    // ── Test doubles ──

    private sealed class FakeGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeRepoManager : IBrainRepoManager
    {
        public string WorkDirectory => "/fake/work";
        public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.FromResult($"/fake/work/{repoName}");
        public Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default) =>
            Task.FromResult("fake-sha");
        public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default) =>
            Task.FromResult(BranchDeleteResult.Success);
        public string GetClonePath(string repoName) => $"/fake/work/{repoName}";
        public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default) =>
            Task.FromResult(new List<string>());
    }
}
