#pragma warning disable CS1591
#pragma warning disable OPENAI001 // ResponsesClient.AsIChatClient is experimental
using CopilotHive.Services;
using CopilotHive.Shared;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;
using SharpCoder.Providers;
using SharpCoder.SubAgents;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CopilotHive.Worker;

public sealed class SharpCoderRunner : IAgentRunner
{
    private readonly WorkerLogger _log = new("SharpCoder");
    private readonly bool _verboseLogging = Environment.GetEnvironmentVariable("VERBOSE_LOGGING") == "true";
    private readonly Func<string?, IChatClient>? _clientFactory;
    private IChatClient? _chatClient;
    private string _currentModel = "(default)";
    private ReasoningEffort? _currentReasoning;

    /// <summary>
    /// The model the next lazily-created client must use, recorded by <see cref="ResetSessionAsync"/>.
    /// </summary>
    private string? _pendingModel;

    /// <summary>
    /// Runs orchestrator provisioning before a client is created. Invoked UNCONDITIONALLY before
    /// every first client creation. <c>null</c> disables provisioning (unit tests, and any
    /// deployment where the worker is configured entirely by the operator).
    /// </summary>
    private Func<string?, CancellationToken, Task>? _configProvisioner;

    /// <summary>
    /// Serializes the ENTIRE LLM client lifecycle — lazy creation, reset/dispose and final
    /// disposal — so two overlapping task assignments can never race it.
    /// <para>
    /// The race this closes: <c>WorkerService</c> sends <c>WorkerReady</c> from a completing
    /// task's <c>finally</c> and the cancel handler sends a second one, so the orchestrator can
    /// deliver a new assignment while the previous task is still unwinding. Without this gate,
    /// two tasks could both observe a null <c>_chatClient</c> and each construct one (leaking a
    /// client), or one could dispose the client while the other was still using it.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _clientLifecycleGate = new(1, 1);

    /// <summary>Set to 1 by the first <see cref="DisposeAsync"/>, making repeat calls no-ops.</summary>
    private int _disposed;

    private readonly string _configRepoDir;

    /// <summary>
    /// Initializes a new <see cref="SharpCoderRunner"/>. Call <see cref="ConnectAsync"/>
    /// before invoking <see cref="SendPromptAsync"/> to create the chat client.
    /// </summary>
    /// <param name="configRepoDir">Directory containing the agent instruction repository. Defaults to <c>/config-repo</c>.</param>
    public SharpCoderRunner(string configRepoDir = "/config-repo")
    {
        _configRepoDir = configRepoDir;
    }

    /// <summary>
    /// Internal constructor for unit testing: injects a pre-created <see cref="IChatClient"/>
    /// and model name, bypassing <see cref="SharpCoder.Providers.ChatClientFactory"/> so that tests
    /// can run without real LLM credentials.
    /// </summary>
    /// <param name="chatClient">The chat client to use for agent execution.</param>
    /// <param name="model">The model identifier to record in log output.</param>
    /// <param name="configRepoDir">Directory containing the agent instruction repository. Defaults to <c>/config-repo</c>.</param>
    internal SharpCoderRunner(IChatClient chatClient, string model, string configRepoDir = "/config-repo")
    {
        _configRepoDir = configRepoDir;
        _chatClient = chatClient;
        _currentModel = model;
        _clientFactory = _ => chatClient;
    }

    /// <summary>
    /// THE CONSTRUCTOR DEFAULT ROLE — the role the runner holds before any <c>UpdateAgents</c> or
    /// assignment role has been applied on a connection. <see cref="ConnectAsync"/> RESTORES it, so
    /// an idle runner never keeps carrying the PREVIOUS run's last role into a new connection's
    /// preparation.
    /// </summary>
    private const WorkerRole DefaultRole = WorkerRole.Unspecified;

    /// <summary>
    /// THE CONSTRUCTOR DEFAULT PER-ROLE GUIDANCE STATE — no per-role guidance at all (an EMPTY
    /// map). <see cref="ConnectAsync"/> restores it together with <see cref="DefaultRole"/>, so the
    /// two per-connection preparation values are always reverted as ONE pair.
    /// </summary>
    private const string? DefaultCustomAgentSystemPrompt = null;

    /// <summary>
    /// The comma-separated AGENTS.md file names the Improver is allowed to edit or create:
    /// the six role files (<c>coder.agents.md, tester.agents.md, reviewer.agents.md,
    /// improver.agents.md, orchestrator.agents.md, docwriter.agents.md</c>).
    /// <para>
    /// Rendered from the role VALUES via <see cref="WorkerRoleExtensions.ToRoleName"/>, which is
    /// the single source of truth for role file names — so these names are never spelled twice.
    /// The worker assembly references only <c>CopilotHive.Shared</c>, so this is the same role set
    /// the orchestrator exposes as <c>WorkerRoles.AgentRoles</c>.
    /// </para>
    /// </summary>
    private static readonly string ImproverAgentFileNames = string.Join(", ",
        new[]
        {
            WorkerRole.Coder, WorkerRole.Tester, WorkerRole.Reviewer,
            WorkerRole.Improver, WorkerRole.Orchestrator, WorkerRole.DocWriter,
        }.Select(r => $"{r.ToRoleName()}.agents.md"));

    private IToolCallBridge? _toolBridge;
    private string? _currentTaskId;
    private string? _currentGoalId;
    private WorkerRole _currentRole;

    /// <summary>
    /// GUIDANCE STORED PER ROLE — the AGENTS.md content delivered for each role by
    /// <see cref="SetCustomAgent"/>. A single value would let one role's guidance leak into another
    /// role's prompt (the role now comes from the ASSIGNMENT, and several roles run one after
    /// another on the same runner within a connection), so the runner keeps one entry per role and
    /// the prompt turn reads only the CURRENT role's entry — <c>null</c> when that role has none.
    /// <para>
    /// A plain <see cref="Dictionary{TKey,TValue}"/> is sufficient: runner calls are sequential and
    /// the whole lifecycle is covered by the quiescent-use contract documented on
    /// <see cref="ConnectAsync"/> (never called concurrently with a prompt turn, reset or disposal).
    /// </para>
    /// </summary>
    private readonly Dictionary<WorkerRole, string> _customAgentSystemPromptsByRole = [];
    private int _maxContextTokens = 150_000;
    private string? _compactionModel;
    private int? _compactionMaxTokens;
    private IReadOnlyList<SubAgentModelDto> _subAgentModels = [];

    // Test seams — nullable, set by tests only
    internal Func<string?, IChatClient>? ClientCreationSeam;
    internal Action<AgentOptions>? OnAgentOptionsCreated;
    internal Action<CodingAgent>? OnAgentCreated;

    /// <summary>Current agent session; set via <see cref="SetSession"/> before <see cref="SendPromptAsync"/>.</summary>
    private AgentSession? _session;

    /// <summary>
    /// THE PER-TASK USAGE SINK — the accumulation every model call of the CURRENT task reports into
    /// through <c>AgentOptions.OnUsage</c>. Never <c>null</c> once a task has been reset into.
    /// <para>
    /// <see cref="ResetTaskUsage"/> REPLACES this reference with a fresh instance instead of
    /// clearing the existing one. That is deliberate: SharpCoder may invoke a recorded call's
    /// <c>OnUsage</c> handler concurrently with, or even after, <c>ExecuteAsync</c> has returned
    /// (a sub-agent forwards its calls to the parent live). The handler bound to the PREVIOUS turn
    /// holds the OLD instance, so its late event lands in a sink nobody reads any more and can
    /// never leak into the next task's numbers.
    /// </para>
    /// <para>
    /// <see cref="SharpCoder.UsageSummary.Add"/> is thread-safe, so the field is only reassigned on
    /// the caller's sequential task boundary (the quiescent-use contract) while concurrent
    /// additions go to the instance captured by the running turn.
    /// </para>
    /// </summary>
    private UsageSummary _taskUsage = new();

    private TestResultReport? _lastTestReport;
    private WorkerReport? _lastWorkerReport;
    private string? _testerReport;

    /// <summary>
    /// The phase names <c>get_phase_report</c> accepts, in CANONICAL casing — the
    /// <see cref="PhaseReport.Phase"/> values the orchestrator writes. The tool matches the
    /// caller's argument against these case-insensitively and always renders a name in this
    /// casing, so a tool result never echoes the model's own capitalization back at it.
    /// </summary>
    private static readonly string[] PhaseReportPhaseNames =
        ["Coding", "Testing", "Review", "DocWriting", "Improve"];

    /// <summary>The exact text <c>get_phase_report</c> returns when the runner holds no reports.</summary>
    internal const string NoPhaseReportsAvailableMessage =
        "No completed phase reports are available for this iteration yet.";

    /// <summary>
    /// The completed-phase reports of the CURRENT assignment's iteration, as handed over by
    /// <c>TaskExecutor</c>. Empty when nothing was passed — replaced, never appended to, so a
    /// re-used runner can never show a previous assignment's reports.
    /// </summary>
    private IReadOnlyList<PhaseReport> _phaseReports = [];

    public TestResultReport? LastTestReport => _lastTestReport;
    public WorkerReport? LastWorkerReport => _lastWorkerReport;

    public void ClearTestReport() => _lastTestReport = null;
    public void ClearWorkerReport() => _lastWorkerReport = null;
    public void SetTesterReport(string? report) => _testerReport = report;

    /// <summary>
    /// Sets the completed-phase reports of the current iteration for THIS assignment — replacing
    /// whatever the previous assignment left behind, so the empty case really empties the runner
    /// (see <see cref="IAgentRunner.SetPhaseReports"/>).
    /// </summary>
    /// <param name="reports">The reports to store, or <c>null</c> for none.</param>
    public void SetPhaseReports(IReadOnlyList<PhaseReport>? reports) => _phaseReports = reports ?? [];

    public void SetToolBridge(IToolCallBridge? bridge) => _toolBridge = bridge;
    public void SetCurrentTaskId(string? taskId) => _currentTaskId = taskId;
    public void SetCurrentGoalId(string? goalId) => _currentGoalId = goalId;

    public void SetCustomAgent(WorkerRole role, string agentsMdContent)
    {
        _currentRole = role;
        _customAgentSystemPromptsByRole[role] = agentsMdContent;
        _testerReport = null;
    }

    /// <summary>
    /// Sets the role the CURRENT assignment runs as — the role travels ON the assignment, so this
    /// is the authoritative per-assignment role (see <see cref="IAgentRunner.SetRole"/>).
    /// <para>
    /// It sets <c>_currentRole</c> ONLY: the per-role guidance map is deliberately left untouched
    /// (a role's delivered guidance stays stored for that role) and the tester report field is
    /// deliberately NOT cleared — <c>SetCustomAgent</c> keeps its own clearing behavior, and the
    /// report lifecycle stays owned by <c>TaskExecutor</c>'s explicit tester-report block.
    /// </para>
    /// </summary>
    /// <param name="role">The role carried by the current assignment.</param>
    public void SetRole(WorkerRole role) => _currentRole = role;

    /// <summary>
    /// Starts a fresh per-task usage accumulation by REPLACING the sink with a new instance (see
    /// <see cref="_taskUsage"/> for why the old one is deliberately not cleared).
    /// </summary>
    /// <remarks>
    /// Called once at the start of every task by <c>TaskExecutor</c>, before the first prompt turn.
    /// The OLD sink object stays untouched: a late <c>OnUsage</c> event from the previous task
    /// (SharpCoder may fire it concurrently with, or after, <c>ExecuteAsync</c> returns — a
    /// sub-agent forwards its calls live) lands there and is unreachable from
    /// <see cref="GetTaskUsage"/> ever after.
    /// </remarks>
    public void ResetTaskUsage() => _taskUsage = new UsageSummary();

    /// <summary>
    /// Returns a DETACHED snapshot of the usage accumulated since the last
    /// <see cref="ResetTaskUsage"/> — never <c>null</c>, and never the live accumulation itself, so
    /// later model calls cannot change a value already handed out.
    /// </summary>
    /// <returns>The accumulated usage of the current task; an empty summary means "no model calls yet".</returns>
    public UsageSummary GetTaskUsage() => _taskUsage.Snapshot();

    /// <summary>
    /// The guidance stored for <paramref name="role"/>, or the CONSTRUCTOR DEFAULT
    /// (<see cref="DefaultCustomAgentSystemPrompt"/>) when that role has no stored guidance.
    /// <para>
    /// Reading PER ROLE is what keeps one role's delivered guidance out of another role's prompt:
    /// an <c>UpdateAgents</c> for the tester must never be appended to a coder (or reviewer, or
    /// doc-writer) prompt on the same connection.
    /// </para>
    /// </summary>
    private string? CustomAgentSystemPromptFor(WorkerRole role) =>
        _customAgentSystemPromptsByRole.TryGetValue(role, out var content)
            ? content
            : DefaultCustomAgentSystemPrompt;

    /// <inheritdoc/>
    public void SetMaxContextTokens(int maxTokens) =>
        _maxContextTokens = maxTokens > 0 ? maxTokens : 150_000;

    /// <inheritdoc/>
    public void SetCompactionModel(string? model) => _compactionModel = model;

    /// <inheritdoc/>
    public void SetCompactionMaxTokens(int? maxTokens) => _compactionMaxTokens = maxTokens;

    /// <inheritdoc/>
    public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) => _subAgentModels = models ?? [];

    /// <inheritdoc/>
    public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) =>
        _configProvisioner = provisioner;

    /// <summary>
    /// Builds the <see cref="SubAgentOptions"/> for the configured model catalog, or
    /// <c>null</c> when the catalog is empty (sub-agents disabled).
    /// </summary>
    internal SubAgentOptions? BuildSubAgentOptions()
    {
        if (_subAgentModels.Count == 0)
            return null;

        var subOpts = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 2,
            DefaultTimeout = TimeSpan.FromMinutes(5),
            MaxTimeout = TimeSpan.FromMinutes(15),
        };

        foreach (var m in _subAgentModels)
        {
            if (!string.IsNullOrWhiteSpace(m.Id))
                subOpts.AvailableModels.Add(new SubAgentModelInfo(m.Id, m.Description, m.ContextWindow, supportsVision: m.SupportsVision));
        }

        // ClientFactory delegates to the injectable seam, falling back to CreateChatClient.
        // Factory-created clients are owned and disposed by SubAgentManager — do NOT track them here.
        subOpts.ClientFactory = modelId =>
            _clientFactory?.Invoke(modelId)
            ?? ClientCreationSeam?.Invoke(modelId)
            ?? CreateChatClient(modelId);

        return subOpts;
    }

    /// <summary>
    /// Builds the full system prompt for the given <paramref name="role"/> by combining the
    /// hardcoded role prompt with any learned heuristics from <paramref name="agentsMdContent"/>.
    /// </summary>
    /// <param name="role">The worker role whose hardcoded prompt to use.</param>
    /// <param name="agentsMdContent">Optional AGENTS.md content to append as learned heuristics.</param>
    /// <returns>
    /// The combined system prompt string. If <paramref name="agentsMdContent"/> is non-empty,
    /// it is appended after a <c>\n\n# Learned Heuristics\n\n</c> separator.
    /// </returns>
    internal static string BuildRoleSystemPrompt(WorkerRole role, string? agentsMdContent)
    {
        // Validation-run discipline shared by the Coder and Tester roles. It teaches the agent
        // to pick an adequate foreground command budget and to preserve first-attempt evidence,
        // instead of hitting the default shell timeout and rerunning blindly. Role separation
        // is preserved: this block never directs the Coder to run the full test suite — the
        // Tester alone owns authoritative full validation.
        const string ValidationRunGuidance = """
            ## Foreground Command Budget and Validation Evidence

            Each `execute_bash_command` call starts a FRESH shell — there are no cross-call shell
            variables, exports, or background-job state, so never assume state carries between calls.
            Short targeted commands may keep the default timeout, but BEFORE any known multi-minute
            command (a full build, a full test-suite validation run, or other known long validation),
            you MUST select a larger budget by passing `timeout_ms=900000` (15 minutes, expressed in
            positive milliseconds) as an argument to `execute_bash_command`. This is a tool-call
            argument ONLY — not a dotnet CLI flag, not an AgentOptions property, not a sub-agent
            session timeout, and not a test assertion timeout.

            ### First-attempt evidence capture

            A long validation call can time out, and this SDK may then discard that call's captured
            stdout — so preserve evidence starting from the FIRST attempt:
            - Decide the absolute log path BEFORE launching the long call: allocate it in an earlier
              short call, or choose a recorded unique literal path. NEVER rely on a path printed only
              inside the long call's output, because that output may be discarded on timeout.
            - Write logs OUTSIDE tracked source (for example under /tmp), unique per attempt, and
              keep previous attempts separate.
            - Redirect stdout and stderr to the log and write an explicit completion marker (with the
              exit status) ONLY after the validation process has finished.
            - Preserve/export required evidence before container teardown. Logs surviving a command
              timeout do NOT imply process survival, crash survival, or container durability.

            ### Exit-status preservation

            Commands must preserve the original validation exit status, including nonzero outcomes:
            capture the status immediately, append the completion marker, optionally show a bounded
            tail for convenience, then exit with the ORIGINAL status. Forbid unguarded `tee`
            pipelines and `&&` chains that drop failure evidence, and NEVER treat a final tail or
            echo success as proof that the validation passed. A tail is only a display preview —
            inspect the COMPLETE log for counts, final summaries, and failure details. An existing
            build may be reused with `--no-build` only when it matches the tested revision and
            configuration — no stale-build shortcuts.

            ### Sequential execution and truthful reporting

            Run builds, tests, and any justified retries SEQUENTIALLY in one workspace — never
            background validation or run parallel duplicate validation. After a timeout, a
            cancellation, or a missing completion marker, treat validation as INCOMPLETE: check
            whether the prior process or its descendants are still running before rerunning, and
            never claim that a timeout guarantees process-tree cleanup. If completion cannot be
            established, report the interruption and the available evidence/log path — never PASS,
            and never invented failing-test counts — using FAIL with a clear "validation incomplete"
            explanation under your existing binary report contract, distinguished from observed
            assertion failures. NEVER combine multiple runs into one fictitious single-suite total.
            """;

        const string SharedPreamble = """
            INFRASTRUCTURE RULES (these are enforced by the system and cannot be overridden):
            - NEVER run `git push` — the infrastructure handles pushing automatically.
            - NEVER run `git checkout`, `git branch`, or `git switch` — the infrastructure handles branching.
            - When the goal description is ambiguous, files-to-change seem incomplete, or acceptance criteria conflict, call `request_clarification` instead of guessing.
            - Call `report_progress` at each meaningful step (e.g. "Reading files", "Building", "Tests passing", "Committing") so the user can follow your progress in real time.
            - Call `report_narrative` at the end of your work, before calling your report tool (report_code_changes, report_test_results, report_review_verdict, report_doc_changes). Write 2-5 sentences about what you tried, what worked, what you struggled with, and why. This helps the system learn and improve.
            - Call `raise_issue` when you notice code quality problems, bugs, suggestions, concerns, or workflow issues that are out of scope for the current goal. Do not fix them yourself unless they directly block the goal.
            - Earlier phases of this iteration may have completed already: call `get_phase_report` to read their full reports (e.g. evidence the goal required in the coder's or doc-writer's report) before asking for it via `request_clarification`.
            """;

        var roleSpecific = role switch
        {
            WorkerRole.Coder => $"""
                {SharedPreamble}

                # Coder

                You are a software developer. **Implement changes by editing files** — not describing them.
                Every task requires you to edit files, build, test, and commit. **Run a TARGETED subset of tests for your change** (the test skill's targeted-subset section — the namespaces/classes you touched), **NOT the full suite** — the tester phase runs the authoritative full suite; your targeted run is a pre-commit self-check. Do NOT attach a coverage collector; coverage is opt-in and not needed for your report.

                {ValidationRunGuidance}

                A text-only response without file edits is a **failure**.

                ## Reporting Your Changes (MANDATORY)

                After edits, builds, tests, and commits, you MUST call the `report_code_changes` tool with:
                - `verdict`: "PASS" if you successfully implemented and committed, "FAIL" if you could not
                - `filesModified`: array of files you changed (e.g. ["src/module.ext", "tests/moduleTests.ext"])
                - `summary`: put EVERYTHING relevant here — what you implemented, files changed and why,
                  decisions made, build/test status, any issues encountered. This is the sole output the
                  system reads; your text response is ignored.

                After calling the tool, respond with a single word only: `done` (or `fail` if verdict is FAIL).
                """,

            WorkerRole.Tester => $"""
                {SharedPreamble}

                # Tester

                You are a QA engineer responsible for comprehensive testing of the codebase. You go
                beyond unit tests — you verify that the system actually works as a whole. You are also
                the owner of missing test coverage: when the goal's changed behavior or acceptance
                criteria are not covered by existing tests, YOU write the missing unit/integration
                tests and improve inadequate existing tests yourself. Do not bounce missing coverage
                back to the Coder unless the goal explicitly excludes test files or explicitly limits
                the task to verification only.

                {ValidationRunGuidance}

                ## Test Authoring Responsibility

                Your role is verification PLUS authoring:
                - Write missing unit/integration tests for in-scope changed behavior and the goal's
                  acceptance criteria, and improve existing tests that are inadequate.
                - Inspect existing coverage FIRST. Add or repair only tests that are actually needed —
                  do not create redundant tests or edit test files when adequate coverage already exists.
                - Exception: genuine package/metadata-only verification tasks (where no behavior test
                  could cover the change) keep validation-only scope. Explicit goal, file, or
                  target-repository exclusions are always preserved.
                - Tests must assert intended behavior. Never encode a bug as expected behavior, never
                  weaken or skip a test to obtain PASS. Missing or broken production behavior remains
                  FAIL with evidence for the Coder. Production-source fixes and test seams outside
                  Tester authority go through `request_clarification`/the existing retry route — a
                  production-code freeze is NOT a test-code freeze.
                - Scope-conflict guard: if a generated instruction tells you this is
                  verification-only / no-test-edits but the governing goal does not support that
                  restriction, do NOT silently accept it as redefining your responsibility —
                  consult `get_goal` and call `request_clarification` about genuine scope conflicts.
                - Learned heuristics (guidance from agents.md files) must never narrow this hardcoded
                  role contract.

                ### Full-suite gate, then commit, then report

                You are the authoritative validation gate — the Coder only runs a targeted self-check:
                - ALWAYS run the authoritative full suite (the test skill's full-suite recipe) after
                  authoring or repairing tests, and before reporting. The authoritative full suite —
                  not a targeted subset — is what decides your verdict. Never substitute a filtered
                  or partial run for the authoritative full suite, no matter what other guidance says.
                - Then commit test changes you authored or repaired (`git add -A && git commit`)
                  BEFORE you call the mandatory `report_test_results` tool, so the tests you wrote are
                  published with your report. Never report without committing your test changes first.

                ## Acceptance Criteria Verification

                Beyond running build and tests, verify that the code changes actually address the goal's
                requirements. If the goal specifies structural changes (e.g. new UI layout, new API endpoints,
                new files) and those changes are absent or incomplete, report this in your summary and set
                verdict to FAIL. Passing existing tests is necessary but not sufficient — the goal's
                acceptance criteria must be met.

                Use `get_goal (no argument)` to fetch the full description if needed.

                ## Reporting Your Results (MANDATORY)

                After all testing, you MUST call the `report_test_results` tool with:
                - `verdict`: "PASS" or "FAIL"
                - `totalTests`: total number of tests run
                - `passedTests`: number that passed
                - `failedTests`: number that failed
                - `coveragePercent`: coverage percentage, or -1 if not measured
                - `buildSuccess`: true if the build succeeded
                - `issues`: array of issue descriptions (empty if none)
                - `summary`: put EVERYTHING relevant here — test counts, any failures with names and error
                  messages, build status, coverage, observations. This is the sole output the system reads;
                  your text response is ignored.

                NEVER report PASS if any test is failing.
                After calling the tool, respond with a single word only: `pass` or `fail`.
                """,

            WorkerRole.Reviewer => $"""
                {SharedPreamble}

                # Reviewer

                You are a senior code reviewer. Review diffs for correctness, quality, and convention
                adherence. Focus on bugs, security, logic errors, and maintainability — not style.

                Do NOT modify code or run `git push`.

                ## Acceptance Criteria Verification (MANDATORY)

                You MUST read the full goal description (use `get_goal (no argument)` to fetch it if needed).
                and verify that EVERY acceptance criterion is satisfied by the changes in the diff. If the diff
                is technically correct but only implements a fraction of the goal's requirements, that is a
                **[CRITICAL]** issue — you MUST REQUEST_CHANGES. Do not accept the brain's or coder's framing
                of "iteration scope" or "focused change" as a reason to skip acceptance criteria. The goal
                description is the sole source of truth for what must be delivered.

                ## Reporting Your Verdict (MANDATORY)

                After reviewing, you MUST call the `report_review_verdict` tool with:
                - `verdict`: "APPROVE" or "REQUEST_CHANGES"
                - `issues`: array of issue descriptions (prefix each with [CRITICAL], [MAJOR], or [MINOR])
                - `summary`: put EVERYTHING relevant here — your overall verdict with reasoning, each issue
                  with severity/location/description, and what was done well. This is the sole output the
                  system reads; your text response is ignored.

                - **APPROVE**: Code correct, ready for testing. Zero critical issues.
                - **REQUEST_CHANGES**: Critical or major issues must be fixed first.
                - **CRITICAL**: Bugs, security, data loss, missing files. Must fix.
                - **MAJOR**: Missing error handling, missing tests, API violations. Should fix.
                - **MINOR**: Naming, refactoring suggestions, doc gaps. Nice-to-have.
                After calling the tool, respond with a single word only: `approved` or `changes`.
                """,

            WorkerRole.DocWriter => $"""
                {SharedPreamble}

                # Doc Writer

                You are a technical documentation specialist. Your job is to update project documentation
                to reflect code changes made on the current feature branch.

                Do NOT edit source code files. Do NOT write or modify test code. Do NOT run tests or build.

                ## Reporting Your Changes (MANDATORY)

                After your work, you MUST call the `report_doc_changes` tool with:
                - `verdict`: "PASS" if you successfully updated documentation, "FAIL" if you could not
                - `filesUpdated`: array of files you changed (e.g. ["CHANGELOG.md", "README.md"])
                - `summary`: put EVERYTHING relevant here — which files were updated and what changed in
                  each, changelog entries added, decisions about scope. This is the sole output the system
                  reads; your text response is ignored.

                After calling the tool, respond with a single word only: `done` (or `fail` if verdict is FAIL).
                """,

            WorkerRole.Improver => $"""
                {SharedPreamble}

                # Improver

                You are an expert at analysing software development iteration outcomes and improving
                agent instructions to produce better results in the next iteration.

                You have direct access to the `agents/` folder containing `*.agents.md` files.
                Use the file tools (view, edit) to read and modify these files directly.
                You **cannot** run shell commands — file reading and editing only.
                If you delegate work to sub-agents, they can request file-write access by passing
                `enable_file_writes=true` in the `start_sub_agent` call. Your file-write capability
                is enabled, so sub-agents that request it will be granted write access. Only bash
                is disabled for you and your sub-agents. Do not ask the orchestrator to apply file
                edits on your behalf — use your own file tools or delegate to a sub-agent with
                `enable_file_writes=true`.

                ## Guidance update policy: append new, compress old

                Each `*.agents.md` file MUST NOT exceed {WorkerConstants.AgentsMdMaxCharacters} characters.
                Size is measured in characters (UTF-16 code units) of the file's full text.

                Follow this policy exactly:

                1. **Look before you edit.** Read the existing files and check their current sizes
                   first — call the `get_file_sizes` tool before making any edit, and call it again
                   after you re-read a file you changed. The agents/ folder may contain only a few
                   files or none at all, and a missing file counts as empty (0 characters); the same
                   {WorkerConstants.AgentsMdMaxCharacters}-character limit applies to a newly created file.
                2. **Only genuinely new lessons.** Formulate only lessons that are genuinely new,
                   broadly applicable, and not already covered by an existing rule. Write them in
                   concise, readable Markdown.
                3. **Append at the end.** Add new lessons at the END of the file. Never interleave
                   them among the older rules and never prepend them.
                4. **New lessons are frozen.** Keep each newly appended lesson unchanged
                   byte-for-byte for the rest of this task, including during any later attempt to
                   reduce the file back under the limit.
                5. **Make room from the top.** When a file is over the limit, work from the TOP of
                   the existing/older material downward: first consolidate and compress the older
                   rules, then, only if that is not enough, remove the oldest material that is
                   redundant or obsolete.
                6. **Never take these shortcuts.** Do not meet the cap by truncating a whole file,
                   by modifying, removing or reordering the new lessons, by weakening protected
                   safety constraints, or by turning guidance into cryptic abbreviations or
                   symbol-heavy shorthand. Keep concise ordinary-language bullets and useful
                   headings that a new reader can understand.
                7. **Touch as little as possible.** Do not rewrite files that your lessons do not
                   affect, do not pad a file to fill unused capacity, and do not churn old material
                   when there is no need to.
                8. **Verify by re-reading.** After editing, re-read each changed file and confirm
                   that the new lessons are intact and that the older guidance is still readable.

                Do NOT add "Iteration History" or changelog-style entries to agents.md files. agents.md files contain
                guidance rules and quality standards, not logs of past iterations. Extract actionable lessons from the
                iteration and add them as guidance rules (e.g., "Always check X before Y", "When doing Z, prefer approach
                A over B"). If a lesson was already captured in a previous iteration, do not duplicate it.

                Guidance rules added to agents.md files must be GENERAL, broadly-applicable software-development patterns
                and coding-style conventions that apply across the codebase, across goals, and across different files and
                contexts. Positive examples include "Always verify a resource is released in every code path",
                "Prefer try/finally over manual cleanup", and "Use a shared helper instead of duplicating logic". Negative
                examples that must NOT be added include "never do X in ServiceY.cs", "when handling paste state for Z", or
                "the TCS-block in ClassB". Those are too narrow: they are tied to a specific file, class, method, past
                iteration, or incident and do not generalize. Existing general rules are fine; do not rewrite them into one-off
                incident advice.

                **Never remove or weaken safety constraints** — do not remove instructions about git workflow,
                test requirements or output format compliance.

                Only edit files inside the agents/ folder, and only these six exact file names
                (one per role): {ImproverAgentFileNames}. If the file for a role you have a
                lesson for does not exist, create it with exactly that file name — creating a
                missing role file is allowed. Do not create any other file, do not rename files,
                and do not touch anything outside the agents/ folder.
                """,

            WorkerRole.Unspecified => SharedPreamble,

            _ => throw new InvalidOperationException($"No hardcoded system prompt defined for WorkerRole '{role}'."),
        };

        if (string.IsNullOrWhiteSpace(agentsMdContent))
            return roleSpecific;

        return roleSpecific + "\n\n# Learned Heuristics\n\n" + agentsMdContent;
    }

    /// <inheritdoc/>
    public void SetSession(object? session) => _session = session as AgentSession;

    /// <inheritdoc/>
    public object? GetSession() => _session;

    /// <inheritdoc/>
    public int GetContextUsagePercent()
    {
        if (_session == null) return 0;

        var contextDenominator = (double)_maxContextTokens;
        var tokens = _session.LastKnownContextTokens > 0
            ? _session.LastKnownContextTokens
            : _session.EstimatedContextTokens;
        return (int)Math.Min(100, (tokens * 100.0) / contextDenominator);
    }

    /// <summary>
    /// Prepares the runner for a NEW connection. It deliberately creates NO LLM client (the client is
    /// created lazily on the first prompt, after credentials have been provisioned) and it neither
    /// re-creates nor disposes the client-lifecycle gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>QUIESCENT-USE CONTRACT.</b> This method is safe for QUIESCENT SEQUENTIAL connection
    /// preparation: the caller must invoke it only when no prompt turn, reset or disposal is in
    /// flight on this instance (the worker runs one attempt at a time and the previous attempt's
    /// <c>RunAsync</c> is fully wound down, including its lexical transport disposal, before the next
    /// one starts). It performs NO synchronization of its own, so concurrent use is NOT a supported
    /// contract.
    /// </para>
    /// <para>
    /// <b>ON A DISPOSED RUNNER.</b> It NEVER RESURRECTS one: it creates no LLM client, it neither
    /// re-creates nor disposes <c>_clientLifecycleGate</c>, and it does not clear the one-way disposed
    /// flag — so a disposed runner stays disposed and the next <see cref="SendPromptAsync"/> still
    /// fails with the existing disposal error. It is deliberately NOT described as a total no-op: it
    /// still writes the per-connection preparation fields below (they are unreachable state on a
    /// runner that can never run again, so writing them changes no observable behavior).
    /// </para>
    /// <para>
    /// <b>WHAT IT RESETS, AND WHAT IT DELIBERATELY DOES NOT.</b> It restores the CONSTRUCTOR
    /// DEFAULTS of the per-connection role state — <c>_currentRole</c> plus the PER-ROLE guidance
    /// map <c>_customAgentSystemPromptsByRole</c> (cleared to the empty map) — and clears
    /// <c>_testerReport</c>, so preparation for a new connection never inherits the previous
    /// connection's <c>UpdateAgents</c> role/guidance or a stale tester report. It is deliberately
    /// NOT a per-assignment reset: applying the constructor defaults HERE only (never before each
    /// assignment) is what keeps a connection's delivered <c>UpdateAgents</c> guidance authoritative
    /// for that connection's assignments, and it is why the per-assignment role
    /// (<see cref="SetRole"/>) is applied later, by <c>TaskExecutor</c>, without reverting that
    /// guidance.
    /// </para>
    /// <para>
    /// Everything else stays the per-assignment setup's responsibility — the existing
    /// <see cref="ResetSessionAsync"/> plus <c>TaskExecutor</c> own model, reasoning effort, session
    /// restore/reset, the tool bridge, task/goal identity, reports, compaction and sub-agent
    /// settings. The previous chat client and session may legitimately stay in place during an idle
    /// backoff, and the next assignment still resets before any prompt, so this promises NO complete
    /// fresh-runner state equivalence outside that supported execution path.
    /// </para>
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        // Deliberately creates NO client. Worker containers hold no LLM credentials of their
        // own: the credentials are provisioned by the orchestrator, and that provisioning
        // fetch runs immediately before the FIRST client creation, which happens lazily in
        // SendPromptAsync. Creating a client here would run before provisioning and before the
        // operator has necessarily signed in.
        //
        // Prepare for a NEW connection in the QUIESCENT state documented above: restore the
        // constructor defaults of the per-connection role state and drop any stale tester report, so
        // this connection starts from the same role/prompt baseline a fresh runner would have. A
        // delivered UpdateAgents on this connection is applied later, by the message loop, and is
        // never reverted — that is why this reset lives here and NOT before each assignment. The
        // assignment's own role (SetRole) is likewise applied later, per assignment, as are the
        // assignment's own phase reports (SetPhaseReports).
        _currentRole = DefaultRole;
        _customAgentSystemPromptsByRole.Clear();
        _testerReport = null;
        _phaseReports = [];

        _log.Info("SharpCoderRunner ready — the LLM client is created lazily on first prompt.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tears down the current LLM client and session so the next <see cref="SendPromptAsync"/>
    /// creates a fresh client for <paramref name="model"/>.
    /// <para>
    /// The client field is set to <c>null</c> BEFORE <see cref="IDisposable.Dispose"/> is called,
    /// so a throwing disposal can never leave the field pointing at a half-disposed client that a
    /// later task would inherit. Disposal is therefore idempotent: a second call has nothing left
    /// to dispose. A disposal exception PROPAGATES — this is a hard teardown and a failure here is
    /// a fault, not something to swallow.
    /// </para>
    /// <para>
    /// The whole detach-and-dispose runs under <see cref="_clientLifecycleGate"/>, which also
    /// guards lazy creation in <see cref="SendPromptAsync"/>. That makes it impossible for a
    /// second task to observe or create a client while this task's dispose is in flight.
    /// The gate is released in a <c>finally</c> so a propagating disposal never strands it.
    /// </para>
    /// </summary>
    /// <param name="model">The model for the next client, or <c>null</c> for the SDK default.</param>
    /// <param name="reasoningEffort">The explicitly transported reasoning effort, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
    {
        await _clientLifecycleGate.WaitAsync(ct);
        try
        {
            // Reasoning effort is transported explicitly by the orchestrator; it is never derived
            // from the model name.
            _currentReasoning = reasoningEffort;
            _pendingModel = model;

            _log.Info($"Resetting session. Requested model: {model ?? "default"}" +
                (_currentReasoning.HasValue ? $", reasoning={_currentReasoning.Value}" : ", reasoning=(none)"));

            _session = null;

            // Null the field FIRST, then dispose the detached reference.
            var previous = _chatClient;
            _chatClient = null;
            previous?.Dispose();
        }
        finally
        {
            _clientLifecycleGate.Release();
        }
    }

    /// <summary>
    /// Creates the LLM client lazily, running orchestrator provisioning UNCONDITIONALLY
    /// immediately beforehand (never only when a credential looks absent), so a token that
    /// became available after the worker registered is picked up.
    /// <para>
    /// The caller MUST hold <see cref="_clientLifecycleGate"/>. <paramref name="model"/> is read
    /// once by the caller under that gate and passed in, so provisioning and construction can
    /// never straddle an intervening reset and provision model A while constructing model B.
    /// </para>
    /// </summary>
    private async Task<IChatClient> CreateClientLazilyAsync(string? model, CancellationToken ct)
    {
        if (_configProvisioner is not null)
            await _configProvisioner(model, ct);

        return _clientFactory?.Invoke(model)
            ?? ClientCreationSeam?.Invoke(model)
            ?? CreateChatClient(model);
    }

    /// <summary>
    /// Acquires the client for this prompt under <see cref="_clientLifecycleGate"/>, creating it
    /// lazily on first use.
    /// <para>
    /// The gate is deliberately NOT released here. It is held for the ENTIRE duration of the
    /// turn and released by the caller's <c>finally</c>, so <see cref="ResetSessionAsync"/> and
    /// <see cref="DisposeAsync"/> cannot null and dispose the client while a turn still holds
    /// the returned reference. Releasing at acquisition time (as an earlier revision did) left
    /// the returned reference unprotected for the whole of the agent run.
    /// </para>
    /// </summary>
    /// <returns>The client to use for this turn. The caller owns the gate until it releases it.</returns>
    private async Task<IChatClient> AcquireClientLeaseAsync(CancellationToken ct)
    {
        await _clientLifecycleGate.WaitAsync(ct);
        try
        {
            // Read the pending model under the gate, so provisioning and construction agree.
            _chatClient ??= await CreateClientLazilyAsync(_pendingModel, ct);
            return _chatClient;
        }
        catch
        {
            // Creation failed: release the lease here, because the caller never receives a
            // reference and therefore has no finally to run.
            _clientLifecycleGate.Release();
            throw;
        }
    }

    public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
    {
        // Lazy first creation, serialized: a fresh task never inherits another task's disposed
        // client, and two overlapping assignments can never both construct one. The lease is
        // held until the result has left the runner, so reset/dispose cannot overlap actual use.
        var chatClient = await AcquireClientLeaseAsync(ct);
        try
        {
            return await RunPromptTurnAsync(chatClient, prompt, workDir, ct);
        }
        finally
        {
            _clientLifecycleGate.Release();
        }
    }

    /// <summary>
    /// Runs one full agent turn against <paramref name="chatClient"/>. The caller holds the
    /// client lifecycle lease for the whole call, so the client cannot be disposed underneath it.
    /// </summary>
    private async Task<string> RunPromptTurnAsync(
        IChatClient chatClient, string prompt, string workDir, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        _log.Info($"Executing task as {_currentRole} with model {_currentModel}. WorkDir: {workDir}");

        var options = new AgentOptions
        {
            WorkDirectory = workDir,
            MaxSteps = 500,
            MaxContextTokens = _maxContextTokens,
            SystemPrompt = BuildRoleSystemPrompt(_currentRole, CustomAgentSystemPromptFor(_currentRole)),
            CustomTools = BuildCustomTools(ct),
            EnableBash = _currentRole != WorkerRole.Improver,
            EnableFileWrites = _currentRole != WorkerRole.Reviewer,
            ReasoningEffort = _currentReasoning,
            ShowToolCallsInStream = true,
        };

        var subAgentOptions = BuildSubAgentOptions();
        if (subAgentOptions != null)
            options.SubAgents = subAgentOptions;

        if (!string.IsNullOrEmpty(_compactionModel))
            options.CompactionClient = ChatClientFactory.Create(_compactionModel);

        if (_compactionMaxTokens.HasValue)
            options.CompactionMaxTokens = _compactionMaxTokens.Value;

        // THE PER-TASK USAGE WIRING. The sink is captured HERE, in a local, and bound to OnUsage:
        // every model call of this turn — agent, compaction and sub-agent calls alike — reports
        // itself through this handler, so recording needs no per-path code and covers a turn that
        // ends with status Error (AgentTurnFailedException), MaxStepsReached or an exception.
        // UsageSummary.Add is thread-safe (SharpCoder may invoke the handler concurrently, and even
        // after ExecuteAsync returns).
        //
        // Set BEFORE OnAgentOptionsCreated so the test seam can observe the wired handler.
        var taskUsage = _taskUsage;
        options.OnUsage = taskUsage.Add;

        OnAgentOptionsCreated?.Invoke(options);

        // Write pre-execution diagnostics so we can inspect inputs even if the LLM call hangs or is killed
        WriteDiagnosticsFile(null, prompt, TimeSpan.Zero, options, "pre");

        // Use the leased reference. The caller holds _clientLifecycleGate for this whole turn,
        // so ResetSessionAsync/DisposeAsync cannot null and dispose this client underneath us.
        await using var agent = new CodingAgent(chatClient, options);

        OnAgentCreated?.Invoke(agent);

        // Ensure session exists before streaming
        _session ??= AgentSession.Create(Guid.NewGuid().ToString("N"));

        // Drain the streaming response to update LastKnownContextTokens after each LLM turn
        var result = await DrainStreamingAsync(agent, _session, prompt, _log, ct);

        stopwatch.Stop();
        var elapsedSecs = stopwatch.Elapsed.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _log.Info($"Task finished in {elapsedSecs}s (status={result.Status}, toolCalls={result.ToolCallCount})");

        // Log diagnostics when available
        if (result.Diagnostics is { } diag)
        {
            _log.Info($"Diagnostics: systemPrompt={diag.SystemPrompt.Length} chars, userMessage={diag.UserMessage.Length} chars, " +
                      $"historyMessages={diag.SessionHistoryCount}, totalMessages={diag.TotalMessageCount}");
            _log.Info($"Diagnostics: tools=[{string.Join(", ", diag.ToolNames)}], bash={diag.EnableBash}, " +
                      $"fileWrites={diag.EnableFileWrites}, skills={diag.SkillsEnabled}, autoWorkspace={diag.AutoLoadedWorkspaceInstructions}");
        }

        // Write post-execution diagnostics with full result
        WriteDiagnosticsFile(result, prompt, stopwatch.Elapsed, options, "post");

        _log.Info($"AgentResult: status={result.Status}, toolCalls={result.ToolCallCount}, model={result.ModelId}, finish={result.FinishReason}");
        if (result.Usage != null)
        {
            _log.Info($"Context: inputTokens={result.Usage.InputTokenCount}, outputTokens={result.Usage.OutputTokenCount}, totalTokens={result.Usage.TotalTokenCount}");
        }
        if (result.Messages != null)
        {
            _log.Info($"AgentResult: {result.Messages.Count} messages total");
            foreach (var msg in result.Messages)
            {
                _log.Info($"  [{msg.Role}] {SummarizeMessage(msg)}");
            }
        }

        if (result.Status == "Error")
        {
            // THE one status whose Message is NOT agent output. SharpCoder reports most provider
            // failures as a Completed/Error result carrying the PROVIDER's failure text, and that text
            // can echo a provisioned secret — WorkerLogger.Error goes to Console.Error, i.e. the
            // container's stderr, which is shipped with the worker's logs. So the line names the status
            // and nothing else. It deliberately does NOT quote the omitted text even as a label, so
            // nothing that looks like an echo of the provider's wording can appear on stderr.
            _log.Error($"Agent finished with non-success status: {result.Status} (provider error text omitted)");
        }
        else if (result.Status != "Success")
        {
            // Every other non-success status (MaxStepsReached, ...) is the agent's OWN partial output:
            // its content is logged exactly as before.
            _log.Error($"Agent finished with non-success status: {result.Status} - {result.Message}");
        }

        // An "Error" result is a PROVIDER FAILURE, not agent output: SharpCoder reports most provider
        // failures that way (only OperationCanceledException, HttpRequestException and
        // ObjectDisposedException propagate as real exceptions). Returning result.Message would hand
        // the provider's failure text to TaskExecutor as the turn's normal output, which would treat
        // it as a completed phase and report TaskOutcome.Completed. Throwing routes the turn through
        // TaskExecutor's EXISTING generic catch: TaskOutcome.Failed, a FAIL verdict, no session save.
        //
        // The exception deliberately carries NO provider text — result.Message can echo a provisioned
        // secret and travels to the orchestrator (see SafeExceptionLog and AgentTurnFailedException).
        //
        // MaxStepsReached (and any other non-success status) keeps its previous behavior: the status
        // is logged above and the partial text is returned; the worker's report tools decide its verdict.
        if (result.Status == "Error")
            throw new AgentTurnFailedException();

        return result.Message;
    }

    /// <summary>
    /// Drains the streaming response from the agent, extracting the final AgentResult.
    /// This method ensures LastKnownContextTokens is updated after every LLM turn.
    /// </summary>
    private static async Task<AgentResult> DrainStreamingAsync(
        CodingAgent agent, AgentSession session, string prompt, WorkerLogger log, CancellationToken ct)
    {
        AgentResult? result = null;
        await foreach (var update in agent.ExecuteStreamingAsync(session, prompt, ct))
        {
            LogToolCallDelta(update, log);

            if (update.Kind == StreamingUpdateKind.Completed)
            {
                result = update.Result;
            }
            // Ordinary chat TextDeltas are deliberately discarded — no output is
            // streamed in worker context; only tool-call envelope lines are logged.
        }

        if (result == null)
            throw new InvalidOperationException("Streaming execution completed without a final AgentResult.");

        return result;
    }

    /// <summary>
    /// Logs a single streaming update to the console if it is a tool-call envelope delta.
    /// <para>
    /// With <c>ShowToolCallsInStream</c> enabled, SharpCoder's <see cref="CodingAgent"/> emits
    /// each tool call as an envelope TextDelta of the form `` "\n\n`🔧 Name(args)`\n" ``.
    /// A delta is recognized as a tool-call line iff, after <c>Trim()</c>, the text starts with
    /// the prefix backtick + 🔧 (U+1F527) + a single space, ends with a closing backtick, and
    /// contains no inner LF/CR. The LF/CR check runs BEFORE sanitization: a delta with an
    /// embedded line break is rejected, never sanitized-then-logged.
    /// </para>
    /// <para>
    /// Accepted deltas are logged as <c>🔧 Name(args)</c> — surrounding backticks stripped —
    /// after control-character sanitization via <see cref="LogSanitizer.SanitizeText"/> (console
    /// line-injection prevention only; no credential redaction, no additional truncation — the
    /// upstream <c>FormatToolCallArgs</c> already caps the args). Everything that is not a
    /// recognized tool-call TextDelta (ordinary chat deltas, Completed, etc.) is a no-op.
    /// </para>
    /// </summary>
    internal static void LogToolCallDelta(StreamingUpdate update, WorkerLogger log)
    {
        if (update.Kind != StreamingUpdateKind.TextDelta)
            return;

        var text = update.Text;
        if (string.IsNullOrEmpty(text))
            return;

        var trimmed = text.Trim();

        // Recognition requires the exact envelope: `🔧 Name(args)`
        const string Prefix = "`\uD83D\uDD27 "; // backtick + wrench emoji (U+1F527) + single space
        const char EnvelopeClose = '`';

        // LF/CR check BEFORE sanitization — an inner line break rejects the delta outright
        if (trimmed.IndexOf('\n') >= 0 || trimmed.IndexOf('\r') >= 0)
            return;

        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            return;

        if (trimmed.Length < Prefix.Length + 1 || trimmed[^1] != EnvelopeClose)
            return;

        // Strip the surrounding envelope backticks (first and last character)
        var logged = trimmed[1..^1];
        log.Info(LogSanitizer.SanitizeText(logged));
    }

    /// <summary>
    /// Disposes the owned LLM client. The field is nulled BEFORE disposal so a throwing
    /// <see cref="IDisposable.Dispose"/> can never leave a reference to a half-disposed client
    /// behind, which also makes repeated disposal safe.
    /// <para>
    /// Runs under <see cref="_clientLifecycleGate"/> so final teardown cannot overlap a task's
    /// lazy creation or a reset. The client disposal PROPAGATES (callers rely on that), so the
    /// gate is released in a <c>finally</c>. Repeat calls are no-ops: the gate itself is only
    /// disposed once, after the client disposal has been attempted.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Idempotent: a second call must not fault on the already-disposed gate.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _clientLifecycleGate.WaitAsync();
        try
        {
            var previous = _chatClient;
            _chatClient = null;
            previous?.Dispose();
        }
        finally
        {
            _clientLifecycleGate.Release();
            _clientLifecycleGate.Dispose();
        }
    }

    private static string SummarizeMessage(ChatMessage msg)
    {
        const int ArgValueMaxLength = 100;
        const int PreviewMaxLength = 200;

        var functionCall = msg.Contents?.OfType<FunctionCallContent>().FirstOrDefault();
        if (functionCall != null)
        {
            var firstArg = functionCall.Arguments?.FirstOrDefault();
            if (firstArg.HasValue)
            {
                var argValue = firstArg.Value.Value?.ToString() ?? string.Empty;
                if (argValue.Length > ArgValueMaxLength)
                    argValue = argValue.Substring(0, ArgValueMaxLength);
                return $"tool:{functionCall.Name}({firstArg.Value.Key}=\"{argValue}\")";
            }
            return $"tool:{functionCall.Name}()";
        }

        var functionResult = msg.Contents?.OfType<FunctionResultContent>().FirstOrDefault();
        if (functionResult != null)
        {
            var raw = functionResult.Result?.ToString() ?? string.Empty;
            return $"result:{functionResult.CallId} \u2192 {SummarizeToolResult(raw)}";
        }

        var text = msg.Text;
        if (text != null && text.Length > PreviewMaxLength)
            text = text.Substring(0, PreviewMaxLength);
        return text ?? string.Empty;
    }

    /// <summary>
    /// Produces a compact one-line summary of a tool result instead of dumping raw content.
    /// </summary>
    private static string SummarizeToolResult(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "(empty)";

        var lines = raw.Split('\n');
        var lineCount = lines.Length;
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(raw);

        // Short single-line results can be shown inline
        if (lineCount == 1 && raw.Length <= 120)
            return $"\"{raw}\"";

        return $"{byteCount} bytes, {lineCount} lines";
    }

    private static readonly string DiagnosticsDir =
        Environment.GetEnvironmentVariable("DIAGNOSTICS_DIR") ?? Path.Combine(Path.GetTempPath(), "copilothive-diagnostics");

    /// <summary>
    /// THE placeholder written as <c>agentResponse</c> in the diagnostics document in place of an
    /// Error result's message. Fixed text: it never echoes any part of the provider's failure text.
    /// </summary>
    internal const string OmittedProviderFailureText = "(omitted: provider failure text)";

    /// <summary>
    /// The agent response that may be PERSISTED for <paramref name="result"/>.
    /// <para>
    /// For <c>Status == "Error"</c> the result's message is the PROVIDER's failure text, not agent
    /// output. SharpCoder reports most provider failures as a Completed/Error result carrying whatever
    /// the provider returned, and that text can echo a provisioned secret (see the
    /// <see cref="SafeExceptionLog"/> rationale). The post-execution diagnostics JSON under
    /// <c>DIAGNOSTICS_DIR</c> must therefore write <see cref="OmittedProviderFailureText"/> instead;
    /// <c>RunPromptTurnAsync</c>'s own closing stderr line applies the same rule by naming the status
    /// alone.
    /// </para>
    /// <para>
    /// Every other status is UNCHANGED: <c>Success</c> is the agent's answer and <c>MaxStepsReached</c>
    /// (or any other status) carries the agent's own partial output, which already travels to the
    /// orchestrator, so its message is returned verbatim. A <c>null</c> result yields <c>null</c>.
    /// </para>
    /// </summary>
    internal static string? SafeAgentResponse(AgentResult? result)
        => result?.Status == "Error" ? OmittedProviderFailureText : result?.Message;

    private void WriteDiagnosticsFile(AgentResult? result, string userPrompt, TimeSpan elapsed, AgentOptions options, string phase)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsDir);

            var taskId = _currentTaskId ?? "unknown";
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = $"{timestamp}_{taskId}_{phase}.json";
            var filePath = Path.Combine(DiagnosticsDir, fileName);

            var toolNames = options.CustomTools
                .Select(t => t is AIFunction f ? f.Name : t.GetType().Name)
                .ToList();

            // For pre-execution: use options directly; for post: use diagnostics from result
            var diag = result?.Diagnostics;
            var doc = new
            {
                phase,
                taskId,
                role = _currentRole.ToString(),
                model = _currentModel,
                reasoning = _currentReasoning?.ToString(),
                timestamp = DateTimeOffset.UtcNow,
                elapsedSeconds = elapsed.TotalSeconds,
                status = result?.Status,
                toolCallCount = result?.ToolCallCount,
                finishReason = result?.FinishReason?.ToString(),
                usage = result?.Usage is { } u ? new
                {
                    inputTokens = u.InputTokenCount,
                    outputTokens = u.OutputTokenCount,
                    totalTokens = u.TotalTokenCount
                } : null,
                session = new
                {
                    sessionHistoryCount = diag?.SessionHistoryCount ?? 0,
                    totalMessageCount = diag?.TotalMessageCount ?? 0,
                    maxSteps = options.MaxSteps,
                    enableBash = options.EnableBash,
                    enableFileWrites = options.EnableFileWrites,
                    autoLoadedWorkspaceInstructions = options.AutoLoadWorkspaceInstructions,
                    skillsEnabled = options.EnableSkills,
                    reasoningEffort = options.ReasoningEffort?.ToString(),
                    workDirectory = options.WorkDirectory,
                    customToolNames = toolNames,
                    allToolNames = diag?.ToolNames ?? (IReadOnlyList<string>)toolNames
                },
                systemPrompt = diag?.SystemPrompt ?? options.SystemPrompt ?? "(not yet assembled)",
                userMessage = userPrompt,
                // An Error result's message here is provider failure text (it can echo a provisioned
                // secret), so it is replaced by a fixed placeholder; every other status writes
                // result.Message exactly as before.
                agentResponse = SafeAgentResponse(result)
            };

            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(filePath, json);
            _log.Info($"Diagnostics ({phase}) written to {filePath} ({json.Length} bytes)");
        }
        catch (Exception ex)
        {
            // Sanitized: the diagnostics document embeds the assembled prompt and the agent
            // result, so a serialization failure can quote provisioned content back in its
            // message. Only the exception classification is logged.
            _log.Error($"Failed to write diagnostics file [{SafeExceptionLog.Describe(ex)}]");
        }
    }

    private IChatClient CreateChatClient(string? modelOverride = null)
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel(modelOverride);
        _currentModel = model ?? "(default)";
        _log.Info($"Creating chat client: provider={provider}, model={_currentModel}" +
            (_currentReasoning.HasValue ? $", reasoning={_currentReasoning.Value}" : ""));
        return ChatClientFactory.Create(modelOverride);
    }

    /// <summary>
    /// Builds the custom tool set for this turn.
    /// <para>
    /// <paramref name="ct"/> is the ASSIGNMENT'S token and MUST be forwarded to every bridge
    /// call. The bridge registers <c>ct.Register(() =&gt; tcs.TrySetCanceled())</c> on each pending
    /// tool request, so a tool that is waiting for a <c>ToolResponse</c> is only released when a
    /// live token is cancelled. Passing <see cref="CancellationToken.None"/> here (as an earlier
    /// revision did) permanently detached those waits: cancelling the assignment could not
    /// release a pending <c>request_clarification</c> / <c>get_goal</c> / <c>raise_issue</c>, so
    /// the drain in <c>WorkerService.ProcessMessagesAsync</c> blocked forever while this runner
    /// still held the full-turn client lease — deadlocking teardown and preventing disposal.
    /// </para>
    /// <para>
    /// CONSTRUCTION-TIME CONTEXT. The five bridge-backed tools close over the bridge, task ID and
    /// goal ID captured HERE, not over the mutable runner fields. A tool set is built once per turn
    /// (see <c>RunPromptTurnAsync</c>), so its tools belong to the assignment this call was made for
    /// even if the runner's fields are later repointed at another assignment.
    /// </para>
    /// </summary>
    /// <param name="ct">The assignment's cancellation token, forwarded to every bridge call.</param>
    private IList<AITool> BuildCustomTools(CancellationToken ct)
    {
        var tools = new List<AITool>();

        // CONSTRUCTION-TIME BINDING. The bridge and the assignment IDs are MUTABLE runner state that
        // the next assignment overwrites (<see cref="SetToolBridge"/>, <see cref="SetCurrentTaskId"/>,
        // <see cref="SetCurrentGoalId"/>). Reading the fields from inside a delegate would therefore
        // re-read whichever assignment is current WHEN the tool is INVOKED, silently retargeting a
        // retained tool built for assignment A onto assignment B's bridge and IDs. Capturing them into
        // locals HERE — before the guard and before the delegates are created — closes over THIS
        // turn's context instead, so an already-built tool stays bound to the assignment it was built
        // for, while a later built set uses that turn's own values.
        //
        // The captured BRIDGE value (not the field) is used for both the guard and the delegates: a
        // later SetToolBridge(null) must not be able to null out the dependency of a tool that was
        // already constructed against a live bridge.
        var bridge = _toolBridge;
        var taskId = _currentTaskId;
        var goalId = _currentGoalId;

        // The SAME construction-time binding for the completed-phase reports: a tool set is built
        // once per turn, and this turn's tools must answer from THIS assignment's reports even if
        // the runner is repointed at another assignment before a retained tool is invoked.
        var phaseReports = _phaseReports;

        if (bridge != null)
        {
            tools.Add(AIFunctionFactory.Create(
                async ([Description("Short status summary")] string status,
                       [Description("Detailed progress explanation")] string details) =>
                {
                    if (string.IsNullOrEmpty(taskId)) return "Error: Task ID not set.";
                    _log.Info($"Tool call: report_progress({status})");
                    await bridge.ReportProgressAsync(taskId, status, details, ct);
                    return "Progress reported.";
                },
                "report_progress",
                "Report current progress to the orchestrator."
            ));

            tools.Add(AIFunctionFactory.Create(
                async ([Description("2-5 sentence narrative of what you tried, what worked, what you struggled with, and why")] string narrative) =>
                {
                    if (string.IsNullOrEmpty(taskId)) return "Error: Task ID not set.";
                    _log.Info("Tool call: report_narrative()");
                    await bridge.ReportNarrativeAsync(taskId, narrative, ct);
                    return "Narrative recorded.";
                },
                "report_narrative",
                "Report a narrative summary of your work experience to help the system learn and improve."
            ));

            tools.Add(AIFunctionFactory.Create(
                async ([Description("The question to ask the orchestrator")] string question) =>
                {
                    if (string.IsNullOrEmpty(taskId)) return "Error: Task ID not set.";
                    _log.Info($"Tool call: request_clarification({question})");
                    var response = await bridge.RequestClarificationAsync(taskId, question, ct);
                    return response;
                },
                "request_clarification",
                "Ask the orchestrator for clarification when the goal description is ambiguous, files-to-change seem incomplete, or acceptance criteria conflict. Do NOT silently work around ambiguities — ask first."
            ));

            tools.Add(AIFunctionFactory.Create(
                async () =>
                {
                    if (string.IsNullOrEmpty(taskId)) return "Error: Task ID not set.";
                    if (string.IsNullOrEmpty(goalId)) return "Error: Goal ID not set.";
                    _log.Info($"Tool call: get_goal()");
                    var response = await bridge.GetGoalAsync(taskId, goalId, ct);
                    return response;
                },
                "get_goal",
                "Fetch the full goal description and acceptance criteria directly from the orchestrator."
            ));

            tools.Add(AIFunctionFactory.Create(
                async ([Description("Issue type: code_quality, bug, suggestion, concern, workflow")] string type,
                       [Description("Short title summarizing the issue")] string title,
                       [Description("Detailed description of the issue")] string description,
                       [Description("Severity: low, medium, high (default: low)")] string? severity = null) =>
                {
                    if (string.IsNullOrEmpty(taskId)) return "Error: Task ID not set.";
                    _log.Info($"Tool call: raise_issue({type}: {title})");
                    var response = await bridge.RaiseIssueAsync(taskId, type, title, description, severity ?? "low", ct);
                    return response;
                },
                "raise_issue",
                "Raise an issue for things you notice that are out of scope for the current goal: code quality problems, bugs in existing code, suggestions, concerns, or workflow issues."
            ));
        }

        if (_currentRole == WorkerRole.Tester)
            tools.Add(BuildTestResultsTool());

        if (_currentRole == WorkerRole.Reviewer)
        {
            tools.Add(BuildReviewVerdictTool());
            tools.Add(AIFunctionFactory.Create(
                () =>
                {
                    _log.Info("Tool call: get_test_report()");
                    return string.IsNullOrWhiteSpace(_testerReport)
                        ? "No test report available — the testing phase was not part of this iteration's plan, or no results were recorded."
                        : _testerReport;
                },
                "get_test_report",
                "Retrieve the tester's structured report for this iteration, including build success, test counts, and verdict. Call this to verify build/test acceptance criteria."));
        }

        if (_currentRole == WorkerRole.Coder)
            tools.Add(BuildCodeChangesTool());

        if (_currentRole == WorkerRole.DocWriter)
            tools.Add(BuildDocChangesTool());

        if (_currentRole == WorkerRole.Improver)
            tools.Add(BuildFileSizesTool());

        // EVERY role gets the completed-phase reports of this iteration: an earlier phase's report
        // can hold evidence a later phase needs to verify (the second Coding occurrence after a
        // review, the tester's build evidence, the doc-writer's report, ...). Read-only, and
        // deliberately ORTHOGONAL to get_test_report, which keeps its own reviewer-only contract.
        tools.Add(AIFunctionFactory.Create(
            ([Description("Phase name: Coding, Testing, Review, DocWriting or Improve (case-insensitive)")] string phase,
             [Description("1-based occurrence of the phase within this iteration, or omitted for the latest one")] int? occurrence = null) =>
            {
                _log.Info($"Tool call: get_phase_report({phase}" + (occurrence is null ? "" : $", occurrence={occurrence}") + ")");
                return ResolvePhaseReport(phaseReports, phase, occurrence);
            },
            "get_phase_report",
            "Retrieve the full report of a phase that already completed in the CURRENT iteration (e.g. the coder's or doc-writer's report) — use it to verify evidence the goal asks for in an earlier phase's report."));

        return tools;
    }

    /// <summary>
    /// Resolves the <c>get_phase_report</c> tool's answer: the requested report VERBATIM, or a
    /// message naming what is available instead.
    /// </summary>
    /// <remarks>
    /// THE REQUESTED NAME IS NEVER ECHOED on the failure path: the message renders the CANONICAL
    /// name when the argument is a known phase (matched case-insensitively) and the argument
    /// verbatim when it is not, so an unknown phase is reported honestly while a mere casing
    /// difference still resolves to the real report.
    /// <para>
    /// LATEST MEANS THE GREATEST <see cref="PhaseReport.Occurrence"/>, never the last ENUMERATED
    /// match. The reports arrive in metadata order — <c>PhaseReportMetadata.Read</c> preserves the
    /// dictionary's enumeration, and protobuf's <c>MapField</c> documents its iteration order as
    /// undefined — so list position carries no timeline guarantee at all and a
    /// <c>LastOrDefault</c> would answer with the wrong occurrence.
    /// </para>
    /// <para>
    /// When the runner holds no reports at all, the message says exactly that — naming an empty
    /// list would be noise. Otherwise every stored report is listed in the DETERMINISTIC order
    /// defined by <see cref="OrderedForListing"/> (canonical phase sequence, then occurrence
    /// ascending), so the same stored set always reads the same way regardless of enumeration
    /// order and the model can retry with a name it can actually use.
    /// </para>
    /// </remarks>
    /// <param name="reports">The reports this turn's tool set was built for.</param>
    /// <param name="phase">The phase name supplied by the model.</param>
    /// <param name="occurrence">The exact occurrence requested, or <c>null</c> for the latest one.</param>
    /// <returns>The stored report verbatim, or the explanatory message.</returns>
    private static string ResolvePhaseReport(
        IReadOnlyList<PhaseReport> reports, string phase, int? occurrence)
    {
        var canonicalPhase = PhaseReportPhaseNames.FirstOrDefault(
            name => string.Equals(name, phase, StringComparison.OrdinalIgnoreCase));

        if (canonicalPhase is not null)
        {
            var matching = reports
                .Where(r => string.Equals(r.Phase, canonicalPhase, StringComparison.OrdinalIgnoreCase));

            // Omitted occurrence → the GREATEST numeric occurrence, independent of enumeration
            // order. An explicit occurrence → exactly that one.
            var selected = occurrence is { } requested
                ? matching.FirstOrDefault(r => r.Occurrence == requested)
                : matching.MaxBy(r => r.Occurrence);

            if (selected is not null)
                return selected.Report;
        }

        if (reports.Count == 0)
            return NoPhaseReportsAvailableMessage;

        var requestedName = canonicalPhase ?? phase;
        var available = string.Join(", ",
            OrderedForListing(reports).Select(r => $"{CanonicalPhaseName(r.Phase)} (occurrence {r.Occurrence})"));
        return $"No {requestedName} report is available for this iteration. Available: {available}.";
    }

    /// <summary>
    /// Orders the reports for the availability listing WITHOUT touching the caller's list: canonical
    /// phase sequence first (Coding, Testing, Review, DocWriting, Improve), then occurrence ASCENDING
    /// within each phase.
    /// </summary>
    /// <remarks>
    /// The order is a property of THIS rendering, never of the stored data: the tool's answer must
    /// not depend on metadata enumeration order, which is undefined across the transport.
    /// <para>
    /// A stored phase name that is not one of the five (only reachable through malformed input, since
    /// a production report always carries a <c>GoalPhase</c> name) sorts AFTER every known phase; its
    /// own ordering is occurrence ascending, then the name ordinal, so the result is total and
    /// repeatable rather than whatever order a stable sort happened to inherit.
    /// </para>
    /// </remarks>
    /// <param name="reports">The reports to order; the input sequence is left unmodified.</param>
    /// <returns>A new, ordered sequence of the same reports.</returns>
    private static IEnumerable<PhaseReport> OrderedForListing(IEnumerable<PhaseReport> reports) =>
        reports.OrderBy(r => CanonicalPhaseIndex(r.Phase))
            .ThenBy(r => r.Occurrence)
            .ThenBy(r => r.Phase, StringComparer.Ordinal);

    /// <summary>
    /// The canonical position of a stored phase name, matched case-insensitively, or
    /// <see cref="PhaseReportPhaseNames"/>.Length when the name is not one of the five — an
    /// unknown name therefore sorts after every known phase.
    /// </summary>
    /// <param name="phase">A stored phase name.</param>
    /// <returns>The canonical index, or the count of known names for an unrecognized one.</returns>
    private static int CanonicalPhaseIndex(string phase)
    {
        var index = Array.FindIndex(
            PhaseReportPhaseNames, name => string.Equals(name, phase, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : PhaseReportPhaseNames.Length;
    }

    /// <summary>
    /// Renders a stored phase name in CANONICAL casing when it names a known phase, and verbatim
    /// otherwise — the same treatment <c>get_phase_report</c> gives the requested name, so neither
    /// side of the tool result depends on how an individual name happened to be spelled.
    /// </summary>
    /// <param name="phase">A stored phase name.</param>
    /// <returns>The canonical phase name, or <paramref name="phase"/> when it is not a known one.</returns>
    private static string CanonicalPhaseName(string phase) =>
        PhaseReportPhaseNames.FirstOrDefault(name => string.Equals(name, phase, StringComparison.OrdinalIgnoreCase))
        ?? phase;

    private AITool BuildTestResultsTool() => AIFunctionFactory.Create(
        ([Description("PASS or FAIL")] string verdict,
         [Description("Total number of tests")] int totalTests,
         [Description("Number of tests that passed")] int passedTests,
         [Description("Number of tests that failed")] int failedTests,
         [Description("Code coverage percentage (0-100), or -1 if not available")] double coveragePercent,
         [Description("Build succeeded (true/false)")] bool buildSuccess,
         [Description("List of issues found, empty if none")] string[] issues,
         [Description("Summary of test results, issues found, and any relevant context")] string summary) =>
        {
            var parsed = TaskVerdictExtensions.ParseTaskVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is TaskVerdict.Pass or TaskVerdict.Fail, "verdict must be exactly 'PASS' or 'FAIL'"),
                (totalTests >= 0, "totalTests must be >= 0"),
                (passedTests >= 0, "passedTests must be >= 0"),
                (failedTests >= 0, "failedTests must be >= 0"),
                (passedTests + failedTests <= totalTests,
                    $"passedTests ({passedTests}) + failedTests ({failedTests}) must not exceed totalTests ({totalTests})"),
                (coveragePercent is >= -1 and <= 100,
                    $"coveragePercent must be -1 (unavailable) or 0-100, got {coveragePercent}"));
            if (error != null) return error;

            _log.Info($"Tool call: report_test_results(verdict={verdict}, total={totalTests}, passed={passedTests}, failed={failedTests}, coverage={coveragePercent})");
            _lastTestReport = new TestResultReport
            {
                Verdict = parsed!.Value,
                TotalTests = totalTests,
                PassedTests = passedTests,
                FailedTests = failedTests,
                CoveragePercent = coveragePercent >= 0 ? coveragePercent : null,
                BuildSuccess = buildSuccess,
                Issues = issues.ToList(),
                Summary = summary,
            };
            return "Test results recorded.";
        },
        "report_test_results",
        "Report structured test results. REQUIRED for testers after running tests.");

    private AITool BuildReviewVerdictTool() => AIFunctionFactory.Create(
        ([Description("APPROVE or REQUEST_CHANGES")] string verdict,
         [Description("List of issues found, empty if none")] string[] issues,
         [Description("Overall review summary")] string summary) =>
        {
            var parsed = ReviewVerdictExtensions.ParseReviewVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is not null, "verdict must be exactly 'APPROVE' or 'REQUEST_CHANGES'"));
            if (error != null) return error;

            _log.Info($"Tool call: report_review_verdict(verdict={verdict}, issues={issues.Length})");
            _lastWorkerReport = new WorkerReport
            {
                ReviewVerdict = parsed!.Value,
                Issues = issues.ToList(),
                Summary = summary,
            };
            return "Review verdict recorded.";
        },
        "report_review_verdict",
        "Report your code review verdict. REQUIRED for reviewers after completing the review.");

    private AITool BuildCodeChangesTool() => AIFunctionFactory.Create(
        ([Description("PASS or FAIL")] string verdict,
         [Description("List of files modified")] string[] filesModified,
         [Description("Summary of changes made")] string summary) =>
        {
            var parsed = TaskVerdictExtensions.ParseTaskVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is TaskVerdict.Pass or TaskVerdict.Fail, "verdict must be exactly 'PASS' or 'FAIL'"));
            if (error != null) return error;

            _log.Info($"Tool call: report_code_changes(verdict={verdict}, files={filesModified.Length})");
            _lastWorkerReport = new WorkerReport
            {
                TaskVerdict = parsed!.Value,
                FilesChanged = filesModified.ToList(),
                Summary = summary,
            };
            return "Code changes recorded.";
        },
        "report_code_changes",
        "Report your code changes. REQUIRED for coders after implementing and committing.");

    private AITool BuildDocChangesTool() => AIFunctionFactory.Create(
        ([Description("PASS or FAIL")] string verdict,
         [Description("List of documentation files updated")] string[] filesUpdated,
         [Description("Summary of documentation changes")] string summary) =>
        {
            var parsed = TaskVerdictExtensions.ParseTaskVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is TaskVerdict.Pass or TaskVerdict.Fail, "verdict must be exactly 'PASS' or 'FAIL'"));
            if (error != null) return error;

            _log.Info($"Tool call: report_doc_changes(verdict={verdict}, files={filesUpdated.Length})");
            _lastWorkerReport = new WorkerReport
            {
                TaskVerdict = parsed!.Value,
                FilesChanged = filesUpdated.ToList(),
                Summary = summary,
            };
            return "Documentation changes recorded.";
        },
        "report_doc_changes",
        "Report your documentation changes. REQUIRED for doc-writers after updating docs.");

    private AITool BuildFileSizesTool() => AIFunctionFactory.Create(
        ([Description("Glob pattern to match files, e.g. '*.md' or '**/*.agents.md'. Leave empty for all files.")] string pattern) =>
        {
            _log.Info($"Tool call: get_file_sizes(pattern={pattern})");
            try
            {
                var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
                var searchOption = searchPattern.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var normalizedPattern = searchPattern.Replace("**/", "");

                var files = Directory.GetFiles(Path.Combine(_configRepoDir, "agents"), normalizedPattern, searchOption);
                if (files.Length == 0)
                    return "No files matched the pattern.";

                var lines = files.Select(f =>
                {
                    var info = new FileInfo(f);
                    var content = File.ReadAllText(f);
                    return $"{Path.GetFileName(f)}: {content.Length} chars, {info.Length} bytes";
                });
                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        },
        "get_file_sizes",
        $"Get character and byte counts for files in the agents directory. Use before editing to check against the {WorkerConstants.AgentsMdMaxCharacters}-character limit.");
}
