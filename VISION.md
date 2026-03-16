# CopilotHive — Vision

CopilotHive already runs an LLM-powered Brain that orchestrates Coder, Reviewer, Tester, and Improver workers in Docker containers with per-role model selection and a self-improvement loop. This document describes where we're headed next.

## Future Architecture

As CopilotHive scales, the orchestration layer will evolve to support generic worker pools, pluggable model backends, and new specialist roles.

```
                    ┌─────────────────────┐
                    │   Orchestrator      │
                    │   Brain (LLM)       │
                    └──────────┬──────────┘
                               │
        ┌──────────────────────┼──────────────────────┐
        │                      │                      │
 ┌──────▼──────┐       ┌──────▼───────┐       ┌──────▼──────┐
 │    Coder    │       │   Reviewer   │       │   Tester    │
 └─────────────┘       └──────────────┘       └─────────────┘
        │                      │                      │
 ┌──────▼──────┐       ┌──────▼───────┐       ┌──────▼──────┐
 │   Improver  │       │   Analyst    │       │  Doc Writer │
 └─────────────┘       │   (future)   │       │  (future)   │
                        └──────────────┘       └─────────────┘
```

## Roadmap

### Generic Worker Pool
Replace fixed-role containers with N generic workers that switch roles per task via `SelectAsync`. Each task already does a fresh clone, so isolation is free. This removes the one-container-per-role constraint and enables dynamic scaling based on pipeline load.

### Pluggable Model Providers
Per-role model selection is already implemented via `goals.yaml`. The next step is a provider abstraction layer so non-Copilot backends (OpenAI, Anthropic, Azure OpenAI, local models via Ollama) can be swapped in without code changes.

### Observability Dashboard
A real-time web dashboard will surface pipeline state, per-goal metrics, worker health, and historical trends. Telemetry data will feed a live UI, enabling operators to monitor and intervene without reading log files.

### Analyst Role
A new `analyst` worker will perform static analysis, dependency audits, and codebase health assessment. The Analyst will run asynchronously alongside the main pipeline and feed findings into the Brain's decision context, enabling proactive quality enforcement beyond reactive review.

### Documentation Writer Role
A `doc-writer` worker will auto-update changelogs, READMEs, and API docs after code changes land. It runs post-merge, diffing the before/after state to produce accurate, contextual documentation updates.

## Parked / Exploratory

### Prompt Template Externalization
Move Brain prompt templates out of C# code into a config repo so the Improver can modify them across iterations. Currently parked — the tight coupling between prompt templates, JSON schema expectations, and C# deserialization makes this fragile. Revisit once the prompt/schema interface is more stable.
