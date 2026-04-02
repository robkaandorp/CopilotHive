## [0.6.0]

### Added

**Goal detail page layout.** The goal detail page now uses a structured flexbox layout with dedicated areas for the top metadata strip, side-by-side content panels (40%/60% split), and bottom action buttons. Added `.goal-detail-page` CSS class with `.goal-detail-top`, `.goal-detail-body`, `.goal-detail-left`, `.goal-detail-right`, and `.goal-detail-bottom` selectors for consistent spacing and scroll behavior.

**Three-tier clarification system.** Workers can call `request_clarification` (renamed from `ask_user`) when facing ambiguous goals. Questions route through a three-tier resolution chain: first the Brain attempts to answer from its accumulated context, then the Composer LLM tries using a forked session (`AgentSession.Fork()`) for a one-shot auto-answer, and finally the question surfaces to the human via the Composer chat UI. The `escalate_to_composer` tool replaced the fragile string-based escalation mechanism with a proper tool call. Escalation now works during all Brain phases including planning and prompt crafting. Clarification exchanges (Q&A with answerer attribution) are logged and displayed on the goal detail page, and aggregated stats appear on the Orchestrator dashboard.

**Hardcoded worker system prompts.** Mandatory safety rules (git push prohibition, role identity, tool call contracts, scope boundaries, clarification instructions) are now hardcoded in `SharpCoderRunner.BuildRoleSystemPrompt()` per worker role. AGENTS.md files are appended as supplementary "Learned Heuristics" after a separator. This prevents the improver from accidentally weakening or removing safety-critical instructions.

**Docs-only iteration plans.** The Brain can plan documentation-only iterations (e.g. `[DocWriting, Review, Merging]`) that execute without a Coding phase. `ValidatePlan` accepts DocWriting as a valid alternative to Coding, `PipelineStateMachine` accepts DocWriting as a valid first phase, and `GoalDispatcher` dispatches the plan's actual first phase instead of hardcoding Coder. Previously, every iteration forced a coder, which wasted time on documentation-only goals.

**Reviewer iteration context.** The reviewer now receives the current iteration's test results in its prompt, giving it visibility into test outcomes before producing a verdict. The reviewer also receives an iteration-scoped diff command (`git diff {iterationStartSha}..HEAD`) so it reviews only the current iteration's changes rather than the cumulative branch diff.

**Mandatory code review for code changes.** `ValidatePlan` now enforces that all iteration plans containing a Coding phase include both Testing and Review. Previously, the Brain could skip Review as long as Testing was present, which allowed cross-cutting bugs to slip through unreviewed. Docs-only plans (DocWriting without Coding) still only require at least one of Testing or Review.

**Plan validation feedback to Brain.** When `ValidatePlan` modifies the Brain's proposed iteration plan (e.g. inserting a Review phase), a system note is injected into the Brain's conversation describing the adjustment — original plan, final plan, added phases, and reason. This ensures the Brain knows which phases will actually execute and can craft tailored prompts for all of them, including phases it didn't originally plan.

**Composer config repo access.** The Composer gained five new tools for managing the config repository: `list_config_files`, `read_config_file`, `update_agents_md`, `edit_agents_md`, and `commit_config_changes`. This allows the Composer to inspect and update AGENTS.md files directly.

**Editable Planning releases.** Releases in Planning status can now be edited from both the dashboard UI and the Composer's `update_release` tool.

**Release repo picker.** The release detail page uses a multi-select checkbox picker for repository assignment instead of a plain text input.

**Release filter on Goals page.** A release filter dropdown on the Goals page lets users filter by release tag. Planning (unreleased) versions are included in the dropdown. Entries are deduplicated by tag when multiple releases share the same version.

**Dashboard layout improvements.** Page titles are extracted into a shared header bar component (`PageHeaderState`). The navigation sidebar, header bar, and footer are sticky/fixed so they remain visible while scrolling content. A footer with a GitHub project link is displayed on every page. All nav menu items have emoji icons for visual identification.

### Changed

**Brain prompt optimization.** The Brain's `DefaultSystemPrompt` now contains static role-specific rules that were previously generated dynamically in `BuildCraftPromptText()`. Cross-goal metrics history has been removed from Brain prompts to reduce noise and token usage. The Brain gained a `get_goal` tool for accessing goal details during planning.

**DocWriting phase routing.** The DocWriting phase is now routed through the Brain for prompt crafting, like Coding, Testing, and Review. Previously it used a hardcoded `BuildDocWriterPrompt` method that bypassed the Brain entirely.

**SharpCoder updated to 0.6.0.** Both CopilotHive projects now reference the stable SharpCoder 0.6.0 NuGet package (from 0.5.0), which includes `AgentSession.Fork()` used by the clarification session fork feature.

### Fixed

**Release filter dropdown deduplication.** The release filter dropdown on the Goals page now deduplicates entries by tag, so selecting a tag like `v0.5.0` shows goals from all releases sharing that tag rather than creating duplicate dropdown entries.

**Config repo git safety.** Fixed race conditions in `ConfigRepoManager` git operations that could cause data loss when concurrent operations accessed the config repository.

**Docs-only iteration dispatch.** Fixed three issues preventing docs-only iteration plans from executing correctly: (1) `ValidatePlan` unconditionally inserted a Coding phase — now accepts plans with DocWriting as a valid alternative. (2) `PipelineStateMachine.StartIteration` rejected plans not starting with Coding — now accepts DocWriting as a valid first phase. (3) `DispatchNextGoalAsync` hardcoded Coder dispatch regardless of the plan — now reads the first phase from the plan and dispatches the corresponding role (DocWriter for docs-only, Coder for code changes).

**Version prefix double-beta.** Fixed CopilotHive version infrastructure that produced double-beta Docker image tags (e.g. `0.6.0-beta-beta.42`).

**SharpCoder README URL.** Fixed a hallucinated SharpCoder GitHub URL in the README that pointed to a non-existent repository path.

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
