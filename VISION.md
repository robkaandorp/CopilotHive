# CopilotHive — Vision & Architecture

## What is CopilotHive?

CopilotHive is a **self-improving multi-agent orchestration system** built on top of
[GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli)
and the [Copilot SDK](https://github.com/github/copilot-sdk). A **Product Owner**
(orchestrator) running on the host machine delegates work to specialized Copilot workers,
each running in its own Docker container with a unique personality, git clone, and
working directory.

## Core Idea

Instead of one Copilot doing everything, CopilotHive runs a **team** of Copilots:

- **Orchestrator** (Product Owner) — decomposes goals into tasks, delegates to workers,
  tracks metrics, and drives continuous improvement.
- **Coder** — writes code on feature branches.
- **Tester** — writes and runs tests, measures coverage.
- **Reviewer** — reviews code for bugs, security, and quality.
- **Docs Writer** — writes documentation aligned with the code.
- **Merge Worker** — resolves git merge conflicts.

Each worker is a Docker container running Copilot CLI in **headless mode** (JSON-RPC
over TCP). The orchestrator communicates with them via the
[Copilot .NET SDK](https://github.com/github/copilot-sdk/tree/main/dotnet).

## Architecture

```
Host machine (C# console app — the Orchestrator)
│
├── Manages worker lifecycle via Docker.DotNet
├── Communicates via Copilot .NET SDK (headless JSON-RPC over TCP)
├── Manages git repos (separate clone per worker)
├── Tracks metrics per iteration
└── Rewrites AGENTS.md files (for workers AND itself)

workspaces/
├── origin/           ← bare repo (local "remote")
├── coder-1/          ← clone mounted into coder container
├── tester-1/         ← clone mounted into tester container
├── reviewer-1/       ← clone mounted into reviewer container
└── docs-1/           ← clone mounted into docs container

Docker containers (copilot-acp-server image, headless mode)
├── copilothive-coder-1    (port 8001)
├── copilothive-tester-1   (port 8002)
├── copilothive-reviewer-1 (port 8003)
└── ...
```

### Why the Orchestrator runs on the host (not Docker)

- **Direct filesystem access** — creates/manages working folders, clones repos, mounts
  volumes into worker containers.
- **Direct Docker socket access** — spins up/stops containers via Docker.DotNet without
  Docker-in-Docker complexity.
- **Access to local credentials** — git config, SSH keys, GitHub tokens.
- **Easier to debug** — logs, UI, direct observation.

### Why separate git clones per worker

- **Full isolation** — each worker only sees its own branch in its own folder.
- **No stepping on each other** — parallel work without file-level conflicts.
- **Git push/pull between local repos is fast** — just file paths as remotes.
- **Clean audit trail** — every change is on a branch, easy to review and rollback.

## Git Workflow

```
main (protected — only orchestrator merges here)
│
├── coder/feature-x       ← coder works here
├── reviewer/feature-x    ← reviewer gets a copy to annotate
├── tester/feature-x      ← tester writes tests here
├── docs/feature-x        ← docs writer works here
└── merge/feature-x       ← merge worker resolves conflicts
```

1. Orchestrator creates a task branch from `main`, sets up clone for the coder.
2. Coder commits to `coder/feature-x`.
3. Orchestrator pulls the coder's work, pushes to tester's clone.
4. Tester writes/runs tests, reports metrics.
5. If tests fail → feedback goes back to coder.
6. If tests pass → reviewer gets the branch.
7. Reviewer adds review comments (as commits or notes).
8. When all roles are done, orchestrator attempts merge to `main`.
9. If conflicts → Merge Worker resolves them.
10. Orchestrator validates the merge, records metrics.

## Self-Improving System

### Everything is Measurable

Every iteration records metrics:
- Test count, pass/fail rate
- Code coverage percentage
- Lint score
- Review issues found
- Documentation coverage
- Duration

### Every Iteration Must Improve

The orchestrator compares metrics with the previous iteration. If metrics regress
after an AGENTS.md change, the system **automatically rolls back** to the previous
version.

### AGENTS.md Evolution

The orchestrator writes and rewrites the AGENTS.md files for all workers (and itself).
After each iteration, it reviews the metrics and updates worker instructions:

```
End of iteration N:
├── Evaluate workers based on metrics
├── Update workers' AGENTS.md with improvements
├── Evaluate SELF
├── Update own AGENTS.md (applied next iteration)
└── Save all previous AGENTS.md to version history
```

Example: If the coder keeps missing edge cases, the orchestrator might add:
*"Always consider null inputs, empty collections, and boundary conditions.
Write defensive checks before implementing the happy path."*

### Dynamic Scaling

The orchestrator decides worker count based on the workload:
- 2 coders when the task backlog is large
- 0 docs workers when only refactoring (no API changes)
- Extra tester when coverage is below target
- Merge worker spawned on-demand only when conflicts arise

### Safety: Constitution vs Strategy

| Layer | Controlled by | Mutable? |
|---|---|---|
| **Constitution** (C# code) | Developer | No — hard-coded rules |
| **Strategy** (AGENTS.md) | Orchestrator | Yes — self-modifiable |

**Constitution** (immutable, in C#):
- Must track metrics every iteration
- Must reject regressions
- Must keep AGENTS.md version history
- Must rollback on metric decline
- One self-update per iteration maximum

**Strategy** (in orchestrator's AGENTS.md):
- Task decomposition approach
- Delegation heuristics
- When to scale workers up/down
- Communication style with workers
- Priority ordering

### AGENTS.md Version History as Training Data

Over time, the version history shows which instructions lead to better outcomes:

```
agents/history/
├── orchestrator/
│   ├── v001.agents.md → metrics: { efficiency: 0.6 }
│   ├── v002.agents.md → metrics: { efficiency: 0.7 } ✅ kept
│   └── v003.agents.md → metrics: { efficiency: 0.5 } ❌ rolled back to v002
├── coder/
│   ├── v001.agents.md → metrics: { coverage: 71% }
│   └── v002.agents.md → metrics: { coverage: 78% }
```

## Technology Stack

- **Language:** C# / .NET 10
- **Copilot SDK:** [GitHub.Copilot.SDK](https://www.nuget.org/packages/GitHub.Copilot.SDK)
  (headless JSON-RPC to workers)
- **Docker:** [Docker.DotNet](https://www.nuget.org/packages/Docker.DotNet) (container
  lifecycle)
- **Git:** Shell out to `git` CLI for branch/merge operations
- **Worker Image:** [copilot-acp-server](../copilot-acp-server) Docker image with
  `COPILOT_MODE=headless`
- **Persistence:** JSON files for metrics, versioned AGENTS.md files

## BYOK / Ollama Support

The Copilot SDK supports Bring Your Own Key (BYOK) with providers like OpenAI, Anthropic,
and **Ollama**. This means:

- The orchestrator could use a premium model (Claude Opus, GPT-5) for complex reasoning.
- Simple workers (docs, linting) could use local Ollama models — free and fast.
- Different workers can use different models based on task complexity.

## Roadmap

### v0.1 — Prove the Loop ✅
Orchestrator + Coder + Tester. Minimal working loop that delegates a coding task,
runs tests, collects metrics, and iterates.

### v0.2 — Bootstrap: CopilotHive Develops Itself
The first real workload for CopilotHive is **itself**. This is the ultimate validation:
if the system can't improve its own codebase, it can't improve anything else.

**The bootstrap sequence:**
1. Add a test project so the tester has something to measure.
2. Run CopilotHive with `--goal="Write unit tests for the CopilotHive codebase"` —
   the coder writes tests, the tester runs them, metrics are established.
3. Run CopilotHive with `--goal="Add retry logic and error handling"` —
   measurable improvement over the baseline.
4. Run CopilotHive with `--goal="Implement AGENTS.md self-improvement"` —
   the system begins evolving its own instructions.

**Why this works:**
- The codebase is small and self-contained — perfect scope for early iterations.
- Clear metrics: does it build? Do tests pass? Does the loop complete?
- Improvements compound — if the coder improves the orchestrator, the next iteration
  benefits immediately.
- It validates the architecture end-to-end.

### v0.3 — Reviewer + Metrics-Driven Improvement
Add the reviewer role. Orchestrator rewrites worker AGENTS.md based on metrics.
Iteration-over-iteration comparison.

### v0.4 — Self-Improving Orchestrator
Orchestrator updates its own AGENTS.md. Rollback on regression. Version history
as a feedback signal.

### v0.5 — Dynamic Scaling + Merge Worker + Docs
Dynamic worker count. Merge worker for conflict resolution. Documentation writer.

### v0.6 — BYOK / Multi-Model
Different models per role. Ollama for simple tasks. Cost optimization.
