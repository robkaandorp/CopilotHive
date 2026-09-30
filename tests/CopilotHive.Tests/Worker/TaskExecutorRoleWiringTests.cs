using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;

using System.Runtime.CompilerServices;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE ASSIGNMENT DECIDES THE ROLE, end to end through the PRODUCTION chain: a REAL
/// <see cref="TaskExecutor"/> drives a REAL <see cref="SharpCoderRunner"/> for an assignment whose
/// <c>WorkTask.Role</c> is the ONLY role information available.
/// <para>
/// THE BUG THESE TESTS PIN. On an installation whose config repo has no
/// <c>agents/*.agents.md</c> file for a role, the orchestrator never sends that role's
/// <c>UpdateAgents</c> message. The runner's role was therefore only ever set by
/// <c>SetCustomAgent</c>, so workers ran on the connection default
/// (<see cref="WorkerRole.Unspecified"/>) with NO report tool and the wrong system prompt — even
/// though the role was right there on the assignment. The fix wires
/// <see cref="IAgentRunner.SetRole"/> from the assignment's own role on the ONE shared execution
/// path in <c>TaskExecutor.ExecuteCoreAsync</c>, and stores guidance PER ROLE.
/// </para>
/// <para>
/// NO <c>SetCustomAgent</c> call is made anywhere in the regression theory below — only
/// <see cref="SharpCoderRunner.ConnectAsync"/> (a real connection start, which restores the
/// connection defaults) followed by a real assignment, exactly as the failing installation
/// behaves.
/// </para>
/// <para>
/// The observation is taken through the REAL agent-options seam
/// (<see cref="SharpCoderRunner.OnAgentOptionsCreated"/> — the options object the production prompt
/// turn actually hands to the agent, including its tools, prompt and capability flags), not
/// through reflection on private state. Everything is deterministic: no sleeps, no delays, no
/// polling — the stub client completes each turn in one step.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class TaskExecutorRoleWiringTests
{
    /// <summary>The role's own mandatory report tool, per role.</summary>
    private static string ReportToolFor(WorkerRole role) => role switch
    {
        WorkerRole.Coder => "report_code_changes",
        WorkerRole.Tester => "report_test_results",
        WorkerRole.Reviewer => "report_review_verdict",
        WorkerRole.DocWriter => "report_doc_changes",
        _ => throw new InvalidOperationException(
            $"No role report tool is defined for WorkerRole '{role}' — this fixture covers the four "
            + "assignment roles that carry one."),
    };

    // ── (a) Regression: assignment role → prompt + report tool, for every role ─

    /// <summary>
    /// For EACH assignment role: a real connection start followed by a real assignment, with NO
    /// <c>SetCustomAgent</c> anywhere, must produce the role's own prompt (built with NO guidance)
    /// and the role's own report tool — never the <see cref="WorkerRole.Unspecified"/> default.
    /// <para>
    /// This is the regression test for the reported bug: on the pre-fix revision every row of this
    /// theory observes the <c>Unspecified</c> prompt, an empty role-tool set, and (for the Tester)
    /// a missing <c>report_test_results</c>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.DocWriter)]
    public async Task AssignmentRole_WithoutAnySetCustomAgent_ProducesRolePromptAndReportTool(
        WorkerRole role)
    {
        var (runner, observed) = BuildRealRunner();
        await using var _ = runner;

        // A REAL connection start: this restores the connection defaults (role = Unspecified), the
        // state a worker is in when the orchestrator sends NO UpdateAgents for this role.
        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        var result = await ExecuteAsync(runner, role, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.NotEmpty(observed);

        var expectedPrompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);
        var unspecifiedPrompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Unspecified, null);
        var expectedTool = ReportToolFor(role);

        // The default-role prompt and the role prompt must be distinguishable, or the assertions
        // below could pass vacuously.
        Assert.NotEqual(unspecifiedPrompt, expectedPrompt);

        // EVERY turn of the assignment (the tester, for one, may receive a follow-up metrics
        // prompt) must run as this assignment's role.
        Assert.All(observed, options =>
        {
            Assert.Equal(expectedPrompt, options.SystemPrompt);
            Assert.NotEqual(unspecifiedPrompt, options.SystemPrompt);
            Assert.Contains(expectedTool, ToolNames(options));
        });
    }

    /// <summary>
    /// THE PHASE-REPORT TOOL IS FOR EVERY ASSIGNMENT ROLE — a later phase of the same iteration
    /// (a second Coding occurrence after a review, the tester, the doc-writer) must be able to read
    /// an earlier phase's completed report, so gating the tool on a role would silently hide the
    /// evidence the goal asked for.
    /// </summary>
    /// <remarks>
    /// NO <c>SetCustomAgent</c> call is made anywhere: only a real <see cref="SharpCoderRunner.ConnectAsync"/>
    /// followed by a real assignment, exactly as the failing installation behaves.
    /// </remarks>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.DocWriter)]
    [InlineData(WorkerRole.Improver)]
    public async Task AssignmentRole_EveryRole_ExposesGetPhaseReport(WorkerRole role)
    {
        using var configRepo = new PhaseReportConfigRepo();
        var (runner, observed) = BuildRealRunner(configRepo.Directory);
        await using var _ = runner;

        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        var result = await ExecuteAsync(runner, role, TestContext.Current.CancellationToken, configRepo.Directory);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.NotEmpty(observed);
        Assert.All(observed, options => Assert.Contains("get_phase_report", ToolNames(options)));
    }

    /// <summary>
    /// The new tool is ADDITIVE: the Reviewer still exposes <c>get_test_report</c> (and the report
    /// verdict tool), the two mechanisms stay orthogonal, and no OTHER role gains
    /// <c>get_test_report</c>.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.DocWriter)]
    [InlineData(WorkerRole.Improver)]
    public async Task AssignmentRole_GetTestReportStaysReviewerOnly(WorkerRole role)
    {
        using var configRepo = new PhaseReportConfigRepo();
        var (runner, observed) = BuildRealRunner(configRepo.Directory);
        await using var _ = runner;

        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        var result = await ExecuteAsync(runner, role, TestContext.Current.CancellationToken, configRepo.Directory);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.NotEmpty(observed);
        Assert.All(observed, options =>
        {
            Assert.Contains("get_phase_report", ToolNames(options));
            if (role == WorkerRole.Reviewer)
                Assert.Contains("get_test_report", ToolNames(options));
            else
                Assert.DoesNotContain("get_test_report", ToolNames(options));
        });
    }

    /// <summary>
    /// THE REVIEWER'S FULL CONTRACT from the same no-<c>SetCustomAgent</c> chain: the review
    /// verdict tool AND <c>get_test_report</c> — and file writes DISABLED, because a reviewer must
    /// never modify the code it reviews.
    /// </summary>
    [Fact]
    public async Task AssignmentRole_Reviewer_ExposesGetTestReportAndDisablesFileWrites()
    {
        var (runner, observed) = BuildRealRunner();
        await using var _ = runner;

        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        var result = await ExecuteAsync(runner, WorkerRole.Reviewer, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.NotEmpty(observed);

        var expectedPrompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Reviewer, null);
        Assert.All(observed, options =>
        {
            Assert.Equal(expectedPrompt, options.SystemPrompt);

            var toolNames = ToolNames(options);
            Assert.Contains("report_review_verdict", toolNames);
            Assert.Contains("get_test_report", toolNames);
            Assert.DoesNotContain("report_code_changes", toolNames);

            Assert.False(options.EnableFileWrites);
        });
    }

    // ── (b) Guidance is stored PER ROLE ────────────────────────────────────────

    /// <summary>
    /// ONE ROLE'S DELIVERED GUIDANCE MUST NEVER APPEAR IN ANOTHER ROLE'S PROMPT.
    /// <para>
    /// The coder's <c>UpdateAgents</c> guidance is delivered first (a real
    /// <c>SetCustomAgent</c>, as the production message loop does), then a TESTER assignment runs —
    /// the shape that leaks under a single-guidance implementation, because the runner would still
    /// be holding the coder's guidance while running as the tester. The tester turn must use the
    /// TESTER prompt with NO coder guidance and the tester's own report tool only.
    /// </para>
    /// <para>
    /// The coder's guidance is then still there for a LATER coder assignment — positive evidence
    /// that the tester assignment did not consume or overwrite it, i.e. that the per-assignment
    /// role call (<see cref="IAgentRunner.SetRole"/>) touches the role ONLY.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Guidance_IsStoredPerRole_AndSurvivesOtherRoleAssignments()
    {
        const string CoderGuidance = "CODER-GUIDANCE";
        var ct = TestContext.Current.CancellationToken;

        var (runner, observed) = BuildRealRunner();
        await using var _ = runner;

        // The coder's UpdateAgents delivery, exactly as WorkerService applies it.
        runner.SetCustomAgent(WorkerRole.Coder, CoderGuidance);

        // ── A TESTER assignment on the same runner ────────────────────────────
        var testerResult = await ExecuteAsync(runner, WorkerRole.Tester, ct);

        Assert.Equal(TaskOutcome.Completed, testerResult.Status);
        Assert.NotEmpty(observed);

        var testerPrompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Tester, null);
        Assert.All(observed, options =>
        {
            Assert.Equal(testerPrompt, options.SystemPrompt);
            Assert.DoesNotContain(CoderGuidance, options.SystemPrompt, StringComparison.Ordinal);

            var toolNames = ToolNames(options);
            Assert.Contains("report_test_results", toolNames);
            Assert.DoesNotContain("report_code_changes", toolNames);
        });

        // ── A LATER CODER assignment gets the coder guidance again ────────────
        observed.Clear();
        var coderResult = await ExecuteAsync(runner, WorkerRole.Coder, ct);

        Assert.Equal(TaskOutcome.Completed, coderResult.Status);
        Assert.NotEmpty(observed);

        var coderPrompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, CoderGuidance);
        Assert.All(observed, options =>
        {
            Assert.Equal(coderPrompt, options.SystemPrompt);
            Assert.Contains(CoderGuidance, options.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(
                SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Tester, null),
                options.SystemPrompt);
            Assert.Contains("report_code_changes", ToolNames(options));
        });
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A REAL <see cref="SharpCoderRunner"/> with the production lazy-client seam pointed at a stub
    /// client and the agent-options seam recording every options object the prompt turn builds. NO
    /// <c>SetCustomAgent</c> call is made here: the role must come from the assignment alone.
    /// </summary>
    private static (SharpCoderRunner Runner, List<AgentOptions> Observed) BuildRealRunner(
        string configRepoDir = "/config-repo")
    {
        var runner = new SharpCoderRunner(configRepoDir);
        var observed = new List<AgentOptions>();

        runner.ClientCreationSeam = _ => new RoleWiringStubClient();
        runner.OnAgentOptionsCreated = options => observed.Add(options);

        return (runner, observed);
    }

    /// <summary>
    /// Drives the real <see cref="TaskExecutor"/> (production constructor, no tool bridge) over an
    /// assignment for <paramref name="role"/>. The git transport is the shared
    /// <see cref="PhaseReportGit"/> double: a clean worktree, no merge base, and the EXPLICIT valid
    /// answers the Improver's pre-run baseline preparation requires (its own path refuses to run
    /// without them). Together with the repository-less task it keeps the run to the prompt path
    /// under test: no network, no real clone, no branch operation, no extra auto-commit prompt.
    /// <para>
    /// <paramref name="configRepoDir"/> is the executor's config-repo path — the Improver needs a
    /// real <c>.git</c>-bearing directory there, so the vectors that cover the Improver pass one.
    /// </para>
    /// </summary>
    private static Task<TaskResult> ExecuteAsync(
        SharpCoderRunner runner, WorkerRole role, CancellationToken ct, string configRepoDir = "/config-repo") =>
        new TaskExecutor(runner, gitOperations: new PhaseReportGit(), configRepoDir: configRepoDir)
            .ExecuteAsync(MakeTask(role), ct);

    /// <summary>The custom tool names the prompt turn handed to the agent.</summary>
    private static List<string> ToolNames(AgentOptions options) =>
        [.. options.CustomTools.Select(tool => tool.Name)];

    /// <summary>
    /// An assignment for <paramref name="role"/> with NO repositories: the executor then uses its
    /// configurable work root (<c>WORKER_WORK_ROOT</c>, pointed at a real directory for the whole
    /// test assembly), so the run reaches the prompt without any clone or branch operation.
    /// </summary>
    private static WorkTask MakeTask(WorkerRole role) => new()
    {
        TaskId = $"task-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
        GoalId = "goal-role-wiring",
        GoalDescription = "Role wiring goal",
        Prompt = "Work on the assigned task",
        Role = role,
        Repositories = [],
    };

    /// <summary>
    /// A minimal chat client that completes one agent turn: a single assistant text update
    /// followed by the terminal stop update, so the runner's real prompt turn runs to a
    /// <c>Success</c> result without any LLM.
    /// </summary>
    private sealed class RoleWiringStubClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("role-wiring-stub", null, "role-wiring-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            StreamAsync(ct);

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
