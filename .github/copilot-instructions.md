# CopilotHive — Copilot Instructions

## Project Overview

CopilotHive is a self-improving multi-agent orchestration system. A C# orchestrator
(the "Product Owner") spawns Docker containers running GitHub Copilot in headless mode,
each with a specialized role (coder, tester, reviewer). Workers communicate via the
Copilot .NET SDK over JSON-RPC and operate on isolated git clones.

## Technology Stack

- **Runtime:** .NET 10 (C# 14)
- **Copilot SDK:** `GitHub.Copilot.SDK` (NuGet) — headless JSON-RPC communication
- **Docker:** `Docker.DotNet` — container lifecycle management
- **Git:** CLI (`git`) — branch/merge operations via `Process.Start`
- **Testing:** xUnit

## Project Structure

```
CopilotHive/
├── src/CopilotHive/                  # Main orchestrator application
│   ├── Agents/AgentsManager.cs       # AGENTS.md versioning and rollback
│   ├── Configuration/                # HiveConfiguration record
│   ├── Copilot/CopilotWorkerClient.cs # SDK wrapper with retry logic
│   ├── Git/GitWorkspaceManager.cs    # Bare repo, clones, branching, merging
│   ├── Metrics/                      # Per-iteration metrics tracking
│   ├── Orchestration/Orchestrator.cs # Main loop: coder → tester → merge
│   ├── Workers/DockerWorkerManager.cs # Docker container lifecycle
│   └── Program.cs                    # CLI entrypoint
├── tests/CopilotHive.Tests/         # xUnit tests (26 tests)
├── agents/                           # AGENTS.md templates per role
│   ├── orchestrator.agents.md
│   ├── coder.agents.md
│   └── tester.agents.md
└── VISION.md                         # Architecture and design document
```

## Build and Test

```bash
dotnet build CopilotHive.slnx
dotnet test CopilotHive.slnx
```

## Conventions

- **Constitution in C#** — Immutable rules (metric tracking, regression rejection, rollback)
  are enforced in code, never in AGENTS.md.
- **Strategy in AGENTS.md** — Delegation heuristics, priorities, and communication style
  are self-modifiable by the orchestrator, one update per iteration max.
- **Separate git clones per worker** — Each worker gets its own clone for full isolation.
  A bare repo acts as the local "remote".
- **Metrics-driven improvement** — Every iteration records metrics. Regressions trigger
  automatic rollback of AGENTS.md changes.
- **No `Directory.Delete` without retry** — On Windows, use `ForceDeleteDirectoryAsync`
  for directories containing `.git` (file locking issues).

## Key Design Patterns

- Workers are Docker containers using the `copilot-acp-server` image in headless mode
- The orchestrator communicates via `CopilotClient` from `GitHub.Copilot.SDK`
- Git operations shell out to `git` CLI (not LibGit2Sharp) for simplicity
- The coder↔tester feedback loop retries up to `MaxRetriesPerTask` times
