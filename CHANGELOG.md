# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] - 2025-07-15

### Added

**Composer Agent** — Conversational goal decomposition with Chat UI, persistent sessions, streaming responses, and 7 LLM-callable tools: `create_goal`, `approve_goal`, `update_goal`, `delete_goal`, `get_goal`, `list_goals`, `search_goals`. Includes codebase tools (`read_file`, `glob`, `grep`) for informed goal creation.

**Composer Chat UI** — Real-time streaming chat interface at `/composer` with Enter-to-send, session reset, cancel support, full Markdown rendering (Markdig), auto-scroll, and session restoration on navigation.

**Composer ask_user** — Interactive question tool (YesNo, SingleChoice, MultiChoice) with markdown rendering and optional feedback text area.

**Composer web_search** — Web research via Ollama API with result truncation to prevent context overflow.

**Composer web_fetch** — Web page fetching with content truncation (default 100 lines) to manage token usage.

**Composer git tools** — Read-only git inspection (`git_log`, `git_diff`, `git_show`, `git_branch`, `git_blame`) against Brain's repo clones.

**Composer list_repositories** — Query live hive-config.yaml for configured repositories with names, URLs, and default branches.

**Composer get_phase_output** — Retrieve worker output from specific pipeline phases with optional content parameter.

**Composer phase detail** — View per-phase detail via the phase detail tool with enriched iteration display.

**Composer update_goal** — Update goal properties including release assignment with transition validation.

**Composer context overflow recovery** — Automatic session reset when context limit exceeded with user notification.

**Brain CodingAgent Migration** — Brain uses SharpCoder's `CodingAgent` with persistent session across all goals, automatic context compaction at 80% threshold, session persistence to `brain-session.json`, and built-in file tools.

**Brain Read-Only File Access** — Brain reads project structure, config, and code via `read_file`, `glob`, `grep` on persistent repo clones at `{stateDir}/repos/{repoName}`.

**Brain Context Management** — Context overflow auto-recovery detects token limit errors and resets session automatically. Context compaction logged with token/message counts and percentage reduction.

**Brain Session Reset** — Dashboard button to reset Brain session, clearing conversation history and reloading agents.md from disk.

**Brain Session Persistence** — Session saved after each Brain call, loaded on startup for crash recovery.

**SQLite Goal Store** — `SqliteGoalStore` as primary goal persistence with CRUD, text search, status filtering, iteration tracking, automatic schema migration. Replaces `FileGoalSource` in the pipeline.

**Goals REST API** — Full CRUD endpoints: `GET /api/goals`, `POST /api/goals`, `GET /api/goals/{id}`, `PATCH /api/goals/{id}/status`, `DELETE /api/goals/{id}`, `GET /api/goals/search` with SQLite backing.

**Goals Browser Dashboard** — `/goals` page with search bar, status/priority/repository filters, sortable table, and inline actions.

**Goal Detail Page** — Inline Brain/Worker prompt display, Planning phase with reasoning visibility, dependency visualization with status indicators, worker output Markdown rendering, merge commit hash links, and "← Goals" back navigation.

**Goal Approval** — Approve goals from dashboard (Draft → Pending) via API and Composer.

**Goal Deletion** — Delete Draft/Failed goals with remote branch cleanup from all associated repositories.

**Goal Cancellation** — Cancel InProgress/Pending goals via API, dashboard, and Composer with clear status transitions.

**Revert to Draft** — Restore Pending goals to Draft state from dashboard and Composer.

**Goal Dependencies** — Goals declare `DependsOn` list of goal IDs that must complete before dispatch; persisted in SQLite and YAML; cross-source dependency resolution; visualization with status indicators.

**Goal Scope** — `GoalScope` enum (Patch/Feature/Breaking) persisted in SQLite and YAML, exposed in Composer tools and dashboard, enforced by reviewer.

**Release Management** — Release entity with CRUD API, SQLite persistence, goal-to-release assignment, dashboard UI (Releases page, detail view, assignment on Goals).

**Configuration Page** — YAML syntax highlighting, Markdown rendering for agent files, removed `goals.yaml` tab (goals managed via Goals page).

**Worker Session Persistence** — Per-role agent sessions via gRPC `GetSession`/`SaveSession`, resuming conversations across iterations.

**Worker Output Persistence** — `PhaseResult.WorkerOutput` stores raw output inline; `IterationSummary.PhaseOutputs` dictionary for completed iterations; persisted to SQLite and YAML.

**Brain Repo Manager** — Persistent repo clones for Brain file access and merge operations; squash merge with Brain-generated commit messages; merge commit hash capture and display.

**Eager Repository Cloning** — Brain clones all repos at startup instead of lazily for immediate file access.

**Conversation Entry Metadata** — `ConversationEntry` tracks iteration context (`Iteration`, `Purpose`) for analyzing history by iteration without heuristic parsing.

**GitHub Actions CI Workflow** — Automated build, test, and container image push to GHCR on `develop` branch pushes. Multi-architecture Docker support (amd64/arm64) with cross-compilation to avoid QEMU segfaults.

**Structured Logging Migration** — Orchestrator logging migrated from `Console.WriteLine` to `ILogger` with structured properties across all components.

**Observability** — Push error visibility in worker output, HTTP resilience with 3 retries and exponential backoff, worker utilization metrics with `/health/utilization` endpoint, Brain context usage logging after each call, model name in completion logs, goal elapsed time logging, phase duration logging on completion.

**Version Display** — CopilotHive version shown in navigation from assembly metadata; `VersionPrefix` in `Directory.Build.props` for CI-driven versioning.

**Conditional DocWriting** — Brain conditionally includes/skips DocWriting phase based on goal description.

**Worker Pool** — Generic worker pool with `WORKER_REPLICAS` configuration; `IsGeneric` flag for pool identity tracking; `TryDequeueAny()` for role-agnostic dispatch.

**Testing Infrastructure** — 636+ xUnit tests including integration tests, gRPC mapper tests, worker logging tests, Composer tool tests, SQLite store tests, and pipeline lifecycle tests.

**Squash Merge** — Feature branches squash-merged with Brain-generated commit messages; handles merge conflicts via abort and retry.

**Merge Commit Hash Capture** — Merge commit SHA captured and stored on goal entity, displayed as clickable GitHub link in Goal Detail.

**Iteration Summary** — Structured summary of completed iterations with per-phase results, test counts, review verdict, and notes.

**DurationFormatter** — Utility class for human-readable duration strings (e.g., "4m 32s", "1h 12m").

**Enum Display Names** — Display name extensions for `GoalPhase`, `WorkerRole`, `GoalPriority`, `GoalStatus`.

**Remote Branch Cleanup** — Failed goal deletion automatically cleans up remote feature branches from all associated repositories.

**Repository Filter** — Repository filter dropdown on Goals page with distinct repository names.

**Inline Prompts** — Collapsible Brain and Worker prompt display in Goal Detail with Markdown rendering.

**Planning Phase** — Visible Planning phase in iteration timeline with reasoning display.

**Goal ID Validation** — Lowercase kebab-case enforcement for goal identifiers.

**Premium Model Tier** — Tier selection for premium model usage with per-worker assignment.

**Auto-Rebase** — Automatic rebase on merge conflicts with pre-improver SHA tracking.

**Agents.md Versioning** — Versioning and rollback on metric regression.

**Config Repo Integration** — Hive config loaded from dedicated config repository.

**SharpCoder Streaming** — `IAsyncEnumerable<StreamingUpdate>` for real-time token streaming.

**Worker Utilization Metrics** — Per-worker utilization tracking with bottleneck role detection.

**Goal Status Validation** — PATCH endpoint validates allowed status transitions (Draft↔Pending only).

**Composer Tool Call Fix** — `FixToolCallArguments` sanitizes outgoing requests for Claude models.

**Health Endpoint** — Backed by `SqliteGoalStore` with SharpCoder version and uptime fields.

**Phase Durations** — Per-phase wall-clock duration tracking in goals.yaml.

**Improver Non-Blocking** — Improver failures logged as warnings, pipeline continues to Merging.

**Test Regression Detection** — Automatic test regression warnings in metrics tracking.

**Coverage Collection** — Switched to `--collect:"XPlat Code Coverage"` collector approach.

**Global Scrollbar** — Thin scrollbar styling applied globally to all scrollable elements.

**Empty Choices Handling** — `CopilotChoiceMergingHandler` synthesizes stop choice when Copilot returns empty choices array.

**Dirty Worktree Safety** — `EnsureCleanWorktreeAsync` re-prompts Copilot if uncommitted changes remain.

**Telemetry Aggregation** — Aggregated metrics across goals and iterations for improver context.

**Agents.md Size Enforcement** — 4000-character limit with improver retry loop.

**Duplicate Goal Completion Guards** — Prevent late task callbacks from re-triggering completion.

**Brain Retry Mechanism** — `AskAsync` retries up to 2 times on timeout, transient errors, and JSON parse failures.

**Less Prescriptive Goals** — Goals describe WHAT not HOW; workers discover implementation approach.

**Per-Role Model Selection** — Different LLM models assigned to each worker type via configuration.

**Streaming Model Output** — Deltas streamed to worker console via `AssistantMessageDeltaEvent`.

**Worker Message Logging** — Informative summaries for tool calls and results with truncation.

**Goal Notes** — Non-fatal observations recorded in goals.yaml (e.g., "Improver skipped: timeout").

**Config Repo Sync** — Automatic config repository sync and checkout at startup.

**Multi-Repo Goal Support** — Goals can target any accessible Git repository.

**Metrics-Driven Self-Improvement** — System tunes behavior based on historical run data.

**AGENTS.md Evolution** — System updates its own agent definitions based on accumulated learnings.

**Bootstrap Capability** — CopilotHive can develop and improve itself using its own pipeline.

**Docker Containerization** — All worker agents run in isolated Docker containers.

**Orchestrator Core** — Goal intake, phase sequencing, branch management.

**Coder Worker Role** — Implements goals on feature branches with feature branch pattern.

**Tester Worker Role** — Builds project and runs test suite with coverage collection.

**Reviewer Worker Role** — Produces structured REVIEW_REPORT with approve/request-changes verdict.

**DocWriter Worker Role** — Updates README, CHANGELOG, and documentation files.

**Improver Worker Role** — Iterative code refinement after review/test feedback.

**Merge Worker Role** — Handles feature branch merging and cleanup.

### Changed

**SharpCoder** — Updated from 0.4.4 to 0.5.0

**Pipeline Phase Order** — Coding → Testing → Review → DocWriting → Improve → Merge

**Docker Compose** — Single `worker` service with replicas replaces fixed-role services

**Configuration** — `ComposerConfig` moved to top-level in `HiveConfigFile`

**Goal Manager** — Uses `SqliteGoalStore` as sole goal source

**Health Endpoint** — Backed by `SqliteGoalStore`

**Brain Prompts** — Instructed to discover HOW not WHAT; no git checkout/branch/switch/push commands; no framework-specific build/test commands

**Reviewer Scope** — DocWriter scope restricted to .md files only

**Improver** — Non-blocking; failures logged as warnings and pipeline continues to Merging

**Coverage Collection** — Switched from `coverlet.msbuild` to `--collect:"XPlat Code Coverage"`

**Smoke Test** — Preserves persistent volumes, uses Composer for goal setting

**Global Scrollbar** — Thin scrollbar styling applied globally

**DocWriter Scope** — No longer edits source code (.cs files) or runs builds; reviews XML doc comments and flags issues

**Git Diff Syntax** — Three-dot syntax (`origin/{baseBranch}...HEAD`) instead of `HEAD~1`

**Commit Instructions** — Simplified to single line `git add -A && git commit`

**Brain Reviewer Instructions** — Include `origin/` prefix for diff commands

**Improver Prompt** — No longer includes agents.md file contents (files on disk)

**Orchestrator Logging** — Migrated from `Console.WriteLine` to `ILogger` with structured properties

**Brain System Prompt** — Allows file reading (was "do NOT run tools or file operations")

**Brain WorkDirectory** — Set to `repos/` parent folder so all repositories visible simultaneously

**GoalDispatcher** — Checks `GetActivePipelines()` before dispatching; includes priority level in dispatch log

**Extracted Constants** — `OrchestratorVersion` into `Constants.cs`; `CleanupIntervalSeconds` and `StaleTimeoutMinutes` into `CleanupDefaults`

**Worker Message Logging** — Uses `SummarizeMessage` helper for informative tool call/result summaries

**Server-Only Architecture** — Runs exclusively in server mode (removed --serve flag)

**Orchestrator Agent Pre-Selection** — Via RPC to ensure correct custom agent is active

**Native SDK Telemetry** — With `TelemetryConfig` on `CopilotClientOptions`

**Generic Worker Pool** — Workers register without fixed role and accept any role per task

**WORKER_REPLICAS Env Var** — Configure number of generic workers (default: 4)

**Multi-Architecture Docker** — Cross-compilation instead of QEMU emulation

**Eager Push CurrentModel** — Dashboard shows actual model including premium tier

### Fixed

**Empty Repository Clone** — Startup no longer fails when cloning an empty repository

**Reviewer Scope Enforcement** — Reviewer scope now correctly matches `GoalScope`

**Eager Push CurrentModel Display** — `CurrentModel` shown correctly during premium tier pushes

**Release Form Repository Dropdown** — Dropdown now shows all configured repositories

**Diagnostics File Path** — Default changed from `/app/diagnostics` to system temp directory

**Runner Reset Test** — CI test uses factory delegate instead of `GH_TOKEN`

**Session Save Cancellation** — `SaveSessionAsync` no longer re-throws `OperationCanceledException`

**Worker Container Restart** — `entrypoint.sh` uses clone-or-pull pattern to survive restarts

**web_search Overflow** — Per-result truncation to 500 chars prevents context window overflow

**web_fetch Line Limit** — Default `max_lines` reduced from 200 to 100

**Push Error Visibility** — Git push failures now reported to Brain in worker output

**QEMU Segfault** — arm64 Docker images built via cross-compilation instead of QEMU emulation

**SQLite Readonly in CI** — Fixed "readonly database" error caused by test collection sharing

**BrainRepoManager CI Failures** — Tests use isolated temporary directories

**GoalDetail Delete Handling** — Delete errors shown as inline messages instead of exceptions

**Worker Model Display** — Shows actual model name including premium tier suffix

**Dependency Link Readability** — Dependency links use accent color for contrast

**Duplicate Improve Phase** — Duplicate "Improve" entries in iteration summary eliminated

**Null PhaseResult Field** — Null `Result` field in `PhaseResultEntry` handled gracefully

**Coder No-Op Commands** — Coder no longer issues `git checkout -b` commands from prompts

**BOM in Worker Project** — BOM removed from `CopilotHive.Worker.csproj`

**Goal Delete Validation** — `DELETE /api/goals/{id}` enforces status restrictions correctly

**Tool Call Arguments** — Replaces `"null"` or `""` tool_call arguments with `"{}"` for Claude models

**Root Cause of Coder No-Ops** — Brain was generating `git checkout -b` commands causing wrong branch commits

**Conflicting Coverlet** — Removed `coverlet.msbuild` 6.0.2 package conflicting with `coverlet.collector`

**PhaseResultEntry Null** — Throws `InvalidOperationException` instead of silently defaulting to "pass"

**IterationSummary TestCounts** — Null `TestCounts` omits `test_counts` key in YAML output

**SQLite Schema Migration** — Automatic migration for new columns

**Goal Status Transitions** — PATCH validates allowed transitions (Draft↔Pending only)

**Composer Transition Validation** — Validates status transitions in `update_goal`

**Repository Tags** — Repository names displayed next to goal title instead of buried in card

**Composer Enriched Iteration Display** — Per-phase breakdown with durations and test counts

**Worker Pool Utilization** — Per-role breakdown and bottleneck detection

**Iteration Duplicate Guard** — Prevents duplicate "Improve" phase entries

**Goal Completion Guards** — Prevent late task callbacks from re-triggering completion

**Config Repo Restart** — Latest changes after restart, not stale from previous run

**Health Endpoint Uptime** — Formatted as "HH:mm:ss" with hours exceeding 24

**Goal ID Validation** — Validates lowercase kebab-case format

**Test Regression Warnings** — Logged via `ILogger` with structured data

**Agent Pre-Selection** — Ensures correct custom agent is active via RPC

**Auto-Rebase** — With pre-improver SHA tracking

**Agents.md Size Limit** — 4000-character enforcement

**Metrics Parsing** — Robust parsing for varied and partial worker output formats

**File-Based Telemetry** — Run metrics and outcomes persisted

**Duplicate Goal Completion** — Guards prevent re-triggering completion

**Premium Model Tier** — First-non-null-wins logic via `ApplyModelTierIfNotSet`

**Brain Prompt Instructions** — Hardened to exclude git commands and framework-specific commands

**Test Method Name Typos** — Fixed in `SharpCoderRunnerSummarizeMessageTests`

**Stale Worker Cleanup** — Constants extracted to `CleanupDefaults`

**Goal Elapsed Time** — Formatted with `DurationFormatter`

**Phase Duration Logging** — Wall-clock duration logged on phase completion

**Model Name in Logs** — Included in task and phase completion logs

**Brain Context Logging** — Usage percentage logged after each call

**Goal Source Count** — Logged at startup

**Print Banner** — Includes UTC start time

**Worker Spawn Logging** — Structured properties for container, port, role, model

**Agent Update Logging** — Structured properties for role names and version info

**Metrics Recording** — Structured properties for iteration, test, coverage data

**SDK Events** — Logged via `ILogger.LogDebug` with structured fields

**Startup Messages** — Logged via `ILogger<Program>` with structured properties

**Optional Logger Injection** — Added to `AgentsManager`, `MetricsTracker`, `DockerWorkerManager`

**Test Isolation** — Unique goal IDs via `UniqueId()` helper

**YamlDotNet Version** — Updated for compatibility

**Copilot SDK Events** — AssistantMessage, Usage, SessionIdle, SessionError structured logging

**HTTP Client Timeout** — 15-second timeout for web tools

**Authorization Header** — Bearer token for Ollama API

**Graceful Web Errors** — Error handling for HTTP errors, network failures, timeouts

**Config Repo Clone-or-Pull** — Handles first start, restart, and corrupt clone scenarios

**Repository Names** — Case-insensitive deduplication in filters

**Filter Count** — Updates correctly when repo filter applied

**Question Widget Styling** — Card-like appearance with accent border

**Answer Formatting** — YesNo and MultiChoice with optional feedback

**Question State** — Clears after submission or cancellation

**Composer Tool Registration** — `ask_user`, `list_repositories`, `get_phase_output`

**System Prompt Updates** — Mentions new capabilities dynamically

**Composer Session** — Persisted to disk, loaded on navigation

**Chat History** — Restored from persistent session

**Auto-Scroll** — To bottom of chat messages

**Markdown Rendering** — Using Markdig with advanced extensions

**Brain Session Stats** — Context usage, message count, cumulative tokens

**Dashboard Stats** — Composer section with progress bar

**Model Configuration Table** — Shows Composer model

**Streaming Updates** — `TextDelta` and `Completed` variants

**Response Reconstruction** — From accumulated updates

**Diagnostics Extraction** — For reuse

**Test Coverage** — Coverlet integration

**Coverage Percent** — Real values from Coverlet

**Coverage Parsing** — Text table and key-value formats

**Model Tier Propagation** — To all Brain methods

**Goal Priority Logging** — In dispatch log message

**Phase Duration Calculation** — From `PhaseStartedAt` timestamp

**Duration Formatting** — 1 decimal place for seconds

**Review Verdict** — Shown in iteration header

**Testing Phase Display** — Inline test counts when available

**No-Review Case** — Handled gracefully

**Prompt Extraction** — From tagged `ConversationEntry` metadata

**Planning Prompts** — `PlanningBrainPrompt` and `PlanningBrainResponse`

**Craft Prompts** — `BrainPrompt` and `WorkerPrompt`

**Collapsible Sections** — Above Worker Output

**Muted Styling** — For Brain Prompt

**Role Name Display** — With Worker Prompt

**Flat Conversation Removal** — Data shown contextually within phases

**Completed Iterations** — Graceful null prompt handling

**Iteration Summary Display** — For completed iterations

**Phase Results** — From `IterationSummary`

**Test Counts** — From stored data

**Failure Reasons** — Preserved in iteration history

**Phase Durations** — Wall-clock time tracking

**Null Safety** — For completed iterations without live pipeline

**Dependency Status Indicators** — ✅ Completed, ⏳ Pending, 🔄 InProgress, ❌ Failed, ❓ Unknown

**Dependency Links** — Clickable with goal ID

**No Dependencies** — Section hidden when empty

**Cross-Source Resolution** — Dependencies from all registered sources

**Blocked Goal Logging** — At Debug level with unsatisfied dependency IDs

**Multi-Dependency Scenarios** — All must complete before unblock

**Priority Ordering** — Among eligible goals

**JSON Column** — `depends_on` in SQLite

**Schema Migration** — Automatic for dependency support

**Round-Trip Persistence** — YAML and SQLite

**Parameter Parsing** — In `create_goal` tool

**Release CRUD** — Create, read, update, delete releases

**Goal-to-Release Assignment** — In `update_goal` and dashboard

**Release Dashboard** — Releases page with list view

**Release Detail View** — With assigned goals

**Assignment on Goals** — Dropdown in goal detail

**Scope Enforcement** — Reviewer matches `GoalScope`

**Conditional Inclusion** — Based on goal description keywords

**Startup Cloning** — All repos cloned immediately

**Config Time Loading** — `hive-config.yaml` read at call time

**Session Resume** — Per-role via gRPC

**Brain-Generated Messages** — Concise commit summaries (~72 char subject)

**Fallback Message** — `BuildSquashCommitMessage` on Brain failure

**Commit Format** — `Goal: {goalId} — {brainSummary}`

**Subject Truncation** — To 120 characters when needed

**Full Description** — In commit body

**Empty Squash Handling** — Logs and returns current HEAD

**Merge Conflict Handling** — Via `git merge --abort` with `--squash`

**Hash Persistence** — In `Goal.MergeCommitHash`

**Hash Propagation** — Via `GoalUpdateMetadata` and `PipelineSnapshot`

**Hash Display** — As clickable GitHub link with 🔗 prefix

**Short Hash** — 7 characters in UI

**Full Hash** — In link URL

**URL Resolution** — From repository configuration

**.git Suffix** — Automatically stripped

**Round-Trip Tests** — For hash persistence

**WorkerOutput Property** — In `PhaseResult`

**PhaseOutputs Dictionary** — In `IterationSummary`

**BuildIterationSummary** — Populates from live pipeline

**FileGoalSource Serialization** — Worker outputs to YAML

**SQLite Persistence** — Via `phase_outputs_json` column

**Dashboard Population** — From pipeline or stored goal

**Prefer Persisted** — Over live pipeline data

**15 Round-Trip Tests** — For `PhaseResult.WorkerOutput` and `IterationSummary.PhaseOutputs`

**CancelGoalAsync Method** — In `GoalDispatcher`

**API Endpoint** — `POST /api/goals/{id}/cancel`

**Dashboard Button** — ⏹ Cancel Goal with confirmation

**Composer Tool** — `cancel_goal` function

**Status Validation** — Before calling dispatcher

**Descriptive Errors** — For non-cancellable goals

**GoalManager.GetGoalAsync** — New method for retrieval

**11 Cancel Tests** — Covering various scenarios

**CI Workflow Trigger** — On `develop` branch pushes

**Test Job** — Restore, build, run xUnit suite

**Build-and-Push Job** — After successful test completion

**GHCR Images** — Orchestrator and worker

**Tags** — `latest` and commit SHA

**Docker Layer Caching** — Via GitHub Actions cache

**Authentication** — `secrets.GITHUB_TOKEN` with `packages: write`

**Build Context** — Repository root

**Cross-Compilation** — `--platform=$BUILDPLATFORM` on build stage

**Runtime Stage** — Target platform's base image

**Avoids QEMU** — JIT segfaults on arm64

**Retries** — 3 with exponential backoff (5s base)

**Per-Attempt Timeout** — 2 minutes

**Transient Error Handling** — For LLM API calls

**Named HTTP Client** — `"ollama-web"` with configuration

**Ollama API Key** — Required for web tools

**Conditional Registration** — Tools appear only when API key present

**Dynamic System Prompt** — Mentions web capabilities when configured

**Clear Error Messages** — When web search unavailable

**Result Formatting** — Titles, URLs, content snippets

**Fetch Truncation** — To requested max_lines

**HTTP Error Handling** — For search and fetch

**Network Failure Handling** — With user-friendly messages

**Timeout Handling** — 15-second timeout

**Authorization Verification** — Bearer token in header

**Invalid JSON Handling** — Graceful degradation

**24 Web Tool Tests** — Covering all scenarios

**20 Git Tool Tests** — For `ComposerToolTests`

**Unknown Repo Error** — Lists available repositories

**Parameter Passing** — Via `ArgumentList.Add()` to prevent injection

**Output Truncation** — To 500 lines with notice

**Case-Insensitive Matching** — For phase names

**Role Key Mapping** — e.g., `"Coding"` → `"coder-{iteration}"`

**Clear Error Messages** — Goal not found, iteration not found, phase not found, no output recorded

**20 Phase Output Tests** — Covering happy path and error cases

**Planning Phase Display** — As first phase box

**Active Planning** — ⏳ indicator with active status

**Completed Planning** — ✓ icon after completion

**Completed Iterations** — Show Planning phase at start

**Plan Reasoning Display** — 💭 prefix with italic text

**Iteration Strategy** — Visible to users

**Worker Output Markdown** — Using `RenderMarkdown()`

**Dark-Theme Styling** — For code blocks, tables, lists, headings

**Raw Text Replacement** — Previously unformatted `<pre>`

**goal-link CSS Class** — For dependency links

**Accent Color** — `#58a6ff` for visibility on dark background

**Previous Unreadability** — Browser-default dark blue/purple

**Reset Button Styling** — `chat-action-btn chat-action-btn--danger`

**Icon Prefix** — 🔄 for visual clarity

**Spinner During Reset** — ⏳ for progress indication

**CSS Modifier** — `.chat-action-btn--danger` for red hover state

**Thread Safety** — Via `_brainCallGate` semaphore

**Automatic Stats Update** — After reset

**Session File Deletion** — `brain-session.json`

**Fresh CodingAgent** — Rebuilt with new system prompt

**Agents.md Reload** — From disk immediately

**2 Reset Tests** — For `ResetSessionAsync`

**Per-Phase Detail** — In enriched iteration display

**Old Format** — Phase count only

**New Format** — Per-phase breakdown with durations

**3 Detail Tests** — For per-phase display

**Branch Naming Convention** — `copilothive/{goalId}`

**Best-Effort Cleanup** — Goal deletion succeeds even if branch deletion fails

**5 DeleteRemoteBranch Tests** — For branch cleanup

**3 Composer Delete Tests** — For Failed goal handling

**3 API Delete Tests** — For endpoint validation

**Goal Id Validation** — In `FileGoalSource.MapToGoal()`

**Goal Id Validation** — In `ApiGoalSource.AddGoal()`

**6 GoalId Tests** — Valid and invalid cases

**Priority Logging** — In dispatch message

**Phase Duration Logging** — In seconds when completes

**DurationFormatter Tests** — 9 tests for human-readable formatting

**Context Usage Logging** — After each Brain call

**4 Context Tests** — For percentage calculation

**WorkerPool.ConnectedWorkerCount** — New property

**IWorkerPool Interface** — Updated

**2 Count Tests** — For worker pool

**PrintBanner Time** — UTC start time below ASCII art

**Source Count Log** — At startup

**Compaction Logging** — Before and after with counts

**Auto-Compaction** — At 80% threshold

**Usage Percentage** — Computed from `InputTokensUsed`

**Zero-Context Handling** — In percentage calculation

**Caller Name Inclusion** — In log message

**Exact Message Format** — Verified in tests

**Token Reduction** — Logged with percentage

**Message Count Reduction** — Logged

**Session Persistence** — To `brain-session.json`

**Session Loading** — On startup

**Crash Recovery** — For Brain session

**Repos Parent Folder** — As `WorkDirectory`

**All Repositories Visible** — Simultaneously

**File Tools** — Built-in via CodingAgent

**Sequential Processing** — One goal at a time

**Learnings Accumulation** — Across goals

**Per-Goal Session** — Removed

**Single Persistent Session** — Replaces dictionary

**CleanupGoalSessionAsync** — Removed from interface

**ReprimeSessionAsync** — Removed from interface

**GetOrCreateSession** — Removed

**_sessions Dictionary** — Removed

**SendToBrainCoreAsync** — Replaced by `ExecuteBrainAsync`

**FunctionInvokingChatClient** — Removed from Brain

**Tool Invocation** — Handled internally by CodingAgent

**Auto-Rebase** — Simplified (removed complexity)

**Brain Prompt Update** — Allows file reading

**Dispatch Check** — For active pipelines

**Priority Inclusion** — In dispatch log

**SummarizeMessage Helper** — For informative logging

**Tool Call Logging** — `tool:{name}({key}="{value}")`

**Tool Result Logging** — `result:{callId} → "{preview}"`

**Argument Truncation** — To 100 chars

**Result Preview Truncation** — To 200 chars

**Plain Text Fallback** — For non-tool messages

**Previous Empty Logging** — For `[assistant]` and `[tool]` fixed

**10 SummarizeMessage Tests** — Via reflection

**Task Execution Logging** — Role and model in start message

**Task Completion Logging** — Elapsed time, status, tool call count

**Stopwatch Usage** — For accurate timing

**7 Logging Tests** — For `SendPromptAsync`

**22 WorkerLogger Tests** — For `Info`, `Error`, `Debug`, `LogBlock`

**27 GrpcMapper Tests** — Comprehensive coverage

**WorkTask Round-Trip** — Domain → gRPC → domain

**TaskResult Round-Trip** — Preserving all fields

**Null/Empty Handling** — For BranchSpec, TaskMetrics, GitChangeSummary

**Enum Bidirectional Mappings** — All values covered

**WorkerRole Mappings** — All 6 roles

**Edge Cases** — Zero test counts, 0% and 100% coverage

**Unknown Enum Values** — Throw `InvalidOperationException`

**TaskStatus Handling** — `Unspecified` and `InProgress` throw

**Health Uptime Field** — Formatted as `"HH:mm:ss"`

**UptimeSpan Field** — For programmatic parsing

**Hours Exceeding 24** — Supported for long-running servers

**Test Verification** — Format matches expected pattern

**GoalId.Validate Method** — Static validation

**Kebab-Case Enforcement** — Lowercase, letters, digits, hyphens only

**No Leading/Trailing Hyphens** — Validation rule

**Git Branch Convention** — Matching goal IDs

**5 Valid Cases** — `fix-build-error`, `add-feature`, etc.

**8 Invalid Cases** — Empty, uppercase, spaces, underscores, hyphens

**Dispatch Logging Tests** — Priority level verification

**Phase Duration Logging Tests** — Duration in seconds verification

**PhaseDurations Tracking** — In goals.yaml

**GoalUpdateMetadata** — Includes phase durations

**Goal** — Includes phase durations

**GoalFileEntry** — Includes phase durations

**FileGoalSource** — Reads and writes phase_durations

**GoalDispatcher Wiring** — Pipeline metrics to completion metadata

**4 Phase Duration Tests** — Multiple scenarios

**IterationSummary Tests** — 7 tests for structure, serialization, round-trip

**Multi-Iteration Appending** — Verified

**Null Handling** — For test counts and phase results

**ImproverSkipped Flag** — Tracked in metrics

**ImproverSkipReason** — Recorded in goal notes

**Non-Fatal Observations** — In `Goal.Notes`

**Iteration Plan Helper** — `NextPhaseAfter(GoalPhase)`

**Retry Mechanism** — Up to 2 retries with 5-second backoff

**Timeout Handling** — In `AskAsync`

**Transient Error Handling** — With retry

**JSON Parse Failure Handling** — With retry

**Dirty Worktree Detection** — `HasUncommittedChangesAsync()`

**Safety Net** — Re-prompts up to 2 times

**Uncommitted Changes** — After task execution

**IterationSummary Structure** — Number, results, counts, verdict, notes

**PhaseResult Structure** — Name, result, duration

**TestCounts Structure** — Total, passed, failed

**Goal.IterationSummaries** — List of summaries

**GoalUpdateMetadata.IterationSummary** — Optional structured summary

**FileGoalSource Round-Trip** — Full fidelity

**BuildIterationSummary** — Constructs from pipeline metrics

**Failed Phase Marking** — As "fail" in summary

**Skipped Phases** — Tracked (e.g., improver timeout)

**Constants Extraction** — For version and cleanup defaults

**OrchestratorVersion** — In `Constants.cs`

**CleanupDefaults** — For interval and timeout

**Non-Blocking Improver** — Failures logged, pipeline continues

**Three-Dot Syntax** — For git diff comparisons

**DocWriter Restriction** — No .cs edits, no builds

**XML Doc Review** — Flags issues in report

**Brain Prompt Hardening** — No git commands, no framework commands

**Improver Prompt** — File paths only, not contents

**Single Line Commit** — Simplified instruction

**Brain Reviewer Diff** — `origin/` prefix

**Coverage Collector** — `--collect:"XPlat Code Coverage"`

**Conflict Resolution** — Coverlet package conflict fixed

**DELETE Validation** — 400 Bad Request for invalid status

**404 Not Found** — When goal doesn't exist

**400 Bad Request** — Clear error message

**Duplicate Improve** — Entry removal before append

**ImproverSkipped True** — With existing entry

**Null Result Handling** — Throws instead of defaults

**Silent Fallback** — Convention violation fixed

**Coder No-Op Root Cause** — `git checkout -b` commands removed

**BOM Removal** — From `CopilotHive.Worker.csproj`

**Status Restrictions** — Enforced correctly

**Pending Goal** — Returns 400 Bad Request

**Draft/Failed Only** — Deletion allowed

**Consistent Behavior** — API and Composer

**Invalid Transitions** — Blocked with clear messages

### Removed

**WORKER_ROLE Env Var** — Removed; workers are now always generic

**Legacy CLI Orchestrator** — 2,922 lines of legacy CLI-mode orchestrator code removed

**Legacy Copilot Abstractions** — Legacy Copilot client abstraction layer removed

**CleanupGoalSessionAsync / ReprimeSessionAsync** — Removed from `IDistributedBrain` interface

**Per-Goal AgentSession** — Per-goal session management replaced by persistent Brain session

**Auto-Rebase Complexity** — Simplified merge flow removes complex rebase handling

**Unused Metrics Folder** — Empty `metrics/` folder removed from repository

**MergeViaTempCloneAsync** — Dead code path removed

**goals.yaml Tab** — Removed from Configuration page; goals managed via Goals page

**Coverlet.MSBuild** — Conflicting package removed

**BOM** — From `CopilotHive.Worker.csproj`

**Per-Goal Sessions** — `ConcurrentDictionary<string, AgentSession>`

**SendToBrainCoreAsync** — Replaced by `ExecuteBrainAsync`

**FunctionInvokingChatClient** — Wrapping in Brain removed

**Auto-Rebase** — Complex handling removed

**CLI Mode** — Entirely removed

**OrchestratorBrain** — Legacy brain removed

**Orchestrator.cs** — Legacy orchestrator removed

**CopilotWorkerClient** — Legacy client removed

**ICopilotWorkerClient** — Interface removed

**CopilotClientFactory** — Factory removed

**53 Legacy Tests** — Removed (333 remain)

**--serve Flag** — Server mode is default

**Fixed-Role Services** — Replaced by generic pool

**Metrics Folder** — Empty folder removed

**Legacy Code** — All CLI-mode orchestrator code

**Abstraction Layer** — Legacy Copilot client abstractions
