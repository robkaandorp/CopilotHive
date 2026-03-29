# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] - 2025-07-15

### Added

**Composer Agent:** Conversational goal decomposition with Chat UI, persistent sessions, streaming responses, and 7 LLM-callable tools: `create_goal`, `approve_goal`, `update_goal`, `delete_goal`, `get_goal`, `list_goals`, `search_goals`. Includes codebase tools (`read_file`, `glob`, `grep`) for informed goal creation.

**Composer Tools:**
- `ask_user` — Interactive questions (YesNo, SingleChoice, MultiChoice) with markdown rendering and optional feedback
- `web_search` and `web_fetch` — Web research via Ollama API with result truncation to prevent context overflow
- `git_log`, `git_diff`, `git_show`, `git_branch`, `git_blame` — Read-only git inspection tools against Brain's repo clones
- `list_repositories` — Query live hive-config.yaml for configured repositories
- `get_phase_output` — Retrieve worker output from specific pipeline phases, with optional content parameter for brain/worker prompts

**Chat UI:** Real-time streaming chat interface at `/composer` with Enter-to-send, session reset, cancel support, full Markdown rendering (Markdig), auto-scroll, and session restoration on navigation.

**Brain CodingAgent Migration:** Brain now uses SharpCoder's `CodingAgent` with persistent session across all goals, automatic context compaction at 80% threshold, session persistence to `brain-session.json`, and built-in file tools on repo clones.

**Brain Read-Only File Access:** Brain can read project structure, config, and code via `read_file`, `glob`, `grep` tools on persistent repo clones at `{stateDir}/repos/{repoName}`.

**Brain Context Management:** Context overflow auto-recovery detects `model_max_prompt_tokens_exceeded` errors and automatically resets the session with user notification.

**SQLite Goal Store:** `SqliteGoalStore` as primary goal persistence with CRUD operations, text search, status filtering, iteration tracking, and automatic schema migration. Replaces `FileGoalSource` and `ApiGoalSource` in the pipeline.

**Goals REST API:** Full CRUD endpoints: `GET /api/goals`, `POST /api/goals`, `GET /api/goals/{id}`, `PATCH /api/goals/{id}/status`, `DELETE /api/goals/{id}`, `GET /api/goals/search` with SQLite backing.

**Goals Browser Dashboard:** `/goals` page with search bar, status/priority/repository filter dropdowns, sortable table, and inline actions (approve, delete, revert to draft).

**Goal Detail Page:** Inline prompt display (Brain prompt, Worker prompt), planning phase with reasoning visibility, dependency visualization with status indicators, worker output Markdown rendering, merge commit hash links, and "← Goals" back navigation.

**Goal Management:**
- Approval from dashboard (Draft → Pending)
- Deletion for Draft/Failed goals with remote branch cleanup
- Cancellation for InProgress/Pending goals via API, dashboard, and Composer
- "Revert to Draft" for Pending goals

**Goal Dependencies:** Goals can declare `DependsOn` list of goal IDs that must complete before dispatch; persisted in SQLite and YAML; cross-source dependency resolution; visualization with status indicators.

**Goal Scope:** `GoalScope` enum (Patch/Feature/Breaking) persisted in SQLite and YAML, exposed in Composer tools and dashboard.

**Release Management:** Release entity with CRUD API, SQLite persistence, goal-to-release assignment, dashboard UI (Releases page, detail view, assignment on Goals).

**Configuration Page:** YAML syntax highlighting, Markdown rendering for agent files, removed `goals.yaml` tab (goals managed via Goals page).

**Worker Session Persistence:** Per-role agent sessions via gRPC `GetSession`/`SaveSession`, resuming conversations across iterations.

**Worker Output Persistence:** `PhaseResult.WorkerOutput` stores raw output inline; `IterationSummary.PhaseOutputs` dictionary for completed iterations; persisted to SQLite and YAML.

**Brain Repo Manager:** Persistent repo clones for Brain file access and merge operations; squash merge with Brain-generated commit messages; merge commit hash capture and display.

**Eager Repository Cloning:** Brain clones all repos at startup instead of lazily.

**Conversation Entry Metadata:** `ConversationEntry` tracks iteration context (`Iteration`, `Purpose`) for analyzing history by iteration without heuristic parsing.

**GitHub Actions CI Workflow:** Automated build, test, and container image push to GHCR on `develop` branch pushes. Multi-architecture Docker support (amd64/arm64) with cross-compilation to avoid QEMU segfaults.

**Structured Logging Migration:** Orchestrator logging migrated from `Console.WriteLine` to `ILogger` with structured properties across `DistributedBrain`, `Program`, `AgentsManager`, `MetricsTracker`, and `DockerWorkerManager`.

**Observability:**
- Push error visibility with `[Git Push Errors]` section in worker output
- HTTP resilience with 3 retries and exponential backoff
- Worker utilization metrics with `/health/utilization` endpoint
- Brain context-usage logging after each call
- Model name in completion logs
- Goal elapsed time logging with human-readable formatting
- Phase duration logging

**Version Display:** CopilotHive version shown in navigation from assembly metadata; `VersionPrefix` in `Directory.Build.props` for CI-driven versioning.

**Conditional DocWriting:** Brain conditionally includes/skips DocWriting phase based on goal description.

**Worker Pool:** Generic worker pool with `WORKER_REPLICAS` configuration; `IsGeneric` flag for pool identity tracking; `TryDequeueAny()` for role-agnostic dispatch.

**Testing Infrastructure:** 636+ xUnit tests including integration tests, gRPC mapper tests, worker logging tests, Composer tool tests, SQLite store tests, and pipeline lifecycle tests.

**Additional Features:**
- Repository filter dropdown on Goals page
- Inline prompt display in Goal Detail with collapsible sections
- Visible Planning phase in iteration timeline
- Worker output rendering as Markdown
- Goal ID validation (lowercase kebab-case)
- Premium model tier selection
- Iteration summary tracking with per-phase results
- Auto-rebase on merge conflicts
- Agents.md versioning and rollback on regression
- Config repo integration
- Telemetry aggregation
- SharpCoder streaming with `IAsyncEnumerable<StreamingUpdate>`
- `DurationFormatter` utility for human-readable durations
- Display name extensions for enums (`GoalPhase`, `WorkerRole`, `GoalPriority`, `GoalStatus`)

### Changed

- **SharpCoder:** Updated from 0.4.4 to 0.5.0
- **Pipeline Phase Order:** Coding → Testing → Review → DocWriting → Improve → Merge
- **Docker Compose:** Single `worker` service with replicas replaces fixed-role services
- **Configuration:** `ComposerConfig` moved to top-level in `HiveConfigFile`
- **Goal Manager:** Now uses `SqliteGoalStore` as sole goal source
- **Health Endpoint:** Backed by `SqliteGoalStore`
- **Brain Prompts:** Instructed to discover HOW not WHAT; no git checkout/branch/switch/push commands; no framework-specific build/test commands
- **Reviewer Scope:** DocWriter scope restricted to .md files only
- **Improver:** Non-blocking — failures logged as warnings, pipeline continues to Merging
- **Coverage Collection:** Switched from `coverlet.msbuild` to `--collect:"XPlat Code Coverage"`
- **Smoke Test:** Preserves persistent volumes, uses Composer for goal setting
- **Global Scrollbar:** Thin scrollbar styling applied globally

### Fixed

- Empty repository clone failure at startup
- Reviewer scope enforcement matching GoalScope
- Eager push CurrentModel display during premium tier pushes
- Release form repository dropdown showing all configured repos
- Diagnostics file default path (changed from /app/diagnostics to system temp directory)
- Runner reset test in CI using factory delegate instead of GH_TOKEN
- Session save cancellation (no longer re-throws OperationCanceledException)
- Worker container restart failure in `entrypoint.sh` (clone-or-pull pattern)
- `web_search` context window overflow (per-result truncation to 500 chars)
- `web_fetch` default max_lines reduced from 200 to 100
- Push error visibility (git push failures now reported to Brain)
- QEMU segfault when building arm64 Docker images (cross-compilation)
- SQLite "readonly database" error in CI (test collection sharing)
- BrainRepoManagerTests CI failures (temporary directories)
- GoalDetail.razor delete error handling (inline messages)
- Worker model display (shows actual model including premium tier)
- Dependency link readability in Goal Detail (accent color)
- Duplicate "Improve" phase entries in iteration summary
- Null `Result` field handling in `PhaseResultEntry`
- Coder no-ops from `git checkout -b` commands in prompts
- BOM removal from `CopilotHive.Worker.csproj`
- DELETE `/api/goals/{id}` endpoint validation (status restrictions)

### Removed

- WORKER_ROLE env var (workers are now always generic)
- Legacy CLI-mode orchestrator code (2,922 lines)
- Legacy Copilot client abstractions
- `CleanupGoalSessionAsync` and `ReprimeSessionAsync` from `IDistributedBrain`
- Per-goal `AgentSession` management
- Auto-rebase complexity in merge flow
- Unused metrics/ folder
- Dead `MergeViaTempCloneAsync` code path
- `goals.yaml` tab from Configuration page

