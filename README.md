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
                    └────────┬──
                             │
          ┌──────────────────┼──────────────────┐
          │                  │                  │
   ┌──────▼──────┐   ┌───────▼──────┐   ┌──────▼──────┐
   │    Coder    │   │   Reviewer   │   │   Tester    │
   │  (Docker)   │   │   (Docker)   │   │  (Docker)   │
   └─────────────┘   └──────────────┘   └─────────────┘
                             │
                    ┌────────▼────────┐
                    │    Improver     │
                    Docker    (│)     
                    └─────────────────┘
```

## How It Works

Goals flow through a structured pipeline:

**Coding → Review → Testing → Improve → Merge**

1. **Coding**: The coder agent implements the goal on a feature branch.
2. **Review**: The reviewer agent inspects the diff and produces a structured report.
3. **Testing**: The tester agent builds the project and runs all tests.
4. **Improve**: The improver agent addresses reviewer and tester feedback.
5. **Merge**: The Brain decides when quality is sufficient and merges the branch.

The **Brain** interprets each worker's output using LLM reasoning to decide the next action — retry, advance, or escalate. Metrics from each run are persisted to disk and feed into **metrics-driven self-improvement**: the system tunes its own behavior over time. **AGENTS.md** evolves as the system accumulates learnings about effective agent behavior.

## Getting Started

### Prerequisites

- [Docker](https://www.docker.com/) (latest stable)
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [GitHub Copilot](https://github.com/features/copilot) subscription with API access

### Setup

1. Clone the repository:
   ```bash
   git clone https://github.com/your-org/CopilotHive.git
   cd CopilotHive
   ```

2. Configure environment variables:
   ```bash
   cp .env.example .env
   # Edit .env with your GitHub Copilot API credentials
   ```

3. Start the orchestrator:
   ```bash
   dotnet run --project src/CopilotHive
   ```

### Configuring Goals

Goals are defined in `goals.yaml`. Each goal specifies what to build and optionally which model to use per role:

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
| `src/` | Orchestrator and shared library source code |
| `tests/` | Unit and integration tests |
| `agents/` | Worker agent definitions and AGENTS.md |
| `docker/` | Dockerfiles and container configuration |
| `metrics/` | File-based telemetry and metrics output |

## Current Features

- **Multi-repo goal support** — goals can target any accessible Git repository
- **Per-role model selection** — assign different LLM models to each worker type
- **Auto-rebase on merge conflicts** — the pipeline automatically rebases and retries
- **File-based telemetry** — metrics and run data persisted to the `metrics/` directory
- **Fallback metrics parsing** — robust parsing handles varied worker output formats

## Contributing

See [AGENTS.md](agents/AGENTS.md) for agent role definitions, behavioral guidelines, and contribution instructions.
