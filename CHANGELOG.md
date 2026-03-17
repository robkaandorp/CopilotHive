# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `GoalPhaseExtensions.ToDisplayName()` extension method that returns human-friendly display names for all `GoalPhase` enum values (e.g. `"Doc Writing"` for `DocWriting`, `"Improvement"` for `Improve`); uses explicit switch expression covering all 9 enum values and throws `InvalidOperationException` for unhandled values.
- `GoalPipeline.GetDisplayName(GoalPhase)` static helper that returns human-friendly display names for all pipeline phases (e.g. `"Doc Writing"` for `DocWriting`, `"Improvement"` for `Improve`).
- `WorkerRoleExtensions.ToDisplayName()` extension method that returns human-friendly display names for all `WorkerRole` enum values (e.g. `"Doc Writer"` for `DocWriter`, `"Merge Worker"` for `MergeWorker`); throws `InvalidOperationException` for unhandled values.
- `GoalExtensions.ToDisplayName()` extension methods for `GoalPriority` (returns "Critical", "High", "Normal", "Low") and `GoalStatus` (returns "Pending", "In Progress", "Completed", "Failed", "Cancelled"); both use explicit switch expressions and throw `InvalidOperationException` for unhandled values.

### Changed
- Extracted `OrchestratorVersion` string into `Constants.OrchestratorVersion` public const in `Constants.cs`; `HiveOrchestratorService` now references the constant instead of a hardcoded literal.
- Extracted `CleanupIntervalSeconds` and `StaleTimeoutMinutes` constants from `StaleWorkerCleanupService` into a new dedicated static class `CleanupDefaults` (in `CopilotHive.Services`) for improved discoverability and reuse.

## [0.8] — Doc Writer Role

### Added
- Doc-writer pipeline phase (between Testing and Review) for automatic documentation updates
- `docwriter.agents.md` — instructions for updating README, CHANGELOG, XML doc comments
- `GoalPhase.DocWriting` and `OrchestratorActionType.SpawnDocWriter`
- `WORKER_ROLE_DOCS_WRITER` in gRPC proto (value 5)
- Default model: claude-haiku-4.5 (fast/cheap); premium: claude-sonnet-4.6
- Brain prompts include docwriting in available phases and example plans
- Improver now reviews docwriter.agents.md alongside other agent files

### Changed
- Default pipeline order: Coding → Testing → DocWriting → Review → Improve → Merge
- Brain CraftPromptAsync includes docwriter guidance
- Agents.md size validation covers docwriter role

## [0.7] — Generic Worker Pool & Pipeline Reorder

### Added
- Generic worker pool — workers register without a fixed role and accept any role per task
- Workers dynamically receive role-specific agents.md before each task assignment
- `WORKER_REPLICAS` env var to configure number of generic workers (default: 4)
- `IsGeneric` flag on ConnectedWorker for pool identity tracking
- `TryDequeueAny()` on TaskQueue for role-agnostic task dispatch
- Premium model tier for coder and tester (claude-opus-4.6)
- Code coverage collection via Coverlet — CoveragePercent now populated with real values
- Coverage parsing in FallbackParseTestMetrics (Coverlet text table + key-value formats)
- model_tier propagation to all Brain methods (InterpretOutputAsync, PlanGoalAsync, DecideNextStepAsync)
- First-non-null-wins logic for model_tier via ApplyModelTierIfNotSet helper
- Streaming model output deltas to worker console (AssistantMessageDeltaEvent)

### Changed
- Pipeline phase order: Coding → Testing → Review (was Coding → Review → Testing)
  - Reviewer now gets test results and can review tester-written tests
- docker-compose: single `worker` service with replicas replaces 4 fixed-role services
- entrypoint.sh: all workers clone config repo at startup (any may act as improver)
- WORKER_ROLE env var is now optional (empty = generic pool worker)
- GoalDispatcher uses ApplyModelTierIfNotSet instead of unconditional tier overwrite

### Removed
- Legacy CLI-mode orchestrator code (2,922 lines)
- Unused metrics/ folder

## [0.6] — Server Architecture & DistributedBrain

### Added
- Server-only architecture — gRPC server + HTTP health endpoint (removed CLI mode entirely)
- DistributedBrain — LLM-powered orchestrator Brain using GitHub Copilot SDK JSON-RPC
- GoalDispatcher — pipeline state machine with phase sequencing (Coding → Review → Testing → Merge → Improve)
- SQLite persistence (PipelineStore) with automatic schema migration
- Config repo integration — external CopilotHive-Config repo for agents.md and goals.yaml
- AgentsManager — agents.md versioning, rollback on regression
- ConfigRepoManager — config repo sync and checkout
- Premium model tier selection — Brain can escalate to premium models for complex tasks
- Duplicate goal completion guards — prevent late task callbacks from re-triggering completion
- Orchestrator agent pre-selection via RPC (ensures correct custom agent is active)
- Native SDK telemetry with TelemetryConfig on CopilotClientOptions
- Telemetry aggregation — summarized metrics injected into improver context
- Agents.md size enforcement — 4000-character limit with improver retry loop
- Auto-rebase with pre-improver SHA tracking
- Less prescriptive goal philosophy — goals describe WHAT not HOW
- Comprehensive test suite: 333 xUnit tests

### Changed
- Orchestrator now runs exclusively in server mode (removed --serve flag)
- Brain prompts instruct workers to discover HOW, not prescribe exact file/method changes
- Improver receives richer context: iteration outcomes, retry counts, specific issues

### Removed
- Legacy CLI-mode orchestrator (OrchestratorBrain, Orchestrator.cs)
- Legacy Copilot client abstractions (CopilotWorkerClient, ICopilotWorkerClient, CopilotClientFactory)
- 53 legacy tests removed (333 remain)

## [0.5] — Improver Role & Observability

### Added
- Improver worker role for iterative code refinement after review/test feedback
- Per-role model selection — assign different LLM models to each worker type via goals.yaml
- Auto-rebase on merge conflicts — pipeline automatically rebases feature branches and retries
- File-based telemetry — run metrics and outcomes persisted to the metrics/ directory
- Fallback metrics parsing — robust parsing handles varied and partial worker output formats

## [0.4] — Brain & Multi-Repo Support

### Added
- Brain module: LLM-powered orchestrator intelligence for interpreting worker output
- Multi-repo goal support — goals can target any accessible Git repository, not just the host repo

### Changed
- Orchestrator now uses LLM reasoning for all tactical decisions instead of hardcoded rule logic

## [0.3] — Reviewer Role & Self-Improvement

### Added
- Reviewer worker role — produces structured REVIEW_REPORT with approve/request-changes verdict
- Metrics-driven self-improvement loop — system tunes behavior based on historical run data
- AGENTS.md evolution — system updates its own agent definitions based on accumulated learnings

## [0.2] — Bootstrap & Containerization

### Added
- Bootstrap capability — CopilotHive can develop and improve itself using its own pipeline
- Docker containerization — all worker agents run in isolated Docker containers

### Changed
- Workers migrated from in-process execution to container-based isolation

## [0.1] — Initial Release

### Added
- Orchestrator core — goal intake, phase sequencing, branch management
- Coder worker role — implements goals on feature branches
- Tester worker role — builds project and runs test suite
- Basic goal pipeline: Coding → Testing loop
