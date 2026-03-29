# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] - 2025-07-15

### Added

**Composer Agent** — Conversational goal decomposition with Chat UI, persistent sessions, streaming responses, and 7 LLM-callable tools: `create_goal`, `approve_goal`, `update_goal`, `delete_goal`, `get_goal`, `list_goals`, `search_goals`. Includes codebase tools (`read_file`, `glob`, `grep`) for informed goal creation.

**Composer Tools** — Extended toolset for the Composer agent:
- **ask_user** — Interactive questions (YesNo, SingleChoice, MultiChoice) with markdown rendering and optional feedback
- **web_search / web_fetch** — Web research via Ollama API with result truncation to prevent context overflow
- **Git Tools** — Read-only git inspection (`git_log`, `git_diff`, `git_show`, `git_branch`, `git_blame`) against Brain's repo clones
- **list_repositories** — Query live hive-config.yaml for configured repositories
- **get_phase_output** — Retrieve worker output from specific pipeline phases, with optional content parameter for brain/worker prompts
- **Composer Phase Detail** — Composer can view per-phase detail via the phase detail tool

**Chat UI** — Real-time streaming chat interface at `/composer` with Enter-to-send, session reset, cancel support, full Markdown rendering (Markdig), auto-scroll, and session restoration on navigation.

**Brain CodingAgent Migration** — Brain now uses SharpCoder's `CodingAgent` with persistent session across all goals, automatic context compaction at 80% threshold, session persistence to `brain-session.json`, and built-in file tools on repo clones.

**Brain Read-Only File Access** — Brain can read project structure, config, and code via `read_file`, `glob`, `grep` tools on persistent repo clones at `{stateDir}/repos/{repoName}`.

**Brain Context Management** — Context overflow auto-recovery detects `model_max_prompt_tokens_exceeded` errors and automatically resets the session with user notification.

**SQLite Goal Store** — `SqliteGoalStore` as primary goal persistence with CRUD operations, text search, status filtering, iteration tracking, and automatic schema migration. Replaces `FileGoalSource` and `ApiGoalSource` in the pipeline.

**Goals REST API** — Full CRUD endpoints: `GET /api/goals`, `POST /api/goals`, `GET /api/goals/{id}`, `PATCH /api/goals/{id}/status`, `DELETE /api/goals/{id}`, `GET /api/goals/search` with SQLite backing.

**Goals Browser Dashboard** — `/goals` page with search bar, status/priority/repository filter dropdowns, sortable table, and inline actions (approve, delete, revert to draft).

**Goal Detail Page** — Inline prompt display (Brain prompt, Worker prompt), planning phase with reasoning visibility, dependency visualization with status indicators, worker output Markdown rendering, merge commit hash links, and "← Goals" back navigation.

**Goal Management** — Goal lifecycle actions available from the dashboard and Composer:
- **Approval** — Approve goals from dashboard (Draft → Pending)
- **Deletion** — Delete Draft/Failed goals with remote branch cleanup
- **Cancellation** — Cancel InProgress/Pending goals via API, dashboard, and Composer
- **Revert to Draft** — Restore Pending goals to Draft state

**Goal Dependencies** — Goals can declare `DependsOn` list of goal IDs that must complete before dispatch; persisted in SQLite and YAML; cross-source dependency resolution; visualization with status indicators.

**Goal Scope** — `GoalScope` enum (Patch/Feature/Breaking) persisted in SQLite and YAML, exposed in Composer tools and dashboard.

**Release Management** — Release entity with CRUD API, SQLite persistence, goal-to-release assignment, dashboard UI (Releases page, detail view, assignment on Goals).

**Configuration Page** — YAML syntax highlighting, Markdown rendering for agent files, removed `goals.yaml` tab (goals managed via Goals page).

**Worker Session Persistence** — Per-role agent sessions via gRPC `GetSession`/`SaveSession`, resuming conversations across iterations.

**Worker Output Persistence** — `PhaseResult.WorkerOutput` stores raw output inline; `IterationSummary.PhaseOutputs` dictionary for completed iterations; persisted to SQLite and YAML.

**Brain Repo Manager** — Persistent repo clones for Brain file access and merge operations; squash merge with Brain-generated commit messages; merge commit hash capture and display.

**Eager Repository Cloning** — Brain clones all repos at startup instead of lazily.

**Conversation Entry Metadata** — `ConversationEntry` tracks iteration context (`Iteration`, `Purpose`) for analyzing history by iteration without heuristic parsing.

**GitHub Actions CI Workflow** — Automated build, test, and container image push to GHCR on `develop` branch pushes. Multi-architecture Docker support (amd64/arm64) with cross-compilation to avoid QEMU segfaults.

**Structured Logging Migration** — Orchestrator logging migrated from `Console.WriteLine` to `ILogger` with structured properties across `DistributedBrain`, `Program`, `AgentsManager`, `MetricsTracker`, and `DockerWorkerManager`.

**Observability** — Expanded runtime visibility:
- **Push Error Visibility** — Git push failures reported to Brain via `[Git Push Errors]` section in worker output
- **HTTP Resilience** — 3 retries with exponential backoff on transient HTTP failures
- **Worker Utilization Metrics** — Per-worker utilization tracking with `/health/utilization` endpoint
- **Brain Context Logging** — Context usage percentage logged after each Brain call
- **Model Name Logging** — Actual model name included in completion log entries
- **Goal Elapsed Time** — Total goal duration logged with human-readable formatting
- **Phase Duration** — Per-phase elapsed time logged on completion

**Version Display** — CopilotHive version shown in navigation from assembly metadata; `VersionPrefix` in `Directory.Build.props` for CI-driven versioning.

**Conditional DocWriting** — Brain conditionally includes/skips DocWriting phase based on goal description.

**Worker Pool** — Generic worker pool with `WORKER_REPLICAS` configuration; `IsGeneric` flag for pool identity tracking; `TryDequeueAny()` for role-agnostic dispatch.

**Testing Infrastructure** — 636+ xUnit tests including integration tests, gRPC mapper tests, worker logging tests, Composer tool tests, SQLite store tests, and pipeline lifecycle tests.

**Additional Features** — Smaller improvements shipped in this release:
- **Repository Filter** — Repository filter dropdown on Goals page
- **Inline Prompts** — Collapsible Brain and Worker prompt display in Goal Detail
- **Planning Phase** — Visible Planning phase in iteration timeline
- **Worker Output Markdown** — Worker output rendered as Markdown in Goal Detail
- **Goal ID Validation** — Lowercase kebab-case enforcement for goal identifiers
- **Premium Model Tier** — Tier selection for premium model usage
- **Iteration Summary** — Tracking with per-phase results for each completed iteration
- **Auto-Rebase** — Automatic rebase on merge conflicts
- **Agents.md Versioning** — Versioning and rollback on metric regression
- **Config Repo Integration** — Hive config loaded from a dedicated config repository
- **Telemetry Aggregation** — Aggregated metrics across goals and iterations
- **SharpCoder Streaming** — `IAsyncEnumerable<StreamingUpdate>` for real-time token streaming
- **DurationFormatter** — Utility class for human-readable duration strings
- **Enum Display Names** — Display name extensions for `GoalPhase`, `WorkerRole`, `GoalPriority`, `GoalStatus`

### Changed

- **SharpCoder** — Updated from 0.4.4 to 0.5.0
- **Pipeline Phase Order** — Coding → Testing → Review → DocWriting → Improve → Merge
- **Docker Compose** — Single `worker` service with replicas replaces fixed-role services
- **Configuration** — `ComposerConfig` moved to top-level in `HiveConfigFile`
- **Goal Manager** — Now uses `SqliteGoalStore` as sole goal source
- **Health Endpoint** — Backed by `SqliteGoalStore`
- **Brain Prompts** — Instructed to discover HOW not WHAT; no git checkout/branch/switch/push commands; no framework-specific build/test commands
- **Reviewer Scope** — DocWriter scope restricted to .md files only
- **Improver** — Non-blocking; failures logged as warnings and pipeline continues to Merging
- **Coverage Collection** — Switched from `coverlet.msbuild` to `--collect:"XPlat Code Coverage"`
- **Smoke Test** — Preserves persistent volumes, uses Composer for goal setting
- **Global Scrollbar** — Thin scrollbar styling applied globally

### Fixed

- **Empty Repository Clone** — Startup no longer fails when cloning an empty repository
- **Reviewer Scope Enforcement** — Reviewer scope now correctly matches `GoalScope`
- **Eager Push Model Display** — `CurrentModel` shown correctly during premium tier pushes
- **Release Form Repository Dropdown** — Dropdown now shows all configured repositories
- **Diagnostics File Path** — Default changed from `/app/diagnostics` to system temp directory
- **Runner Reset Test** — CI test now uses factory delegate instead of `GH_TOKEN`
- **Session Save Cancellation** — `SaveSessionAsync` no longer re-throws `OperationCanceledException`
- **Worker Container Restart** — `entrypoint.sh` uses clone-or-pull pattern to survive restarts
- **web_search Overflow** — Per-result truncation to 500 chars prevents context window overflow
- **web_fetch Line Limit** — Default `max_lines` reduced from 200 to 100
- **Push Error Visibility** — Git push failures now reported to Brain in worker output
- **QEMU Segfault** — arm64 Docker images built via cross-compilation instead of QEMU emulation
- **SQLite Readonly in CI** — Fixed "readonly database" error caused by test collection sharing
- **BrainRepoManager CI Failures** — Tests now use isolated temporary directories
- **GoalDetail Delete Handling** — Delete errors shown as inline messages instead of exceptions
- **Worker Model Display** — Shows actual model name including premium tier suffix
- **Dependency Link Readability** — Dependency links in Goal Detail use accent color for contrast
- **Duplicate Improve Phase** — Duplicate "Improve" entries in iteration summary eliminated
- **Null PhaseResult Field** — Null `Result` field in `PhaseResultEntry` handled gracefully
- **Coder No-Op Commands** — Coder no longer issues `git checkout -b` commands from prompts
- **BOM in Worker Project** — BOM removed from `CopilotHive.Worker.csproj`
- **Goal Delete Validation** — `DELETE /api/goals/{id}` enforces status restrictions correctly

### Removed

- **WORKER_ROLE Env Var** — Removed; workers are now always generic
- **Legacy CLI Orchestrator** — 2,922 lines of legacy CLI-mode orchestrator code removed
- **Legacy Copilot Abstractions** — Legacy Copilot client abstraction layer removed
- **CleanupGoalSessionAsync / ReprimeSessionAsync** — Removed from `IDistributedBrain` interface
- **Per-Goal AgentSession** — Per-goal session management replaced by persistent Brain session
- **Auto-Rebase Complexity** — Simplified merge flow removes complex rebase handling
- **Unused Metrics Folder** — Empty `metrics/` folder removed from repository
- **MergeViaTempCloneAsync** — Dead code path removed
- **goals.yaml Tab** — Removed from Configuration page; goals managed via Goals page
