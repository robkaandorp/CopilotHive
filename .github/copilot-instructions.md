# CopilotHive — Copilot Instructions

## Project Overview

CopilotHive is a self-improving multi-agent orchestration system. A C# orchestrator
(the "Product Owner") manages a pool of generic Docker containers running SharpCoder
as their AI agent engine. Workers dynamically accept any role (coder, tester, doc-writer,
reviewer, improver) per task. They communicate via direct LLM API calls (SharpCoder)
and operate on isolated git clones. The orchestrator Brain uses SharpCoder's `CodingAgent`
with a single persistent session that carries context across all goals.

## Technology Stack

- **Runtime:** .NET 10 (C# 14)
- **Agent Engine:** `SharpCoder` (NuGet) — autonomous coding agent loop
- **Docker:** `Docker.DotNet` — container lifecycle management
- **Git:** CLI (`git`) — branch/merge operations via `Process.Start`
- **Testing:** xUnit

## Project Structure

```
CopilotHive/
├── src/CopilotHive/                  # Main orchestrator application
│   ├── Agents/AgentsManager.cs       # AGENTS.md versioning and rollback
│   ├── Configuration/                # HiveConfiguration record
│   ├── Git/BrainRepoManager.cs      # Persistent Brain repo clones and merge ops
│   ├── Metrics/                      # Per-iteration metrics tracking
│   ├── Orchestration/DistributedBrain.cs # LLM-powered Brain for orchestration decisions
│   ├── Services/GoalDispatcher.cs    # Pipeline state machine with phase sequencing
│   ├── Workers/DockerWorkerManager.cs # Docker container lifecycle
│   └── Program.cs                    # CLI entrypoint
├── tests/CopilotHive.Tests/         # 616+ xUnit tests
├── agents/                           # AGENTS.md templates per role
│   ├── orchestrator.agents.md
│   ├── coder.agents.md
│   ├── tester.agents.md
│   ├── docwriter.agents.md
│   └── reviewer.agents.md
├── docs/                              # Design documents
│   ├── VISION.md                      # Architecture and future roadmap
│   └── ARCHITECTURE_ALTERNATIVES.md   # Agent runtime evaluation
└── docker/                            # Container definitions
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
- **Separate git clones per worker** — Each task gets its own clone for full isolation.
- **Brain repo clones** — The Brain has persistent read-only clones at `{stateDir}/repos/{repoName}`,
  used for file access during planning/prompting and for merge operations.
- **Sequential goal processing** — Goals process one at a time so the Brain can accumulate
  context across goals via its single persistent session.
- **Metrics-driven improvement** — Every iteration records metrics. Regressions trigger
  automatic rollback of AGENTS.md changes.
- **No `Directory.Delete` without retry** — On Windows, use `ForceDeleteDirectoryAsync`
  for directories containing `.git` (file locking issues).

## Key Design Patterns

- Workers are generic Docker containers that accept any role per task via dynamic agent selection
- The orchestrator Brain uses SharpCoder's `CodingAgent` with a single persistent session
- The Brain has read-only file access to target repos and auto-compacts its context
- Workers use `SharpCoderRunner` with `CodingAgent` for autonomous task execution
- Git operations shell out to `git` CLI (not LibGit2Sharp) for simplicity
- The coder↔tester feedback loop retries up to `MaxRetriesPerTask` times

## Working Principles

- **Do not make assumptions, always verify.** Check actual state (logs, databases,
  git status, running processes) before drawing conclusions or making changes.

## Error Handling Philosophy

- **No silent fallbacks.** Every switch/if-else that resolves roles, prompts, actions,
  or branch names must handle all cases explicitly. A catch-all `_` or `default` case
  must throw an error (e.g. `InvalidOperationException`), not return a generic fallback.
  Silent fallbacks mask bugs and prevent proper debugging and self-improvement.
- **Fail fast on invalid state.** If a required value (like `CoderBranch`) is null when
  it should be set, throw immediately rather than substituting a placeholder like "TBD".
  A failed goal with a clear error is better than a goal that silently produces garbage.
- **Branch names are configuration.** They come from `HiveConfigFile.RepositoryConfig.DefaultBranch`
  and must never be hardcoded as `"main"` or `"develop"` in operational code.
- **Framework and tooling references are configuration.** Prompts must not hardcode
  framework-specific commands like `dotnet build`, `dotnet test`, or test framework names
  like `xUnit`. Instead, reference project skills that contain the actual commands.
  This keeps CopilotHive framework-agnostic.
- **Improve phase is non-blocking.** If the improver fails (Brain timeout, dispatch error),
  the goal continues to Merging. Failures are recorded in `Goal.Notes` and
  `IterationMetrics.ImproverSkipped` — they must never fail the goal.
- **Brain prompts must never include git checkout/branch/switch/push commands.** Branch
  management is handled by infrastructure (`TaskExecutor`). Brain prompts also must never
  include framework-specific build/test commands (e.g. `dotnet build`, `dotnet test`);
  workers discover these via project skills.
