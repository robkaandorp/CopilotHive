# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `SharpCoderRunnerSummarizeMessageTests` — xUnit test suite with 10 [Fact] tests covering the `SummarizeMessage` helper method via reflection; tests verify tool call logging format, tool result format, argument truncation (100 chars), result preview truncation (200 chars), null handling, and plain text fallback behavior
- `SharpCoderRunner.SendPromptAsync` logging improvements — task execution now logs worker role and model: "Executing task as {role} with model {model}. WorkDir: {workDir}"; task completion logs elapsed time, status, and tool call count: "Task finished in {elapsed}s (status={status}, toolCalls={toolCalls})" using `System.Diagnostics.Stopwatch`
- `SharpCoderRunnerLoggingTests` — xUnit test suite verifying `SendPromptAsync` logging: role and model in task start message, elapsed time, status, and tool call count in task completion message


### Fixed
- Test method name typos in `SharpCoderRunnerSummarizeMessageTests` ('Tole' → 'Tool' in several test names)

### Changed
- Worker message logging in `SharpCoderRunner.cs` — the message loop now uses the new `SummarizeMessage` helper to produce informative summaries:
  - Messages containing `FunctionCallContent` log as `tool:{name}({key}="{value}")` with the first argument (truncated to 100 chars)
  - Messages containing `FunctionResultContent` log as `result:{callId} → "{preview}"` with the result preview (truncated to 200 chars)
  - Plain text messages retain existing behavior (truncated to 200 chars)
  - Previously, `[assistant]` and `[tool]` messages with empty `msg.Text` would log empty content, making debugging difficult


- `GET /health` endpoint now includes a `sharpCoderVersion` field reflecting the version of the SharpCoder NuGet package loaded at runtime (e.g., `"0.2.0.10"`)
- `HealthResponse.SharpCoderVersion` property with XML documentation
- `HealthEndpointTests` — 2 new xUnit tests (`GetHealth_HasSharpCoderVersionField`, `GetHealth_SharpCoderVersion_MatchesSemanticVersionFormat`) to verify the SharpCoder version field presence and semantic version format (14 total tests in the class)
- `WorkerLoggerTests` — xUnit test suite with 22 [Fact] tests covering `WorkerLogger.Info`, `Error`, `Debug`, and `LogBlock` output format; tests verify standard messages, empty messages, special character preservation, stdout/stderr routing, verbose mode behavior, log block truncation logic, and cross-method stream isolation
- `GrpcMapperTests` — comprehensive xUnit test suite with 27 tests (21 [Fact] + 6 [Theory]) covering:
  - WorkTask and TaskResult round-trip conversions (domain → gRPC → domain) preserving all fields
  - Null/empty handling for BranchSpec, TaskMetrics, GitChangeSummary, repositories, and metadata
  - BranchAction and TaskOutcome enum bidirectional mappings (all values)
  - WorkerRole enum mappings via `ToGrpcRole()` and `ToDomainRole()` for all roles (Coder, Tester, Reviewer, DocWriter, Improver, MergeWorker)
  - Edge cases: zero test counts, 0% and 100% coverage, empty task IDs/prompts
  - Unknown enum values throw `InvalidOperationException` (no silent fallbacks); `TaskStatus.Unspecified` and `TaskStatus.InProgress` throw appropriately
- `GET /health` endpoint now includes an `"uptime"` field formatted as `"HH:mm:ss"` (hours:minutes:seconds), allowing hours to exceed 24 for long-running servers; also includes raw `UptimeSpan` (TimeSpan) for programmatic parsing
- `HealthEndpointTests.GetHealth_UptimeField_MatchesHhMmSsFormat()` xUnit test verifies the uptime field is present and matches the expected format
- `GoalId.Validate(string)` static method in new `Configuration/GoalId.cs` for validating goal identifiers — IDs must be non-empty, lowercase kebab-case (letters, digits, hyphens only; no leading/trailing hyphens) to match git branch naming convention
- Goal ID validation in `FileGoalSource.MapToGoal()` — validates each goal ID after parsing from goals.yaml
- Goal ID validation in `ApiGoalSource.AddGoal()` — validates goal ID when adding a new goal via API
- `GoalIdTests` — 6 xUnit tests (5 valid cases: `fix-build-error`, `add-feature`, `abc`, `a1b2`, `a-1-b`; 8 invalid cases covering empty/null, uppercase, spaces, leading/trailing hyphens, underscores)

### Changed
- Orchestrator logging migrated from `Console.WriteLine` to `ILogger` with structured properties:
  - `DistributedBrain.cs` — Brain SDK events (AssistantMessage, Usage, SessionIdle, SessionError) now logged via `ILogger.LogDebug` with structured fields (`Source`, `Length`, `Model`, `InputTokens`, `OutputTokens`, `Cost`, `Duration`, `EventType`) instead of `Console.WriteLine` with formatted strings
  - `Program.cs` — Startup messages (gRPC server ports, Brain connection status, config loading) now logged after application build via `ILogger<Program>` with structured properties
  - `AgentsManager.cs` — AGENTS.md update and rollback operations now logged via injected `ILogger<AgentsManager>?` (optional, null-safe) with structured role names and version info
  - `MetricsTracker.cs` — Iteration metrics recording and test regression warnings now logged via injected `ILogger<MetricsTracker>?` with structured iteration, test, and coverage data
  - `DockerWorkerManager.cs` — Worker spawning, cleanup, and stoppage now logged via injected `ILogger<DockerWorkerManager>?` with structured container, port, role, and model information
- Optional logger injection added to `AgentsManager`, `MetricsTracker`, and `DockerWorkerManager` constructors — when omitted (defaults to null), log output is suppressed for backward compatibility
- Service registration in `Program.cs` updated to inject `ILogger` instances for `AgentsManager` and `MetricsTracker` via dependency injection
- Logging levels assigned consistently: `LogDebug` for SDK events and diagnostic detail, `LogInformation` for key operational events, `LogWarning` for error conditions and alerts

### Added (Previous)
- `GoalDispatcherBuildIterationSummaryTests` — xUnit test class with 2 tests verifying `BuildIterationSummary()` correctly handles `ImproverSkipped` flag: ensures exactly one "Improve" phase entry appears in output (no duplicates) regardless of whether PhaseDurations already recorded an "Improve" entry
- `IterationSummary` YAML-level test for null `TestCounts` omission — verifies that when `IterationSummary.TestCounts` is null, the serialised YAML output omits the `test_counts` key entirely
- `IterationSummary` YAML-level test for null `PhaseResult` — verifies that when a phase result entry in YAML has a null `result` field, `FileGoalSource.ReadGoalsAsync()` throws `InvalidOperationException` rather than silently defaulting
- `ConnectedWorkerTests.cs` — comprehensive xUnit test suite for `ConnectedWorker` class with 6 tests covering constructor initialization, state transitions (MarkBusy/MarkIdle), heartbeat updates, message channel I/O, and stale detection logic.
- `PhaseDurations` tracking in goals.yaml — GoalUpdateMetadata, Goal, and GoalFileEntry now include per-phase wall-clock durations (in seconds); FileGoalSource reads and writes phase_durations on goal completion; GoalDispatcher wires pipeline.Metrics.PhaseDurations to the completion metadata; 4 new xUnit tests verify reading/writing phase durations across multiple scenarios.
- `WorkerUtilizationService` — computes per-role worker utilization metrics from the current pool state; includes overall utilization fraction (0.0–1.0), per-role breakdown, and bottleneck role detection (utilization > 0.8).
- `WorkerUtilizationMetrics` DTO with `OverallUtilization` (double), `RoleBreakdown` (dictionary of role name to utilization fraction), and `BottleneckRoles` (list of role names exceeding 80% utilization).
- `GET /health/utilization` endpoint that returns current worker pool utilization metrics as JSON; registered in `Program.cs` and backed by `WorkerUtilizationService`.
- `GoalPhaseExtensions.ToDisplayName()` extension method that returns human-friendly display names for all `GoalPhase` enum values (e.g. `"Doc Writing"` for `DocWriting`, `"Improvement"` for `Improve`); uses explicit switch expression covering all 9 enum values and throws `InvalidOperationException` for unhandled values.
- `GoalPipeline.GetDisplayName(GoalPhase)` static helper that returns human-friendly display names for all pipeline phases (e.g. `"Doc Writing"` for `DocWriting`, `"Improvement"` for `Improve`).
- `WorkerRoleExtensions.ToDisplayName()` extension method that returns human-friendly display names for all `WorkerRole` enum values (e.g. `"Doc Writer"` for `DocWriter`, `"Merge Worker"` for `MergeWorker`); throws `InvalidOperationException` for unhandled values.
- `GoalExtensions.ToDisplayName()` extension methods for `GoalPriority` (returns "Critical", "High", "Normal", "Low") and `GoalStatus` (returns "Pending", "In Progress", "Completed", "Failed", "Cancelled"); both use explicit switch expressions and throw `InvalidOperationException` for unhandled values.
- `Goal.Notes` list field and `GoalUpdateMetadata.Notes` for recording non-fatal observations in goals.yaml (e.g. "Improver skipped: timeout")
- `IterationMetrics.ImproverSkipped` and `ImproverSkipReason` fields for tracking when the improve phase was skipped
- `IterationPlan.NextPhaseAfter(GoalPhase)` helper method for finding the next phase in a plan
- Brain retry mechanism — `AskAsync` retries up to 2 times on timeout, transient errors, and JSON parse failures (5-second backoff)
- Dirty-worktree safety net — `EnsureCleanWorktreeAsync` re-prompts Copilot up to 2 times if uncommitted changes remain after task execution
- `GitOperations.HasUncommittedChangesAsync()` for detecting dirty worktrees
- `IterationSummary` — structured summary of a completed pipeline iteration, containing iteration number, per-phase results (name, result, duration), test counts, review verdict, and notes; automatically appended to `Goal.IterationSummaries` after each iteration completes (success or failure)
- `PhaseResult` — per-phase execution result (name, result: "pass"/"fail"/"skip", duration_seconds)
- `TestCounts` — aggregate test counts (total, passed, failed) for a test run
- `Goal.IterationSummaries` — list of `IterationSummary` entries tracking metrics from each completed iteration
- `GoalUpdateMetadata.IterationSummary` — optional structured summary to append during goal completion/failure
- `FileGoalSource` now reads and writes `iteration_summaries` in goals.yaml with full round-trip fidelity
- `GoalDispatcher.BuildIterationSummary()` — constructs `IterationSummary` from pipeline metrics, marking the failed phase as "fail" and tracking skipped phases (e.g. improver timeout)
- `IterationSummaryTests` — 7 xUnit tests verifying `IterationSummary` structure, YAML serialisation, round-tripping via `FileGoalSource`, null handling, and multi-iteration appending

### Changed
- Extracted `OrchestratorVersion` string into `Constants.OrchestratorVersion` public const in `Constants.cs`; `HiveOrchestratorService` now references the constant instead of a hardcoded literal.
- Extracted `CleanupIntervalSeconds` and `StaleTimeoutMinutes` constants from `StaleWorkerCleanupService` into a new dedicated static class `CleanupDefaults` (in `CopilotHive.Services`) for improved discoverability and reuse.
- Improve phase is now non-blocking — failures (Brain timeout, dispatch errors) are logged as warnings and recorded in goal notes/metrics; the pipeline continues to Merging instead of failing the goal
- Git diff comparisons now use three-dot syntax (`origin/{baseBranch}...HEAD`) instead of `HEAD~1`, correctly detecting all changes on a feature branch
- DocWriter scope restricted — no longer edits source code (.cs files) or runs builds; reviews XML doc comments and flags issues in `DOC_REPORT.xml_doc_issues` but only modifies .md files
- Brain prompt instructions hardened — "NEVER include git checkout/branch/switch/push commands" and "NEVER include framework-specific build/test commands"
- Improver prompt no longer includes agents.md file contents (files are on disk); only lists file paths, saving thousands of tokens
- Commit instructions in agents.md simplified to single line `git add -A && git commit`
- Brain reviewer instructions include `origin/` prefix for diff commands (worker clones only have remote tracking refs)
- Coverage collection switched from `coverlet.msbuild` to `--collect:"XPlat Code Coverage"` collector approach (resolves package conflict)

### Fixed
- Duplicate "Improve" phase entries in `BuildIterationSummary()` — when `ImproverSkipped=true` and PhaseDurations already contained an "Improve" entry, the summary would produce two entries for the same phase; now explicitly removes any existing "Improve" entry before appending the skipped one
- Null `Result` field in `PhaseResultEntry` from YAML now throws `InvalidOperationException` instead of silently defaulting to `"pass"`, consistent with codebase convention of no silent fallbacks (treats null Result like null Id)
- Root cause of coder no-ops — Brain was generating `git checkout -b feature/...` commands in coder prompts, causing coders to commit on wrong branches; TaskExecutor then detected 0 changes on the infrastructure branch
- Removed conflicting `coverlet.msbuild` 6.0.2 package (conflicted with `coverlet.collector` 8.0.0)

## Removed
- WORKER_ROLE env var, workers are now always generic.
- Update xunit nuget package to xunit.v3.

## [0.8] — Doc Writer Role

### Added
- Doc-writer pipeline phase (between Testing and Review) for automatic documentation updates
- `docwriter.agents.md` — instructions for updating README, CHANGELOG, XML doc comments
- `GoalPhase.DocWriting` and `OrchestratorActionType.SpawnDocWriter`
- `WORKER_ROLE_DOCS_WRITER` in gRPC proto (value 5)
- Default model: claude-haiku-4.5 (fast/cheap); premium: claude-sonnet-4.6
- Brain prompts include docwriting in available phases and example plans
- Improver now reviews docwriter.agents.md alongside other agent files

### Changed
- Default pipeline order: Coding → Testing → DocWriting → Review → Improve → Merge
- Brain CraftPromptAsync includes docwriter guidance
- Agents.md size validation covers docwriter role

## [0.7] — Generic Worker Pool & Pipeline Reorder

### Added
- Generic worker pool — workers register without a fixed role and accept any role per task
- Workers dynamically receive role-specific agents.md before each task assignment
- `WORKER_REPLICAS` env var to configure number of generic workers (default: 4)
- `IsGeneric` flag on ConnectedWorker for pool identity tracking
- `TryDequeueAny()` on TaskQueue for role-agnostic task dispatch
- Premium model tier for coder and tester (claude-opus-4.6)
- Code coverage collection via Coverlet — CoveragePercent now populated with real values
- Coverage parsing in FallbackParseTestMetrics (Coverlet text table + key-value formats)
- model_tier propagation to all Brain methods (InterpretOutputAsync, PlanGoalAsync, DecideNextStepAsync)
- First-non-null-wins logic for model_tier via ApplyModelTierIfNotSet helper
- Streaming model output deltas to worker console (AssistantMessageDeltaEvent)

### Changed
- Pipeline phase order: Coding → Testing → Review (was Coding → Review → Testing)
  - Reviewer now gets test results and can review tester-written tests
- docker-compose: single `worker` service with replicas replaces 4 fixed-role services
- entrypoint.sh: all workers clone config repo at startup (any may act as improver)
- WORKER_ROLE env var is now optional (empty = generic pool worker)
- GoalDispatcher uses ApplyModelTierIfNotSet instead of unconditional tier overwrite

### Removed
- Legacy CLI-mode orchestrator code (2,922 lines)
- Unused metrics/ folder

## [0.6] — Server Architecture & DistributedBrain

### Added
- Server-only architecture — gRPC server + HTTP health endpoint (removed CLI mode entirely)
- DistributedBrain — LLM-powered orchestrator Brain using GitHub Copilot SDK JSON-RPC
- GoalDispatcher — pipeline state machine with phase sequencing (Coding → Review → Testing → Merge → Improve)
- SQLite persistence (PipelineStore) with automatic schema migration
- Config repo integration — external CopilotHive-Config repo for agents.md and goals.yaml
- AgentsManager — agents.md versioning, rollback on regression
- ConfigRepoManager — config repo sync and checkout
- Premium model tier selection — Brain can escalate to premium models for complex tasks
- Duplicate goal completion guards — prevent late task callbacks from re-triggering completion
- Orchestrator agent pre-selection via RPC (ensures correct custom agent is active)
- Native SDK telemetry with TelemetryConfig on CopilotClientOptions
- Telemetry aggregation — summarized metrics injected into improver context
- Agents.md size enforcement — 4000-character limit with improver retry loop
- Auto-rebase with pre-improver SHA tracking
- Less prescriptive goal philosophy — goals describe WHAT not HOW
- Comprehensive test suite: 333 xUnit tests

### Changed
- Orchestrator now runs exclusively in server mode (removed --serve flag)
- Brain prompts instruct workers to discover HOW, not prescribe exact file/method changes
- Improver receives richer context: iteration outcomes, retry counts, specific issues

### Removed
- Legacy CLI-mode orchestrator (OrchestratorBrain, Orchestrator.cs)
- Legacy Copilot client abstractions (CopilotWorkerClient, ICopilotWorkerClient, CopilotClientFactory)
- 53 legacy tests removed (333 remain)

## [0.5] — Improver Role & Observability

### Added
- Improver worker role for iterative code refinement after review/test feedback
- Per-role model selection — assign different LLM models to each worker type via goals.yaml
- Auto-rebase on merge conflicts — pipeline automatically rebases feature branches and retries
- File-based telemetry — run metrics and outcomes persisted to the metrics/ directory
- Fallback metrics parsing — robust parsing handles varied and partial worker output formats

## [0.4] — Brain & Multi-Repo Support

### Added
- Brain module: LLM-powered orchestrator intelligence for interpreting worker output
- Multi-repo goal support — goals can target any accessible Git repository, not just the host repo

### Changed
- Orchestrator now uses LLM reasoning for all tactical decisions instead of hardcoded rule logic

## [0.3] — Reviewer Role & Self-Improvement

### Added
- Reviewer worker role — produces structured REVIEW_REPORT with approve/request-changes verdict
- Metrics-driven self-improvement loop — system tunes behavior based on historical run data
- AGENTS.md evolution — system updates its own agent definitions based on accumulated learnings

## [0.2] — Bootstrap & Containerization

### Added
- Bootstrap capability — CopilotHive can develop and improve itself using its own pipeline
- Docker containerization — all worker agents run in isolated Docker containers

### Changed
- Workers migrated from in-process execution to container-based isolation

## [0.1] — Initial Release

### Added
- Orchestrator core — goal intake, phase sequencing, branch management
- Coder worker role — implements goals on feature branches
- Tester worker role — builds project and runs test suite
- Basic goal pipeline: Coding → Testing loop
