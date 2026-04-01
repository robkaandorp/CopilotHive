[![CI](https://github.com/robkaandorp/CopilotHive/actions/workflows/ci.yml/badge.svg)](https://github.com/robkaandorp/CopilotHive/actions/workflows/ci.yml)

# CopilotHive

CopilotHive is a **self-improving multi-agent orchestration system** powered by **[SharpCoder](https://github.com/robkaandorp/SharpCoder)** (an autonomous coding agent library). A pool of generic worker agents collaborate autonomously inside Docker containers — dynamically taking on roles (coder, tester, doc-writer, reviewer, improver) per task — to implement software goals without human intervention. A conversational **Composer** agent helps decompose high-level intent into actionable goals through a streaming chat interface.

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

**Coding → Testing → (Doc Writing) → Review → Merge → Improve**

1. **Coding**: A worker (assigned the coder role) implements the goal on a feature branch.
2. **Testing**: A worker (assigned the tester role) builds the project and runs all tests.
3. **Doc Writing** *(conditional)*: A worker (assigned the doc-writer role) updates documentation to reflect the changes. This phase is included only when the goal requires documentation changes; the Brain conditionally skips it for goals that don't affect user-facing behaviour or public APIs.
4. **Review**: A worker (assigned the reviewer role) inspects the diff, tests, and documentation.
5. **Merge**: The Brain decides when quality is sufficient and squash-merges the branch, combining all feature branch commits into a single descriptive commit.
6. **Improve** *(non-blocking)*: A worker (assigned the improver role) updates `agents.md` based on metrics. If improvement fails, the pipeline still completes — the failure is recorded in goal notes and metrics.

If testing or review fails, the pipeline retries the coding step (up to a configured limit).

The **Brain** (`DistributedBrain`) plans iteration phases and crafts worker prompts using SharpCoder's `CodingAgent` for LLM communication. The Brain maintains a **single persistent session** across all goals with automatic context compaction (infinite context), and has **read-only file access** to target repositories for informed decision-making. Goals are processed **sequentially** so the Brain accumulates learnings from one goal to the next. Workers report structured verdicts via tool calls, and the pipeline state machine (`PipelineStateMachine`) drives sequencing — retrying, advancing, or failing based on those verdicts. Pipeline state is persisted to **SQLite** (`PipelineStore`) with auto-migration, and the Brain session is persisted to `brain-session.json`, so the server can resume after restarts. Metrics feed into the **improver** for self-improvement: the system tunes its own `agents.md` instructions over time.

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
     --goals-file=goals.yaml \
     --config-repo=https://github.com/your-org/CopilotHive-Config \
     --config-repo-path=./config-repo
   ```

   This starts a **gRPC server** on port 9000 and an **HTTP health endpoint** on port 9001.

   Set `BRAIN_CONTEXT_WINDOW` to configure the Brain's maximum context window in tokens (default: 100,000). Different models have different limits (e.g. Claude 200k, GPT-4 128k).

### Configuring Goals

Goals are stored in **SQLite** (`goals.db`) as the primary source of truth. The recommended way to create goals is through the **Composer Chat UI** at `/composer`, which provides a conversational interface for decomposing high-level intent into well-scoped goals. Goals can also be created via the REST API (`POST /api/goals`) or imported from `goals.yaml` on first startup.

The `goals.yaml` format (used for initial bootstrap):

```yaml
goals:
  - id: my-feature
    description: "Implement feature X in repository Y"
    repo: https://github.com/your-org/target-repo
    models:
      coder: gpt-4o
      reviewer: gpt-4o-mini
      tester: gpt-4o-mini
      improver: gpt-4o
```

**Goal ID Format**: Goal IDs must be non-empty, lowercase kebab-case identifiers containing only letters (a–z), digits (0–9), and hyphens (–). IDs must not start or end with a hyphen (e.g., `fix-build-error`, `add-feature`, `release-v1-0`). This format mirrors git branch naming conventions (e.g., `copilothive/{goal-id}`). Invalid goal IDs will throw an `ArgumentException` with a descriptive error message.

When a goal completes (success or failure), the Orchestrator updates the goal entry with metadata including `total_duration_seconds` — total wall-clock duration from start to completion — `phase_durations` — a map of phase names to wall-clock durations in seconds — and `iteration_summaries` — an array of structured summaries (one per iteration) capturing phases, test results, and review verdicts:

```yaml
goals:
  - id: my-feature
    # ... (input fields above)
    started_at: "2026-03-17T19:38:37Z"
    completed_at: "2026-03-17T19:42:15Z"
    total_duration_seconds: 218
    iterations: 2
    status: completed
    phase_durations:
      Coding: 125.5
      Testing: 85.3
      DocWriting: 45.2
      Review: 60.1
      Merge: 12.5
    iteration_summaries:
      - iteration: 1
        phases:
          - name: Coding
            result: pass
            duration_seconds: 60.0
            worker_output: "Coder: Implemented feature X."
          - name: Testing
            result: fail
            duration_seconds: 20.5
            worker_output: "Tester: 2 tests failed."
        test_counts:
          total: 10
          passed: 8
          failed: 2
        review_verdict: null
        notes: []
        phase_outputs:
          coder-1: "Coder: Implemented feature X."
          tester-1: "Tester: 2 tests failed."
      - iteration: 2
        phases:
          - name: Coding
            result: pass
            duration_seconds: 65.5
          - name: Testing
            result: pass
            duration_seconds: 65.3
          - name: DocWriting
            result: pass
            duration_seconds: 45.2
          - name: Review
            result: pass
            duration_seconds: 60.1
          - name: Merge
            result: pass
            duration_seconds: 12.5
        test_counts:
          total: 10
          passed: 10
          failed: 0
        review_verdict: approve
        notes: []
```

## Project Structure

| Directory | Description |
|-----------|-------------|
| `src/CopilotHive/` | Main orchestrator — Brain, GoalDispatcher, persistence, metrics |
| `src/CopilotHive.Shared/` | Shared protobuf definitions and DTOs |
| `src/CopilotHive.Worker/` | Worker process (runs inside Docker containers) |
| `tests/` | 1337+ xUnit tests |
| `agents/` | Default agent templates (overridden by config repo at runtime) |
| `docker/` | Dockerfiles and container configuration |

## Current Features

- **Server-only mode** — gRPC server + HTTP health endpoint (no CLI mode)
- **LLM-powered Brain** — `DistributedBrain` uses SharpCoder's `CodingAgent` with a single persistent session, read-only file access to repos, automatic context compaction, and configurable context window (`BRAIN_CONTEXT_WINDOW`)
- **Composer** — conversational agent for goal decomposition and management with streaming chat UI (`/composer`); uses a persistent SharpCoder session with 8 goal-management tools (`create_goal`, `approve_goal`, `update_goal`, `delete_goal`, `cancel_goal`, `get_goal`, `list_goals`, `search_goals`), 3 release-management tools (`create_release`, `list_releases`, `edit_planning_releases`), codebase tools (`read_file`, `glob`, `grep`), 5 git tools (`git_log`, `git_diff`, `git_show`, `git_branch`, `git_blame`), 2 web research tools (`web_search`, `web_fetch`) — available only when `OLLAMA_API_KEY` is set, phase output inspection (`get_phase_output` with `content` parameter to retrieve specific output text), repository listing (`list_repositories`) — queries live config to show all configured repositories, interactive user questions (`ask_user`) — supports Yes/No, SingleChoice, and MultiChoice questions with optional feedback, and **5 config repo tools** (`list_config_files`, `read_config_file`, `update_agents_md`, `edit_agents_md`, `commit_config_changes`) for direct AGENTS.md inspection and updates; **clarification auto-answer capability** — can auto-answer Brain clarification questions using a forked LLM session; full Markdown rendering (Markdig) and chat history persistence across page navigations; **automatic context overflow recovery** — detects `model_max_prompt_tokens_exceeded` errors and auto-resets the session to recover
- **Sequential goal processing** — goals process one at a time so the Brain accumulates context across goals
- **Worker utilization metrics** — `GET /health/utilization` endpoint provides per-role worker utilization and bottleneck detection
- **Self-improvement loop** — the improver modifies `agents.md` based on accumulated metrics
- **SQLite persistence** — `PipelineStore` with auto-migration for pipeline state; `SqliteGoalStore` as the primary source of truth for goals with full CRUD, search, and iteration history; `SqliteReleaseStore` for release entities with full CRUD
- **Goals REST API** — `GET/POST/PATCH/DELETE /api/goals`, `GET /api/goals/{id}`, `GET /api/goals/search?q=…&status=…`, `POST /api/goals/{id}/cancel`
- **Releases REST API** — `GET/POST/PATCH/DELETE /api/releases`, `GET /api/releases/{id}`; release statuses follow the lifecycle **Planning → In Progress → Released**; goals can be assigned to a release via the API or the Composer
- **Dashboard** — Blazor Server UI with **shared header bar**, **sticky nav/footer**, and **emoji icons** on nav items; goals browser (filterable/searchable by status, priority, repository, and **release filter with Planning release support**), goal detail with iteration timeline and dependency visualization, **Releases page** (list all releases with status and goal count; detail page showing assigned goals and progress), release assignment visible on the Goals page, worker status (including actual model being used per task with premium tier display), orchestrator view (Brain + Composer stats, with Reset Brain Session button), live logs, and configuration; configuration page displays `hive-config.yaml` with YAML syntax highlighting (keys, comments, booleans, numbers); nav bar and footer display the running **CopilotHive version** sourced from assembly metadata
- **Config repo** — externalized agent instructions and goals (`CopilotHive-Config`)
- **Multi-repo goal support** — goals can target any accessible Git repository
- **Per-role model selection** — assign different LLM models to each worker type
- **Squash merge** — feature branches are squash-merged into the base branch, producing a single descriptive commit per goal
- **Auto-rebase on merge conflicts** — the pipeline automatically retries merges
- **Fallback metrics parsing** — robust parsing handles varied worker output formats
- **Duplicate goal completion guards** — prevents re-processing of already-completed goals
- **Telemetry** — per-run metrics aggregated and fed into the improver
- **Context and token logging** — Brain and workers log estimated/actual token usage per LLM call for cost tracking and context management; Brain logs context usage percentage after each call (e.g., "Brain context usage: 45.2% (58000/128000 tokens) after PlanIterationAsync")
- **Rich worker logging** — tool calls logged as `tool:name(arg="value")`, results as `result:id → "preview"`, with role, model, and elapsed time per task
- **Dirty-worktree safety net** — automatically re-prompts Copilot if uncommitted changes remain after task execution
- **Brain retry mechanism** — automatic retries on LLM timeout or transient failures (up to 2 retries with 5-second backoff)
- **Non-blocking improve phase** — improver failures don't prevent goal completion; recorded in goal notes and metrics
- **Three-dot diff comparison** — accurate detection of all changes on feature branches using `origin/{baseBranch}...HEAD`
- **HTTP resilience** — all LLM API calls use `Microsoft.Extensions.Http.Resilience` with 3 retries, exponential backoff, and 2-minute per-attempt timeout
- **Worker feedback in Brain context** — worker output (verdicts, test metrics, issues) is injected into the Brain conversation for informed replanning after failures
- **Automatic branch cleanup** — when a Failed goal is deleted, its remote feature branches are automatically cleaned up from all associated repositories; best-effort (logs warning on failure, doesn't prevent goal deletion)
- **Goal notes** — non-fatal observations tracked in goals.yaml (e.g. "Improver skipped: timeout")
- **Iteration summaries** — structured per-iteration metrics (phases, test counts, review verdicts) recorded in goals.yaml for observability without reading logs
- **Phase duration logging** — each pipeline phase logs its wall-clock duration in seconds when it completes (e.g., "Phase Testing for goal X completed in 45.2s")
- **Goal dependency visualization** — the dashboard displays dependency relationships: 🔗 icon for unblocked goals (all dependencies completed), ⏳ icon for blocked goals (dependencies pending); the goal detail page lists dependencies as clickable links with status indicators
- **Visible Planning phase in iteration timeline** — the Goal Detail page shows the Brain's planning phase as a distinct phase box (active when planning, completed once plan is determined), with the plan's reasoning displayed below the phase bar for transparency
- **Inline prompt display** — Brain prompts and worker prompts are shown inline within each phase on the Goal Detail page, using tagged `ConversationEntry` metadata; prompts appear as collapsible sections (Brain Prompt muted, Worker Prompt with role name) above Worker Output, with Planning Prompt/Response shown for the planning phase
- **Conversation entry metadata** — `ConversationEntry` tracks iteration number and purpose for each conversation entry (planning, craft-prompt, worker-output, error), enabling analysis of conversation history by iteration without heuristic parsing
- **Release Management** — releases are first-class entities with a full CRUD REST API (`/api/releases`) and SQLite persistence (`SqliteReleaseStore`). Goals can be assigned to a release; the release tracks status (**Planning → In Progress → Released**) and aggregates goal counts per status. The Composer exposes `create_release` and `list_releases` tools for conversational release planning. The Dashboard includes a **Releases page** listing all releases with status badges and goal counts, plus a detail page per release showing assigned goals and progress.
- **Worker Session Persistence** — SharpCoder `AgentSession` objects are persisted per-role on the orchestrator via two new gRPC RPCs (`GetSession` / `SaveSession`). Sessions are stored in SQLite as part of `GoalPipeline`, allowing workers to resume from the same session context across orchestrator restarts without losing conversation history.
- **GoalScope** — goals carry a `scope` field (`Patch` / `Feature` / `Breaking`) that communicates the intended impact to workers. The reviewer enforces scope-appropriate rules (e.g., `Breaking` changes require migration notes and changelog entries). Scope is exposed in all Composer tools that return or accept goal data, and displayed as a badge on the Goals page and goal detail page in the Dashboard.
- **Conditional Doc Writing** — the Brain inspects the goal description and decides at planning time whether the DocWriting phase is needed. Goals that don't affect user-facing behaviour or public APIs skip the phase entirely, reducing pipeline duration. The decision is recorded in iteration metadata and visible on the Goal Detail page.
- **Three-tier clarification system** — Workers call `request_clarification` when goals are ambiguous. Questions resolve through a three-tier chain: Brain auto-answer → Composer LLM auto-answer (using a forked session) → human via Composer chat. Clarification exchanges are logged on the goal detail page with answerer attribution (brain/composer/human). Aggregated stats shown on Orchestrator dashboard.
- **Hardcoded worker safety prompts** — Mandatory safety rules (git push prohibition, role identity, tool call contracts, scope boundaries) are hardcoded per worker role in `SharpCoderRunner.BuildRoleSystemPrompt()`. AGENTS.md content is appended as supplementary heuristics after a `# Learned Heuristics` separator, ensuring the improver cannot weaken critical rules.
- **Docs-only iteration plans** — The Brain can plan documentation-only iterations (e.g. `[DocWriting, Review, Merging]`) without a forced Coding phase. `ValidatePlan` now accepts plans containing DocWriting as a valid alternative to Coding.
- **Reviewer iteration context** — Reviewers receive current iteration test results and an iteration-scoped diff command, reviewing only the current iteration's changes instead of the cumulative branch diff.
- **Composer config repo tools** — Five new tools (`list_config_files`, `read_config_file`, `update_agents_md`, `edit_agents_md`, `commit_config_changes`) give the Composer direct access to inspect and update AGENTS.md files in the config repository.
- **Eager Repo Cloning** — at startup, the Brain clones all repositories declared in the config into the local Brain repo store (`{stateDir}/repos/{repoName}`). This ensures file-access tools are immediately available for the first goal without waiting for an on-demand clone, eliminating the cold-start latency that affected the first iteration.
- **Version Display** — the CopilotHive version (read from assembly `InformationalVersion` metadata) is shown in the Dashboard navigation bar and footer. This makes it easy to verify which build is running without inspecting logs or binary metadata.

## Contributing

See [agents/README.md](agents/README.md) for agent role definitions, behavioral guidelines, and contribution instructions.
