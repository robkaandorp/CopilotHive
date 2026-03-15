# CopilotHive — Vision

This document describes the design vision for where CopilotHive is going. It covers capabilities not yet implemented.

## Future Architecture

As CopilotHive scales, the orchestration layer will evolve to support dynamic worker pools, pluggable model backends, and a new Analyst role.

```
                    ┌─────────────────────┐
                    │   Orchestrator      │
                    │   Brain (LLM)       │
                    └──────────┬──────────┘
                               │
        ┌──────────────────────┼──────────────────────┐
        │                      │                      │
 ┌──────▼──────┐       ┌───────▼──────┐       ┌──────▼──────┐
 │    Coder    │       │   Reviewer   │       │   Tester    │
 └─────────────┘       └──────────────┘       └─────────────┘
        │                      │
            {                 echo ___BEGIN___COMMAND_OUTPUT_MARKER___;                 PS1="";PS2="";unset HISTFILE;                 EC=$?;                 "___BEGIN___COMMAND_DONE_MARKER___$EC" echo;             }
 │   Improver  │       │   Analyst    │  ← future role
 └─────────────┘       └──────────────┘
```

## Roadmap

### Dynamic Scaling
Worker agents will scale horizontally based on pipeline load. The Brain will manage a pool of available workers, spawning and recycling containers as demand fluctuates. This removes the current single-worker-per-role constraint.

### BYOK / Multi-Model Support
Users will be able to bring their own API keys and swap LLM providers (OpenAI, Anthropic, Azure OpenAI, local models via Ollama) at the goal or role level. A provider abstraction layer will make model backends pluggable without code changes.

### Observability Dashboard
A real-time web dashboard will surface pipeline state, per-goal metrics, worker health, and historical trends. The `metrics/` telemetry data will feed a live UI, enabling operators to monitor and intervene without reading log files.

### Analyst Role
A new `analyst` worker will perform static analysis, dependency audits, and codebase health assessment. The Analyst will run asynchronously alongside the main pipeline and feed findings into the Brain's decision context, enabling proactive quality enforcement beyond reactive review.
