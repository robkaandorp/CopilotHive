[![CI](https://github.com/robkaandorp/CopilotHive/actions/workflows/ci.yml/badge.svg)](https://github.com/robkaandorp/CopilotHive/actions/workflows/ci.yml)

# CopilotHive

CopilotHive is a **self-improving multi-agent orchestration system** powered by **[SharpCoder](https://github.com/robkaandorp/SharpCoder)** 0.16.0 (an autonomous coding agent library). A pool of generic worker agents collaborate autonomously inside Docker containers — dynamically taking on roles (coder, tester, doc-writer, reviewer, improver) per task — to implement software goals without human intervention. A conversational **Composer** agent helps decompose high-level intent into actionable goals through a streaming chat interface.

## Architecture

The Orchestrator Brain (an LLM-powered decision engine) receives goals and dispatches work to a pool of generic workers. Each worker runs in an isolated Docker container and accepts any role (coder, tester, doc-writer, reviewer, improver) per task. The **Composer** provides a conversational interface for goal decomposition and management.

```
                    ┌─────────────────┐
                    │  Orchestrator   │
                    │     Brain       │
                    │  (LLM-powered)  │
                    └────────┬────────┘
                             │ gRPC
          ┌──────────┬───────┼───────┬──────────┐
          │          │       │       │          │
   ┌──────▼──────┐ ┌─▼──────────┐ ┌──▼───────┐ ┌▼──────────┐
   │  Worker 1  │ │  Worker 2  │ │ Worker 3 │ │ Worker 4  │
   │  (Docker)  │ │  (Docker)  │ │ (Docker) │ │ (Docker)  │
   └────────────┘ └────────────┘ └──────────┘ └───────────┘
        any role       any role      any role      any role

   ┌─────────────────────────────────────────────────────────┐
   │  Composer (Chat UI at /composer)                       │
   │  Streaming LLM conversation for goal decomposition     │
   └─────────────────────────────────────────────────────────┘
```

## How It Works

Goals flow through a structured pipeline:

**Content block(s) → Testing → Review → (Improve) → Merging**

1. **Content block** — One or more content phases run in sequence: **Coding** (a worker assigned the coder role implements the goal on a feature branch) and/or **Doc Writing** (a worker assigned the doc-writer role updates documentation). The Brain includes Doc Writing only when the goal requires documentation changes; it is part of the same content block as Coding (when both are needed) or stands alone.
2. **Testing** — A worker (assigned the tester role) builds the project and runs all tests. Each content block is immediately followed by Testing.
3. **Review** — A worker (assigned the reviewer role) inspects the diff, tests, and documentation.
4. **Improve** *(optional, non-blocking)* — A worker (assigned the improver role) updates `agents.md` based on metrics. If improvement fails, the pipeline still completes — the failure is recorded in goal notes and metrics.
5. **Merging** — The Brain decides when quality is sufficient and squash-merges the branch, combining all feature branch commits into a single descriptive commit.

If testing or review fails, the pipeline retries the content block (up to a configured limit).

The **Brain** (`DistributedBrain`) plans iteration phases and crafts worker prompts using SharpCoder's `CodingAgent` for LLM communication. The Brain maintains a **single persistent session** across all goals with automatic context compaction (infinite context), and has **read-only file access** to target repositories for informed decision-making. Goals are processed **in parallel** (up to a configurable `MaxParallelGoals` limit) with each goal running its own Brain session forked from a shared master session. Workers report structured verdicts via tool calls, and the pipeline state machine (`PipelineStateMachine`) drives sequencing — retrying, advancing, or failing based on those verdicts. Pipeline state and goals are persisted to a single **SQLite** database (`copilothive.db`) via **Entity Framework Core**, and the Brain and Composer sessions are persisted to `brain-master.json` and `composer-session.json` respectively, so the server can resume after restarts. Per-goal progress documents in the knowledge graph capture the narrative of each iteration — the Brain's plan, worker narratives, and iteration summaries — providing rich context for the Composer, human, and Brain itself. Optional **GitHub OAuth authentication** protects the dashboard when configured. Metrics feed into the **improver** for self-improvement: the system tunes its own `agents.md` instructions over time.

## Getting Started

### Prerequisites

- [Docker](https://www.docker.com/) (latest stable)
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- A GitHub token (`GH_TOKEN`) or LLM provider API key for model access
- A **config repo** containing `hive-config.yaml` with model and worker configuration (see below)

### Setup

1. Clone the repository:
   ```bash
   git clone https://github.com/robkaandorp/CopilotHive.git
   cd CopilotHive
   ```

2. Build and run:
   ```bash
   dotnet build CopilotHive.slnx
   dotnet run --project src/CopilotHive -- \
     --port=9000 \
     --config-repo=https://github.com/your-org/CopilotHive-Config \
     --config-repo-path=./config-repo
   ```

   This starts a **gRPC server** on port 9000 and an **HTTP health endpoint** on port 9001.

   Set `BRAIN_CONTEXT_WINDOW` to configure the Brain's maximum context window in tokens (default: 150,000). Worker context windows are configured via `orchestrator.worker_context_window` (global default) or `workers.<role>.context_window` (per-role override) in `hive-config.yaml`.

### Authentication (Optional)

By default, CopilotHive runs without authentication (open mode). To enable GitHub OAuth authentication:

1. Create a GitHub OAuth App (GitHub → Settings → Developer settings → OAuth Apps → New OAuth App)
   - Authorization callback URL: `http://localhost:9001/signin-github`
2. Set environment variables:
   - `GITHUB_OAUTH_CLIENT_ID` — your OAuth App Client ID
   - `GITHUB_OAUTH_CLIENT_SECRET` — your OAuth App Client Secret
3. Restart CopilotHive — the dashboard will require authentication

The first GitHub user to sign in becomes the admin. The OAuth access token replaces the need for `GH_TOKEN`.

> **Plain-HTTP deployments**: When serving the dashboard over plain HTTP on a non-localhost hostname (internal LAN / docker swarm), you must set `ALLOW_INSECURE_OAUTH=true` or the OAuth handshake will fail with "Correlation failed" (browsers silently drop `Secure`-marked cookies over `http://` on non-localhost hosts). This is safe only on a trusted internal network, because OAuth cookies would otherwise be sent in the clear.

### Configuring Goals

Goals are stored in **SQLite** (`copilothive.db`) as the primary source of truth. The recommended way to create goals is through the **Composer Chat UI** at `/composer`, which provides a conversational interface for decomposing high-level intent into well-scoped goals. Goals can also be created via the REST API (`POST /api/goals`).

> **Pre-upgrade migration notice**: If you are using goals.yaml, run v0.19.x once with `--goals-file=goals.yaml` to import goals into SQLite. Verify import succeeded by checking the dashboard or `GET /api/goals` before upgrading to v0.20.0+ — the import path is removed in v0.20.0.

**Goal ID Format**: Goal IDs must be non-empty, lowercase kebab-case identifiers containing only letters (a–z), digits (0–9), and hyphens (–). IDs must not start or end with a hyphen (e.g., `fix-build-error`, `add-feature`, `release-v1-0`). This format mirrors git branch naming conventions (e.g., `copilothive/{goal-id}`). Invalid goal IDs will throw an `ArgumentException` with a descriptive error message.

## Project Structure

| Directory | Description |
|-----------|-------------|
| `src/CopilotHive/` | Main orchestrator — Brain, GoalDispatcher, persistence, metrics |
| `src/CopilotHive.Shared/` | Shared protobuf definitions and DTOs |
| `src/CopilotHive.Worker/` | Worker process (runs inside Docker containers) |
| `tests/` | 3700+ xUnit tests |
| `agents/` | Default agent templates (overridden by config repo at runtime) |
| `docker/` | Dockerfiles and container configuration |

## Current Features

- **Server-only mode** — gRPC server + HTTP health endpoint (no CLI mode)
- **TargetRepositoryNames** — Multi-repo goals with editable target repos and read-only reference repos
- **NuGet publish monitoring** — automatic polling of the NuGet API after releases, with `PackagePublished` events on the event bus
- **Autonomous composer mode** — all 9 supported active-event types available as opt-in active events, with Autopilot/Normal presets for full autonomous operation
- **SharpCoder.Providers** — Model provider code extracted to a shared NuGet package. CopilotHive now depends on `SharpCoder.Providers` instead of maintaining its own `ChatClientFactory`
- **GitHub OAuth workflow scope** — Workers can now push workflow file changes
- **LLM-powered Brain** — `DistributedBrain` uses SharpCoder's `CodingAgent` with per-goal Brain sessions forked from a persistent master session, read-only file access to repos, automatic context compaction, and configurable context window (`BRAIN_CONTEXT_WINDOW`). **Chunked compaction** — when `CompactionMaxTokens` is configured (from the compaction model's context window in `AvailableModels`), old messages exceeding the budget are split into token-budgeted chunks, each summarized separately, preventing compaction model overflow. Brain dashboard metrics always reflect the master session for stable context usage readings; exact token counts from the API (`LastKnownContextTokens`) used instead of character-based heuristic estimates. **Live Brain Model Switching** — the Brain's model and context window can be changed from the Configuration page and take effect within seconds (event-driven notifications via DashboardNotifier (with a 10-second timer for time-based displays)). **Worker context windows are configurable** per-role or globally via `hive-config.yaml` (`workers.<role>.context_window` or `orchestrator.worker_context_window`). **Premium Worker Models** — each role has an optional premium model tier; when the Brain escalates a phase via the `model_tiers` mechanism in `orchestrator.agents.md`, the dispatcher uses the role's premium model for that phase
- **Decomposed Brain** — `DistributedBrain` was split into focused services: `BrainPromptBuilder` (static prompt construction), `BrainPlanParser` (plan parsing/validation), and a slim coordinator, improving testability and making each concern independently modifiable
- **Unified model resolution** — All model configuration now flows through `HiveConfigFile` with a three-tier resolution chain: per-model overrides → per-role defaults → global default. This eliminated inconsistencies where the Brain and workers could pick different models
- **In-App Model Configuration** — Users can change model configuration from the Configuration dashboard page. A "Models" tab provides dropdown selectors for the orchestrator (Brain), composer, per-role workers, and compaction model, populated from the global `Models.AvailableModels` list in `hive-config.yaml`. Changes take effect on the next goal dispatch and are written back to `hive-config.yaml` via `ConfigModelService`. The Composer also uses this unified global model list. Exposed via `GET /api/config/models` and `PATCH /api/config/models`. `hive-config.yaml` and `orchestrator.agents.md` changes are picked up on the next `SyncRepoAsync` cycle without an orchestrator restart.
- **Strongly-typed Brain tool results** — `BrainToolCallResult` replaced with discriminated-union records: `EscalateResult`, `IterationPlanResult`, and `GoalLookupResult`, eliminating string-matching on Brain tool results
- **Knowledge Graph** — Document data model with `KnowledgeDocument`, `DocumentLink`, and related entities. `KnowledgeGraph` service with CRUD, link management, inverse queries, BFS traversal, YAML frontmatter handling. `Goal.Documents` field for associating documents with goals. Dashboard pages at `/knowledge` and `/knowledge/{DocumentId}`. `KnowledgeGraph.Search()` uses tokenized multi-term AND-logic search — queries are split on whitespace, hyphens, underscores, and punctuation so multi-word queries match hyphenated document IDs and titles.
- **Brain knowledge tools** — `search_knowledge`, `read_document`, and `traverse_graph` tools added to the Brain for querying and exploring the knowledge graph during planning
- **Composer** — conversational agent for goal decomposition and management with streaming chat UI (`/composer`); uses a persistent SharpCoder session with 9 goal-management tools (`create_goal`, `approve_goal`, `update_goal` — now supports full Draft goal editing: description, priority, scope, repositories, depends_on, and documents can all be changed on Draft goals, `delete_goal`, `cancel_goal`, `get_goal` (parameterless, goal ID injected by runtime), `list_goals`, `search_goals` — uses tokenized multi-term search (AND logic), matching hyphenated goal IDs and multi-word queries, `get_goal` (with goal ID parameter for explicit fetching)), 3 release-management tools (`create_release`, `list_releases`, `edit_planning_releases`), **9 knowledge tools** (`create_document`, `read_document`, `update_document`, `delete_document`, `search_knowledge`, `list_documents`, `link_document`, `unlink_document`, `traverse_graph`), codebase tools (`read_file`, `glob`, `grep`), 5 git tools (`git_log`, `git_diff`, `git_show`, `git_branch`, `git_blame`), 2 web research tools (`web_search`, `web_fetch`) — available only when `OLLAMA_API_KEY` is set, phase output inspection (`get_phase_output` with `content` parameter to retrieve specific output text), repository listing (`list_repositories`) — queries live config to show all configured repositories, interactive user questions (`ask_user`) — supports Yes/No, SingleChoice, and MultiChoice questions with optional feedback, **new context status bar** (shows context usage % and compaction indicator), **new multi-model dropdown** with model selection **persisted** across sessions, and **5 config repo tools** (`list_config_files`, `read_config_file`, `update_agents_md`, `edit_agents_md`, `commit_config_changes`) for direct AGENTS.md inspection and updates; **clarification auto-answer capability** — can auto-answer Brain clarification questions using a forked LLM session; **goal creation pre-flight checklist** — system prompt includes verification checklist and approval policy before dispatch; **operating procedures in system prompt** — startup instructions to read `memory-composer-operating-procedures` and the idea-to-implementation transition convention from the knowledge graph, ensuring procedures survive session resets; full Markdown rendering (Markdig) and chat history persistence across page navigations; **automatic context overflow recovery** — detects `model_max_prompt_tokens_exceeded` errors and auto-resets the session to recover
- **Parallel goal dispatch** — Goals execute concurrently up to a configurable `MaxParallelGoals` limit. Each goal runs with its own Brain session forked from a shared master session, allowing multiple goals to progress simultaneously. When a goal completes, a summary merges back into the master session so accumulated learnings are retained
- **Multi-round coding iterations** — The Brain can plan multiple sequential Coding+Testing rounds within a single iteration before Review. Useful for large file changes or work with sequential dependencies. Each round gets its own Brain-crafted phase instruction. Workers receive the current round's instruction automatically via the `get_goal` tool response (`current_phase_instruction` field). `ValidatePlanStrict` enforces that each contiguous content block (Coding and/or DocWriting) is followed by Testing. Repeated phases are displayed correctly in Goal Detail with occurrence-aware handling (e.g., `coding-1`, `coding-2` have distinct buttons and timeline entries)
- **GoalDetail 3-panel layout** — Redesigned goal detail page with a top metadata strip, two-column body (left: description/notes/failure info; right: iterations), and bottom action strip. Iterations display as horizontal colour-coded tabs (green=completed, red=failed, blue=active) with full "Iteration N" labels. Iteration tabs and phase bar are sticky so only the content area scrolls. Live progress timeline shows chronological feed of progress reports and clarifications from all phases with smart auto-scroll that only fires when new entries arrive, allowing users to scroll up and read older content without being snapped back. Progress entries display role badge and status badge on the same line with cleaner formatting (no phase label or worker ID). Clicking a failed iteration tab automatically selects the failed phase to immediately show failure details. Release label appears inline in the metadata row aligned with status, priority, and scope badges
- **Progress reports phase/iteration aware** — Progress reports stored per-pipeline with phase name and iteration attribution. Dashboard displays progress history for all phases including completed ones, not just the active phase. Clarification entries also carry phase and iteration attribution in the timeline
- **Worker `get_goal` parameterless** — Workers call `get_goal` with no arguments to recover their current goal's description after context compaction. Goal ID is injected by the worker runtime, preventing accidental fetching of wrong goals
- **Worker resilient reconnect** — Workers use exponential-backoff retry loop when orchestrator is unavailable at startup, making the system resilient to container startup ordering
- **Goals page filter persistence** — Filter settings (status, priority, repository, release) persist across navigation and browser refreshes. Reset button clears all filters
- **Goals page sticky header** — Filter bar and table headers remain pinned while scrolling through goal rows
- **Worker utilization metrics** — `GET /health/utilization` endpoint provides per-role worker utilization and bottleneck detection
- **Self-improvement loop** — the improver modifies `agents.md` based on accumulated metrics
- **EF Core persistence** — All state is persisted in a single `copilothive.db` SQLite database via Entity Framework Core (`CopilotHiveDbContext`). `GoalStore` (goals, releases, iterations) and `PipelineStore` (pipelines, conversations, task mappings) both use `IDbContextFactory<CopilotHiveDbContext>`. Schema reconciliation at startup creates missing tables (`EnsureSchemaUpToDate`) and applies pending EF Core migrations (`Database.MigrateAsync()`). Pre-migration backups created via SQLite's online backup API. SQLite runs in **WAL mode** (Write-Ahead Logging) for concurrent read/write access.
- **Backup & Restore** — `BackupService` creates tar.gz archives containing the database, Brain session files (`brain-master.json`, `brain-goal-*.json`), Composer session (`composer-session.json`), metrics, and data protection keys. Backups are created on-demand via the Configuration page "Backup" tab or `POST /api/backup`, listed via `GET /api/backup`, and downloaded via `GET /api/backup/{filename}`. Restore via `POST /api/backup/restore` creates a safety backup before replacing files; orchestrator restart required after restore. Old backups pruned to last 10
- **Full In-App Configuration** — All `hive-config.yaml` settings are editable from the dashboard Configuration page. New tabs: Available Models (add/edit/remove models, browse provider models from GitHub Copilot and Ollama APIs), Repositories (add/edit/remove with auto-clone), Orchestrator settings (max_iterations, max_retries, max_parallel_goals, verbose_logging, brain_max_steps, branch_cleanup_delay_hours), Worker context windows (per-role), and Composer settings (max_steps). Separate reasoning effort dropdowns for every model assignment (orchestrator, workers, premium workers, composer, sub-agent models). Changes are written back to `hive-config.yaml` and hot-reloaded
- **GitHub OAuth Authentication** — When `GITHUB_OAUTH_CLIENT_ID` and `GITHUB_OAUTH_CLIENT_SECRET` environment variables are set, all dashboard pages and REST endpoints require authentication via GitHub OAuth. The first user to sign in becomes the admin (single-user model). The OAuth access token is stored in the database and used for Copilot API access, eliminating the need for `GH_TOKEN`. When OAuth env vars are not set, the system runs in "open mode" (no authentication, backward compatible). Login page with "Sign in with GitHub" button, user profile (avatar + username) in nav bar, logout button
- **Per-Assignment Reasoning Effort** — Reasoning effort (none/low/medium/high/extra_high) is now a separate config field on every model assignment (orchestrator, workers, premium workers, composer, sub-agent models) instead of being encoded as a `:suffix` in model names. The Configuration page has separate dropdowns for each assignment. The Composer chat has an editable reasoning dropdown that live-updates the running Composer. The Orchestrator dashboard shows reasoning per role and per LLM session. Model names are plain — no suffix encoding
- **Composer Session Compaction** — Manual "Compact" and "Compact 50%" buttons in the Composer chat. Full compaction summarizes the entire session (except recent messages). Partial compaction (50%) summarizes only the oldest 50% of tokens, keeping the newest 50% verbatim — gentler, preserves more recent context. Uses SharpCoder 0.16.0's `CompactOldestPercentAsync`
- **Goal Progress Narratives** — Workers write reflective narratives (what they tried, what worked, what they struggled with) via a `report_narrative` tool call. These are appended to a living progress document in the knowledge graph, alongside the Brain's iteration plan and summary. The progress document is linked to the goal and visible on the goal detail page. The Composer can read it to answer questions about goal execution. The Brain reads it before planning new iterations for richer context
- **Pre-Execution Goal Review** — An optional review process where a capable model verifies goal descriptions before dispatch. The reviewer checks file references, code references, scope, acceptance criteria, and dependencies against the actual codebase. Produces a verdict and review document in the knowledge graph. The Composer can trigger reviews and automatically refine goals based on feedback
- **LLM Session Dashboard** — A unified view of all active LLM sessions in the orchestrator container on the Orchestrator dashboard page. Shows Brain master, Brain per-goal, Composer, and Goal Review sessions with model, context usage, status, and last activity
- **Release Automation** — When a release is marked as Released, CopilotHive can automatically merge the working branch (e.g. `develop`) to a configurable target branch (e.g. `main`) and create a version tag, triggering CI. Per-repository configuration controls which branch to merge to and which branch to tag. Pre-release validation ensures all goals are completed and repositories are configured before execution. Rollback support deletes created tags on failure (merges are not reverted)
- **Repository Branch Dropdowns** — Repository configuration uses dropdowns populated from actual remote branches instead of manual text entry, making it easier to configure release automation correctly
- **Per-goal Brain contexts** — The Brain maintains independent agent sessions and LLM clients per goal via `GoalBrainActor` child actors with channel-based mailboxes — no per-goal execution locks, no `AsyncLocal`, no lease protocol. The `Actor<TMessage>` base class with `System.Threading.Channels` serializes all state access through a single-reader mailbox.
- **Actor architecture** — `Actor<TMessage>` base class using `System.Threading.Channels` as a single-reader mailbox, with `GoalActor`, `BrainActor`, and `GoalBrainActor` prototypes. Sequential message processing eliminates explicit synchronization (locks, CAS loops, `AsyncLocal`, tombstones) from mailbox-owned message-processing state — the mailbox serializes access. A single `_lifecycleLock` handles Start/Dispose mutual exclusion. `BrainActor` manages the master session, pipeline registry, and `GoalBrainActor` children. `GoalBrainActor` children handle per-goal LLM execution with tools that capture `this` directly.
- **Sub-Agents** — The Composer, Brain, and all worker roles (coder, tester, reviewer, docwriter, improver) can delegate codebase exploration, verification sweeps, and large-text summarization to background sub-sessions via SharpCoder's `start_sub_agent` tool. Only summaries return to the calling session, keeping its context clean. Sub-sessions run read-only by default and respect per-role capability ceilings (e.g., reviewer read-only, improver no-bash). A curated, describable `sub_agent_models` catalog (dashboard-editable, falls back to `available_models`) controls which models are offered
- **Clean Code principles** — Goal reviewer checks goal complexity, size, simpler alternatives, and root-cause patching; the Brain prompt enforces small functions, SRP, DRY, and YAGNI; the Composer splits large goals and prefers deleting code over adding.
- **Iteration extension** — Goals that exhaust their iteration budget can be resumed with additional iterations via the dashboard, API, or Composer tool.
- **Composer log inspection** — The Composer can now read recent application logs directly, enabling self-guided debugging without manual log pasting.
- **`get_current_time` Tool** — The Brain and Composer can query the current UTC date and time on demand via a `get_current_time` tool call, eliminating the need for the human to provide the date for changelog entries and release notes
- **Reject-not-fix plan validation** — The pipeline now REJECTS invalid iteration plans instead of silently auto-fixing or substituting a default. An actionable rejection reason is fed back to the Brain for a bounded replan (3 attempts); exhausted budgets fail the goal. The enforced block-based grammar (R1-R7) requires each content block (Coding and/or DocWriting) to be followed by Testing, exactly one Review after all content blocks, optional Improve before Merging, and Merging as the final phase.
- **Sub-agent vision support** — An informational `SupportsVision` flag on model entries flows end-to-end through the sub-agent catalog, helping the Brain and Composer select vision-capable models for image-based sub-tasks.
- **Composer Background-tasks panel** — Live sub-agent progress (Running → terminal transitions) is displayed in the Composer chat, driven by SharpCoder's `SubAgentChanged` event. The result area is scrollable with Markdown rendering, and the panel defaults to collapsed on page load.
- **Composer-chat image/PDF attachments** — Attach images (PNG/JPG/JPEG/GIF/WEBP) and PDFs to Composer messages via a compact inline file picker or clipboard paste. Attachments are streamed to vision-capable sub-agents via `start_sub_agent image_paths` for visual analysis, with only the textual summary returning to the chat session.
- **Releases page redesign** — Each repository's releases display on a single horizontally-scrollable row. Older released releases collapse to a default window (Planning releases always visible), with a compact "Show older releases" toggle button to expand them.
- **Conditional Improve in planner** — The Brain's planning prompt now recommends the Improve phase only for retries or iterations with prior issues (review/test failures, rejections), instead of unconditionally for every iteration.
- **Composer tooling enhancements** — `get_release` tool for inspecting release membership, enriched `get_goal` output (Release/Depends On/document-links), and release-filtered `list_goals`/`search_goals` with labeled output.
- **Goals REST API** — `GET/POST/PATCH/DELETE /api/goals`, `GET /api/goals/{id}`, `GET /api/goals/search?q=…&status=…`, `POST /api/goals/{id}/cancel`
- **Releases REST API** — `GET/POST/PATCH/DELETE /api/releases`, `GET /api/releases/{id}`; release statuses follow the lifecycle **Planning → In Progress → Released**; goals can be assigned to a release via the API or the Composer
- **Dashboard** — Blazor Server UI with **shared header bar**, **sticky nav/footer**, and **emoji icons** on nav items; goals browser (filterable/searchable by status, priority, repository, and **release filter with Planning release support**), goal detail with iteration timeline and dependency visualization, **Releases page** (list all releases with status and goal count; detail page showing assigned goals and progress), release assignment visible on the Goals page, worker status (including actual model being used per task with premium tier display), orchestrator view (Brain + Composer stats, with Reset Brain Session button), live logs, and configuration; The dashboard uses event-driven notifications via `DashboardNotifier` for immediate refresh on state changes, with a 10-second timer for time-based displays. configuration page displays `hive-config.yaml` with YAML syntax highlighting (keys, comments, booleans, numbers); nav bar and footer display the running **CopilotHive version** sourced from assembly metadata; responsive sidebar navigation (collapses to icon-only at ≤768px); 🔔 clarification bell icon in the global header opens a slide-out drawer when workers request human clarification (visible from any page); Workers page shows real-time context usage % per worker (updated every 30 seconds via heartbeat)
- **Config repo** — externalized agent instructions (`CopilotHive-Config`)
- **Multi-repo goal support** — goals can target any accessible Git repository
- **Per-role model selection** — assign different LLM models to each worker type
- **Squash merge** — feature branches are squash-merged into the base branch, producing a single descriptive commit per goal
- **Auto-rebase on merge conflicts** — the pipeline automatically retries merges
- **Fallback metrics parsing** — robust parsing handles varied worker output formats
- **Duplicate goal completion guards** — prevents re-processing of already-completed goals
- **Telemetry** — per-run metrics aggregated and fed into the improver
- **Context and token logging** — Brain and workers log exact token counts (`LastKnownContextTokens`) from API responses when available, with heuristic fallback before the first call, for cost tracking and context management; Brain logs context usage percentage after each call (e.g., "Brain context usage: 45.2% (58000/128000 tokens) after PlanIterationAsync")
- **Rich worker logging** — tool calls logged as `tool:name(arg="value")`, results as `result:id → "preview"`, with role, model, and elapsed time per task
- **Dirty-worktree safety net** — automatically re-prompts Copilot if uncommitted changes remain after task execution
- **Brain retry mechanism** — automatic retries on LLM timeout or transient failures (up to 2 retries with 5-second backoff)
- **Non-blocking improve phase** — improver failures don't prevent goal completion; recorded in goal notes and metrics
- **Three-dot diff comparison** — accurate detection of all changes on feature branches using `origin/{baseBranch}...HEAD`
- **HTTP resilience** — all LLM API calls use `Microsoft.Extensions.Http.Resilience` with 3 retries, exponential backoff, and 2-minute per-attempt timeout
- **Worker feedback in Brain context** — worker output (verdicts, test metrics, issues) is injected into the Brain conversation for informed replanning after failures
- **Automatic branch cleanup** — when a Failed goal is deleted, its remote feature branches are automatically cleaned up from all associated repositories; best-effort (logs warning on failure, doesn't prevent goal deletion). `DispatcherMaintenance` periodically deletes `copilothive/{goal-id}` feature branches from target repositories after a configurable delay once the goal is completed and merged.
- **Goal notes** — non-fatal observations tracked in the goal database (e.g. "Improver skipped: timeout")
- **Iteration summaries** — structured per-iteration metrics (phases, test counts, review verdicts) recorded in the goal database for observability without reading logs; build success state now correctly persists after goal completion
- **Phase duration logging** — each pipeline phase logs its wall-clock duration in seconds when it completes (e.g., "Phase Testing for goal X completed in 45.2s")
- **Goal dependency visualization** — the dashboard displays dependency relationships: 🔗 icon for unblocked goals (all dependencies completed), ⏳ icon for blocked goals (dependencies pending); the goal detail page lists dependencies as clickable links with status indicators
- **Visible Planning phase in iteration timeline** — the Goal Detail page shows the Brain's planning phase as a distinct phase box (active when planning, completed once plan is determined), with the plan's reasoning displayed below the phase bar for transparency
- **Inline prompt display** — Brain prompts and worker prompts are shown inline within each phase on the Goal Detail page, using tagged `ConversationEntry` metadata; prompts appear as collapsible sections (Brain Prompt muted, Worker Prompt with role name) above Worker Output, with Planning Prompt/Response shown for the planning phase
- **Conversation entry metadata** — `ConversationEntry` tracks iteration number and purpose for each conversation entry (planning, craft-prompt, worker-output, error), enabling analysis of conversation history by iteration without heuristic parsing
- **Release Management** — releases are first-class entities with a full CRUD REST API (`/api/releases`) and SQLite persistence (`GoalStore`). Goals can be assigned to a release; the release tracks status (**Planning → In Progress → Released**) and aggregates goal counts per status. The Composer exposes `create_release` and `list_releases` tools for conversational release planning. The Dashboard includes a **Releases page** listing all releases with status badges and goal counts, plus a detail page per release showing assigned goals and progress. The release detail page includes an "Unrelease" button that reverts a Released release back to Planning, allowing edits and goal reassignment.
- **Worker Session Persistence** — SharpCoder `AgentSession` objects are persisted per-role on the orchestrator via two new gRPC RPCs (`GetSession` / `SaveSession`). Sessions are stored in SQLite as part of `GoalPipeline`, allowing workers to resume from the same session context across orchestrator restarts without losing conversation history.
- **GoalScope** — goals carry a `scope` field (`Patch` / `Feature` / `Breaking`) that communicates the intended impact to workers. The reviewer enforces scope-appropriate rules (e.g., `Breaking` changes require migration notes and changelog entries). Scope is exposed in all Composer tools that return or accept goal data, and displayed as a badge on the Goals page and goal detail page in the Dashboard.
- **Conditional Doc Writing** — the Brain inspects the goal description and decides at planning time whether the DocWriting phase is needed. Goals that don't affect user-facing behaviour or public APIs skip the phase entirely, reducing pipeline duration. The decision is recorded in iteration metadata and visible on the Goal Detail page.
- **Three-tier clarification system** — Workers call `request_clarification` when goals are ambiguous. Questions resolve through a three-tier chain: Brain auto-answer → Composer LLM auto-answer (using a forked session) → human via Composer chat. Clarification exchanges are logged on the goal detail page with answerer attribution (brain/composer/human). Aggregated stats shown on Orchestrator dashboard. **Planning-phase Brain escalations** are visible in Goal Detail; clarification events render as structured cards without duplicate raw timeline entries; role badges display correctly for Planning/Brain and Improve/Improver phases
- **Hardcoded worker safety prompts** — Mandatory safety rules (git push prohibition, role identity, tool call contracts, scope boundaries) are hardcoded per worker role in `SharpCoderRunner.BuildRoleSystemPrompt()`. AGENTS.md content is appended as supplementary heuristics after a `# Learned Heuristics` separator, ensuring the improver cannot weaken critical rules.
- **Docs-only iteration plans** — The Brain can plan documentation-only iterations (e.g. `[DocWriting, Testing, Review, Merging]`) without a forced Coding phase. `ValidatePlanStrict` enforces that every content block — whether Coding, DocWriting, or both — is followed by Testing. `PipelineStateMachine` accepts DocWriting as a valid first phase, and `GoalDispatcher` dispatches the plan's actual first phase instead of hardcoding Coder.
- **Mandatory Testing and Review** — `ValidatePlanStrict` enforces that every iteration plan includes Testing after each content block and exactly one Review. Invalid plans are REJECTED with an actionable reason — the Brain must resubmit a valid plan within a bounded number of attempts. There is no auto-fix or default-plan fallback.
- **Plan rejection and replan** — When the Brain submits an invalid iteration plan, the pipeline REJECTS it with a reason describing which grammar rule was violated. The Brain receives the rejection reason and must resubmit a valid plan within a bounded number of attempts (3). If no valid plan is produced, the goal fails. There is no auto-adjustment, phase insertion, or default-plan substitution.
- **Reviewer iteration context** — Reviewers receive current iteration test results and an iteration-scoped diff command, reviewing only the current iteration's changes instead of the cumulative branch diff. Reviewers can call `get_test_report()` to actively retrieve the tester's structured report (build success, test counts, verdict), preventing spurious rejections when reviewers cannot verify build/test results
- **Composer config repo tools** — Five new tools (`list_config_files`, `read_config_file`, `update_agents_md`, `edit_agents_md`, `commit_config_changes`) give the Composer direct access to inspect and update AGENTS.md files in the config repository.
- **Reviewer receives coder output** — The reviewer's prompt now includes the coder's output from the current iteration, giving the reviewer context about what was implemented before inspecting the diff
- **Prompt injection protection** — All prompt construction now wraps injected content in fenced delimiters to prevent prompt injection attacks and improve LLM parsing
- **Acceptance criteria verification** — Reviewer and Tester hardcoded system prompts include mandatory acceptance criteria verification blocks, instructing them to always read the full goal description and verify every criterion is met — not just that tests pass
- **Worker report as authoritative output** — Workers' `report_*` tool call summaries are now used as the canonical output across the pipeline (stored in `PhaseOutputs`, shown in the dashboard). Previously, narrative text responses were used, which were less structured
- **Eager Repo Cloning** — at startup, the Brain clones all repositories declared in the config into the local Brain repo store (`{stateDir}/repos/{repoName}`). This ensures file-access tools are immediately available for the first goal without waiting for an on-demand clone, eliminating the cold-start latency that affected the first iteration.
- **Worker crashes fail goal immediately** — When a worker task returns `TaskOutcome.Failed` (infrastructure failure, unhandled exception), the goal is immediately marked as failed rather than silently retrying or hanging
- **Missing worker report treated as failure** — When a worker completes its session without calling the mandatory report tool, the phase is treated as a failure rather than a silent pass
- **Pipeline store cleanup on goal reset** — When a failed goal is reset to Draft, any out-of-memory pipeline state is properly cleaned up from the pipeline store, preventing stale state from interfering with the next dispatch
- **Composer model dropdown contrast** — Fixed the model selector dropdown in the Composer chat UI being illegible in dark theme due to insufficient contrast between text and background colours
- **Dashboard elapsed time display** — Fixed the elapsed time display in the Active Goals table to freeze at the final value when goals complete, rather than continuing to increment
- **Iteration failure color fix** — Iteration tabs and phase indicators now correctly show red/failed status when a reviewer requests changes or tests fail, instead of always displaying green
- **Version Display** — the CopilotHive version (read from assembly `InformationalVersion` metadata) is shown in the Dashboard navigation bar and footer. This makes it easy to verify which build is running without inspecting logs or binary metadata.
- **Issue Tracking** — Workers and the Brain can raise issues during goal execution via a `raise_issue` tool. The Composer manages issues conversationally through `create_issue`, `list_issues`, `get_issue`, and `update_issue` tools. Issues are structured records (type: bug/code_quality/suggestion/concern/workflow, severity: low/medium/high) with a triage lifecycle (open → triaged → acknowledged → in_progress → resolved → closed), stored in the database via EF Core. REST API at `/api/issues` (GET/POST/PATCH/DELETE) with filtering. Dashboard page at `/issues` with filterable table, triage dropdowns, and inline create form. Issues can be linked to goals with tri-state semantics (set/clear/unchanged). Goal Detail pages show back-links to related issues (via source goal or linked goal). The Issues page hides closed issues by default.
- **CI Monitoring** — After each goal merge, CopilotHive optionally monitors CI status via the GitHub check-runs API. On CI success, a `CiSucceeded` event flows to the Composer event bus. On failure, issues are automatically created from test failures with a `CiFailed` event. Configurable per repository (`monitor_ci`, `ci_timeout_minutes`) via `hive-config.yaml` or the Configuration page. Includes startup scan for restart recovery.
- **Composer Event Bus** — System events (goal completed/failed/dispatched, issue raised/resolved, release completed) are broadcast on a typed event bus and buffered for the Composer. When the user sends their next chat message, pending events are prepended as a context block so the Composer is automatically up to date. Events are shown as a muted "System" message in the Composer chat UI. On restart, the `EventBusStartupScanner` reconstructs the buffer by querying recent state changes since the last Composer session activity (with a 60-minute fallback), so events are not lost across restarts.
- **Composer actor architecture** — The Composer's streaming and session lifecycle operations are managed by a `ComposerActor` using the actor mailbox pattern. State mutations are serialized through the mailbox without explicit locking, enabling future multi-Composer (multi-user) support.
- **Active event notifications** — Significant events are automatically injected as `[System Notification]` messages for the Composer to act on autonomously. Notifications are configurable via the Composer chat toggle and the Configuration page, including active events and throttling settings in `composer.event_notifications`.
- **CI monitor log fetching** — CI failure issues now contain full test details — including the test name, error message, and stack trace — fetched from GitHub Actions logs.
- **Reasoning effort mapping** — `ExtraHigh` reasoning maps correctly per provider: `xhigh` for Copilot, `max` for Ollama, and clamped to `high` for GitHub Models.
- **Stale worker fix** — Workers are no longer reclaimed based on wall-clock task duration; stale-worker detection uses task-specific activity instead.

## Contributing

See [agents/README.md](agents/README.md) for agent role definitions, behavioral guidelines, and contribution instructions.
