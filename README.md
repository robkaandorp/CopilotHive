[![Build](https://github.com/robkaandorp/CopilotHive/actions/workflows/build.yml/badge.svg)](https://github.com/robkaandorp/CopilotHive/actions/workflows/build.yml)

# CopilotHive

CopilotHive is a **self-improving multi-agent orchestration system** powered by the **GitHub Copilot SDK**. A pool of generic worker agents collaborate autonomously inside Docker containers — dynamically taking on roles (coder, tester, doc-writer, reviewer, improver) per task — to implement software goals without human intervention.

## Architecture

The Orchestrator Brain (an LLM-powered decision engine) receives goals and dispatches work to a pool of generic workers. Each worker runs in an isolated Docker container and accepts any role (coder, tester, doc-writer, reviewer, improver) per task.

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

The **Brain** (`DistributedBrain`) plans iteration phases and crafts worker prompts using the GitHub Copilot SDK (JSON-RPC). Workers report structured verdicts via tool calls, and the pipeline state machine (`PipelineStateMachine`) drives sequencing — retrying, advancing, or failing based on those verdicts. Pipeline state is persisted to **SQLite** (`PipelineStore`) with auto-migration, so the server can resume after restarts. Metrics feed into the **improver** for self-improvement: the system tunes its own `agents.md` instructions over time.

## Getting Started

### Prerequisites

- [Docker](https://www.docker.com/) (latest stable)
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [GitHub Copilot](https://github.com/features/copilot) subscription with API access
- A **config repo** containing `agents/*.agents.md` and `goals.yaml` (see below)

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

   Set `BRAIN_COPILOT_PORT` to override the port the Brain uses to reach the Copilot CLI process.

### Configuring Goals

Goals are defined in `goals.yaml` (typically stored in the config repo). Each goal specifies what to build and optionally which model to use per role:

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

When a goal completes (success or failure), the Orchestrator updates the goal entry with metadata including `phase_durations` — a map of phase names to wall-clock durations in seconds — and `iteration_summaries` — an array of structured summaries (one per iteration) capturing phases, test results, and review verdicts:

```yaml
goals:
  - id: my-feature
    # ... (input fields above)
    started_at: "2026-03-17T19:38:37Z"
    completed_at: "2026-03-17T19:42:15Z"
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
          - name: Testing
            result: fail
            duration_seconds: 20.5
        test_counts:
          total: 10
          passed: 8
          failed: 2
        review_verdict: null
        notes: []
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
| `tests/` | 478 xUnit tests |
| `agents/` | Default agent templates (overridden by config repo at runtime) |
| `docker/` | Dockerfiles and container configuration |

## Current Features

- **Server-only mode** — gRPC server + HTTP health endpoint (no CLI mode)
- **LLM-powered Brain** — `DistributedBrain` uses GitHub Copilot SDK for orchestration decisions
- **Worker utilization metrics** — `GET /health/utilization` endpoint provides per-role worker utilization and bottleneck detection
- **Self-improvement loop** — the improver modifies `agents.md` based on accumulated metrics
- **SQLite persistence** — `PipelineStore` with auto-migration for pipeline state
- **Config repo** — externalized agent instructions and goals (`CopilotHive-Config`)
- **Multi-repo goal support** — goals can target any accessible Git repository
- **Per-role model selection** — assign different LLM models to each worker type
- **Auto-rebase on merge conflicts** — the pipeline automatically rebases and retries
- **Fallback metrics parsing** — robust parsing handles varied worker output formats
- **Duplicate goal completion guards** — prevents re-processing of already-completed goals
- **Telemetry** — per-run metrics aggregated and fed into the improver
- **Dirty-worktree safety net** — automatically re-prompts Copilot if uncommitted changes remain after task execution
- **Brain retry mechanism** — automatic retries on LLM timeout or transient failures (up to 2 retries with 5-second backoff)
- **Non-blocking improve phase** — improver failures don't prevent goal completion; recorded in goal notes and metrics
- **Three-dot diff comparison** — accurate detection of all changes on feature branches using `origin/{baseBranch}...HEAD`
- **Goal notes** — non-fatal observations tracked in goals.yaml (e.g. "Improver skipped: timeout")
- **Iteration summaries** — structured per-iteration metrics (phases, test counts, review verdicts) recorded in goals.yaml for observability without reading logs

## Contributing

See [agents/README.md](agents/README.md) for agent role definitions, behavioral guidelines, and contribution instructions.
