[![Build](https://github.com/robkaandorp/CopilotHive/actions/workflows/build.yml/badge.svg)](https://github.com/robkaandorp/CopilotHive/actions/workflows/build.yml)

# CopilotHive

CopilotHive is a **self-improving multi-agent orchestration system** powered by **SharpCoder** (an autonomous coding agent library). A pool of generic worker agents collaborate autonomously inside Docker containers — dynamically taking on roles (coder, tester, doc-writer, reviewer, improver) per task — to implement software goals without human intervention. A conversational **Composer** agent helps decompose high-level intent into actionable goals through a streaming chat interface.

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

**Coding → Testing → Doc Writing → Review → Merge → Improve**

1. **Coding**: A worker (assigned the coder role) implements the goal on a feature branch.
2. **Testing**: A worker (assigned the tester role) builds the project and runs all tests.
3. **Doc Writing**: A worker (assigned the doc-writer role) updates documentation to reflect the changes.
4. **Review**: A worker (assigned the reviewer role) inspects the diff, tests, and documentation.
5. **Merge**: The Brain decides when quality is sufficient and merges the branch.
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
| `tests/` | 740+ xUnit tests |
| `agents/` | Default agent templates (overridden by config repo at runtime) |
| `docker/` | Dockerfiles and container configuration |

## Current Features

- **Server-only mode** — gRPC server + HTTP health endpoint (no CLI mode)
- **LLM-powered Brain** — `DistributedBrain` uses SharpCoder's `CodingAgent` with a single persistent session, read-only file access to repos, automatic context compaction, and configurable context window (`BRAIN_CONTEXT_WINDOW`)
- **Composer** — conversational agent for goal decomposition and management with streaming chat UI (`/composer`); uses a persistent SharpCoder session with 7 LLM-callable tools (`create_goal`, `approve_goal`, `update_goal`, `delete_goal`, `get_goal`, `list_goals`, `search_goals`) plus codebase tools (`read_file`, `glob`, `grep`); full Markdown rendering (Markdig) and chat history persistence across page navigations
- **Sequential goal processing** — goals process one at a time so the Brain accumulates context across goals
- **Worker utilization metrics** — `GET /health/utilization` endpoint provides per-role worker utilization and bottleneck detection
- **Self-improvement loop** — the improver modifies `agents.md` based on accumulated metrics
- **SQLite persistence** — `PipelineStore` with auto-migration for pipeline state; `SqliteGoalStore` as the primary source of truth for goals with full CRUD, search, and iteration history
- **Goals REST API** — `GET/POST/PATCH/DELETE /api/goals`, `GET /api/goals/{id}`, `GET /api/goals/search?q=…&status=…`
- **Dashboard** — Blazor Server UI with goals browser (filterable/searchable), goal detail with iteration timeline and dependency visualization, worker status, orchestrator view (Brain + Composer stats, with Reset Brain Session button), live logs, and configuration; configuration page displays `hive-config.yaml` with YAML syntax highlighting (keys, comments, booleans, numbers)
- **Config repo** — externalized agent instructions and goals (`CopilotHive-Config`)
- **Multi-repo goal support** — goals can target any accessible Git repository
- **Per-role model selection** — assign different LLM models to each worker type
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
- **Goal notes** — non-fatal observations tracked in goals.yaml (e.g. "Improver skipped: timeout")
- **Iteration summaries** — structured per-iteration metrics (phases, test counts, review verdicts) recorded in goals.yaml for observability without reading logs
- **Phase duration logging** — each pipeline phase logs its wall-clock duration in seconds when it completes (e.g., "Phase Testing for goal X completed in 45.2s")
- **Goal dependency visualization** — the dashboard displays dependency relationships: 🔗 icon for unblocked goals (all dependencies completed), ⏳ icon for blocked goals (dependencies pending); the goal detail page lists dependencies as clickable links with status indicators

## Contributing

See [agents/README.md](agents/README.md) for agent role definitions, behavioral guidelines, and contribution instructions.
