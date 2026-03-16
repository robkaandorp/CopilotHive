[![Build](https://github.com/robkaandorp/CopilotHive/actions/workflows/build.yml/badge.svg)](https://github.com/robkaandorp/CopilotHive/actions/workflows/build.yml)

# CopilotHive

CopilotHive is a **self-improving multi-agent orchestration system** powered by the **GitHub Copilot SDK**. Specialized worker agents — coder, tester, reviewer, and improver — collaborate autonomously inside Docker containers to implement software goals without human intervention.

## Architecture

The Orchestrator Brain (an LLM-powered decision engine) receives goals and dispatches work to specialized agents. Each agent runs in an isolated Docker container and reports results back to the Brain.

```
                    ┌─────────────────┐
                    │  Orchestrator   │
                    │     Brain       │
                    │  (LLM-powered)  │
                    └────────┬────────┘
                             │ gRPC
          ┌──────────┬───────┼───────┬──────────┐
          │          │       │       │          │
   ┌──────▼──────┐ ┌─▼────────▼─┐ ┌──▼───────┐ ┌▼──────────┐
   │    Coder   │ │  Reviewer  │ │  Tester  │ │ Improver  │
   │  (Docker)  │ │  (Docker)  │ │ (Docker) │ │ (Docker)  │
   └────────────┘ └────────────┘ └──────────┘ └───────────┘
```

## How It Works

Goals flow through a structured pipeline:

**Coding → Review → Testing → Merge → Improve**

1. **Coding**: The coder agent implements the goal on a feature branch.
2. **Review**: The reviewer agent inspects the diff and produces a structured report.
3. **Testing**: The tester agent builds the project and runs all tests.
4. **Merge**: The Brain decides when quality is sufficient and merges the branch.
5. **Improve**: The improver agent updates `agents.md` based on accumulated metrics.

If testing or review fails, the pipeline retries the coding step (up to a configured limit).

The **Brain** (`DistributedBrain`) interprets each worker's output using LLM reasoning via the GitHub Copilot SDK (JSON-RPC) to decide the next action — retry, advance, or escalate. Pipeline state is persisted to **SQLite** (`PipelineStore`) with auto-migration, so the server can resume after restarts. Metrics feed into the **improver** for self-improvement: the system tunes its own `agents.md` instructions over time.

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

## Project Structure

| Directory | Description |
|-----------|-------------|
| `src/CopilotHive/` | Main orchestrator — Brain, GoalDispatcher, persistence, metrics |
| `src/CopilotHive.Shared/` | Shared protobuf definitions and DTOs |
| `src/CopilotHive.Worker/` | Worker process (runs inside Docker containers) |
| `tests/` | 333 xUnit tests |
| `agents/` | Default agent templates (overridden by config repo at runtime) |
| `docker/` | Dockerfiles and container configuration |
| `metrics/` | File-based telemetry output |

## Current Features

- **Server-only mode** — gRPC server + HTTP health endpoint (no CLI mode)
- **LLM-powered Brain** — `DistributedBrain` uses GitHub Copilot SDK for orchestration decisions
- **Self-improvement loop** — the improver modifies `agents.md` based on accumulated metrics
- **SQLite persistence** — `PipelineStore` with auto-migration for pipeline state
- **Config repo** — externalized agent instructions and goals (`CopilotHive-Config`)
- **Multi-repo goal support** — goals can target any accessible Git repository
- **Per-role model selection** — assign different LLM models to each worker type
- **Auto-rebase on merge conflicts** — the pipeline automatically rebases and retries
- **Fallback metrics parsing** — robust parsing handles varied worker output formats
- **Duplicate goal completion guards** — prevents re-processing of already-completed goals
- **File-based telemetry** — metrics and run data persisted to the `metrics/` directory

## Contributing

See [agents/README.md](agents/README.md) for agent role definitions, behavioral guidelines, and contribution instructions.
