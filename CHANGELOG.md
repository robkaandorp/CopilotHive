## [Unreleased]

### Fixed

**Release filter dropdown deduplication.** The release filter dropdown on the Goals page now deduplicates entries by tag, so selecting a tag like `v0.5.0` shows goals from all releases sharing that tag (e.g., both CopilotHive and SharpCoder v0.5.0 releases) rather than creating duplicate dropdown entries.

## [0.5.1] - 2026-03-29

### Fixed

**Empty repository handling.** The Brain and BrainRepoManager now detect empty repositories during the clone phase and skip the develop-branch checkout that previously raised a fatal git error. Workers' GitOperations creates an orphan branch with an initial commit rather than failing when the target repository contains no commits.

**Orphan branch diff detection.** Fixed an issue where the orchestrator incorrectly reported "0 files changed" for orphan branches on empty repositories. The `GetGitStatusAsync` three-dot diff (`origin/develop...HEAD`) fails when there is no common ancestor between an orphan branch and the base branch. Added a fallback that diffs against Git's empty tree when the base-branch diff fails, correctly capturing all committed files.

**Web fetch null links crash.** The Composer's WebFetchAsync method now gracefully handles null or missing links arrays returned by the Ollama search API, preventing an unhandled null-reference exception that crashed the fetch tool when results contained no link data.

**Orchestrator version display.** The hardcoded `OrchestratorVersion = "1.0.0"` constant has been replaced with a runtime lookup via `AssemblyInformationalVersionAttribute`, so the version shown in the UI and logs always reflects the actual assembly version rather than a stale placeholder.

**Retry failed goals.** A retry button on the dashboard allows failed goals to be reset to Draft status, clearing the failure reason and all iteration data through `IGoalStore.ResetGoalIterationDataAsync`. Redispatching after a retry is coordinated by `GoalDispatcher.ClearGoalRetryState`, ensuring the goal re-enters the pipeline cleanly without residual state from the previous attempt.

**Brain context for retried goals.** Fixed an issue where the Brain's persistent session retained conversation history from a failed goal's original run. When a goal was retried, the Brain would reference stale planning and prompt context from the previous attempt, leading to confused iteration prompts. Retried goals now inject explicit context into the Brain's planning and prompt-crafting calls, instructing it to disregard previous session history and treat the goal as a fresh start.

**Orphan merge skip.** Fixed an issue where `BrainRepoManager` would skip the squash merge with a warning when the default branch did not yet exist on the remote, discarding the worker's output silently. When the merge target is missing but the feature branch exists on origin, the default branch is now created from the feature branch tip and pushed so that subsequent goals have a valid merge base.

**Missing base branch on worker checkout.** Fixed an issue in `GitOperations` where `git checkout baseBranch` would fail on non-empty repositories that had not yet fetched all remote branches. Workers now attempt to fetch the base branch from origin and create a local tracking branch before falling back to creating the branch from the current HEAD, ensuring feature branches always have a valid base regardless of the local clone state.

**Orphan branch merge handling.** Fixed an issue where merging the first feature branch on a new repository silently skipped the merge because the default branch (e.g. `main`) didn't exist on origin yet. The orchestrator now creates the default branch from the feature branch content when the default branch doesn't exist, ensuring the initial scaffold is properly merged and follow-up goals can build on it.

**Worker base branch fallback.** Fixed a crash where the worker failed to create a feature branch when the configured base branch didn't exist in the cloned repository. The worker now attempts to fetch the base branch from origin, or creates it from the current HEAD if it doesn't exist anywhere, instead of throwing an exception.

## [0.5.0] - 2026-03-28

### Added

**Composer Agent & Chat UI.** A conversational Composer agent at `/composer` provides streaming chat for goal decomposition and management. It offers goal CRUD tools (create, approve, update, delete, cancel, list, search), codebase inspection (read_file, glob, grep), five read-only git tools, web search and fetch via Ollama, phase output inspection with brain and worker prompt access, repository listing, release management tools, and interactive questions with markdown rendering. The chat persists sessions across page navigations and automatically recovers from context overflow by resetting the session.

**Brain & Orchestration.** The Brain uses SharpCoder's CodingAgent with a single persistent session that carries context across all goals, replacing per-goal session management. It has read-only file access to all target repositories cloned eagerly at startup, automatic context compaction at 80% capacity, and session persistence for crash recovery. Goals process sequentially so the Brain accumulates learnings. The Brain generates concise commit messages for squash merges with fallback to description-based messages.

**Dashboard & UI.** A Goals browser at `/goals` lists all goals with status, scope, creation date, and iteration count. The Goal detail view shows full state including brain and worker prompts, iteration history, and merge commits. A Planning phase tab displays a dependency visualization graph and a configuration page shows brain model, worker replicas, and git branch settings. The UI displays the current version in the footer and a releases page lists all releases with their dates and scopes.

**Goal Management.** A SQLite-backed goal store serves as the primary persistence layer for all goal data. A REST API exposes CRUD operations for goal creation, approval, deletion, cancellation, and revert. Goals support dependency linking and scoping into patch, feature, and breaking-change categories. Release tracking maintains goal-to-release associations, and status validation ensures only valid state transitions are permitted.

**Worker Sessions.** Per-role worker sessions persist context across iterations, enabling workers to maintain state within a goal. Workers communicate over gRPC for efficient streaming and binary serialization.

**Pipeline Features.** The documentation writing phase runs conditionally based on goal scope. Squash merges are used for all goal completions, with merge commit tracking persisting worker output across iterations. Iteration summaries provide a bird's-eye view of goal progress, and the improvement phase runs non-blocking so it does not delay goal completion.

**Infrastructure.** GitHub Actions provides continuous integration with multi-arch Docker images supporting both amd64 and arm64. HTTP resilience patterns handle transient failures gracefully. Structured logging captures all system events in a consistent format.

**Observability.** Context usage is logged for every phase to track token consumption. Phase duration tracking measures end-to-end latency per phase. Worker utilization metrics and elapsed time are recorded for all pipeline stages.

### Changed

The SharpCoder dependency was updated to a newer version with improved code generation. The pipeline phase order was adjusted so documentation writing runs conditionally based on goal scope. SQLite became the primary goal store, replacing the previous file-based approach. The generic worker pool replaces fixed-role Docker Compose services, allowing a single worker type to handle multiple roles. The logging infrastructure migrated to structured logging with consistent field formatting across all services.

### Fixed

Goal cancellation now properly terminates all in-progress phases and clears pending work. Session context no longer leaks between unrelated goals. Worker gRPC connections are properly closed when goals complete or are cancelled. The Brain's context compaction now correctly preserves critical state while trimming peripheral information. Repository cloning no longer fails on repositories with unusually deep directory structures. Merge commit detection now correctly identifies squash merges versus regular commits.

### Removed

Legacy CLI mode and all associated orchestrator code have been removed in favor of the server-driven model. Per-goal session management was replaced by the Brain's persistent session, eliminating the WORKER_ROLE environment variable and fixed-role service definitions. Complex auto-rebase handling, the metrics folder placeholder, and unused abstraction layers for legacy Copilot clients were removed. The --serve flag is no longer needed as server mode is now default.
