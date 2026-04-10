## [0.9.0] - 2026-04-11

### Architecture & Refactoring

- **DistributedBrain decomposed** — `DistributedBrain` was split into focused services: `BrainPromptBuilder` (static prompt construction), `BrainPlanParser` (plan parsing/validation), `BrainSessionManager` (session lifecycle), `BrainDecisionMaker` (planning/prompt-crafting LLM calls), and a slim `DistributedBrain` coordinator. This improves testability and makes each concern independently modifiable.
- **GoalDispatcher extracted into services** — `GoalDispatcher` was decomposed from a ~800-line monolith into focused service classes: `PipelineDriver` (phase execution loop), `GoalLifecycleService` (mark completed/failed), `GoalMaintenanceService` (periodic cleanup), and `SyncAgentsService` (config repo sync). The dispatcher now delegates to these services.
- **Unified model resolution** — `HiveConfiguration` was removed. All model configuration now flows through `HiveConfigFile` with a three-tier resolution chain: per-model overrides → per-role defaults → global default. This eliminated inconsistencies where the Brain and workers could pick different models.
- **Sub-models extracted from GoalPipeline** — `BranchContext`, `ConversationTracker`, and `RoleSessionStore` were extracted as standalone types from `GoalPipeline`, reducing its surface area and improving encapsulation.
- **GoalDispatcher forwarding wrappers removed** — Static forwarding wrappers in `GoalDispatcher` that just delegated to `PipelineDriver` methods were replaced with direct calls, reducing indirection.
- **Strongly-typed Brain tool results** — `BrainToolCallResult` (a plain string) was replaced with discriminated-union records: `EscalateResult`, `IterationPlanResult`, and `GoalLookupResult`. This eliminated string-matching on Brain tool results.

### Knowledge Graph

- **Knowledge Graph data model** — Added `KnowledgeDocument`, `DocumentLink`, `DocumentType`, `DocumentStatus`, `LinkType` entities. `KnowledgeGraph` service with CRUD, link management, inverse queries, BFS traversal, YAML frontmatter handling, and path/ID round-tripping. `Goal.Documents` field added to `Goal.cs` with SQLite schema and serialization updates.
- **Composer knowledge tools** — 9 new Composer tools: `create_document`, `read_document`, `update_document`, `delete_document`, `search_knowledge`, `list_documents`, `link_document`, `unlink_document`, `traverse_graph`. All mutating operations are immediately committed to the config repo.
- **Brain knowledge tools** — `search_knowledge`, `read_document`, and `traverse_graph` tools added to the Brain for querying and exploring the knowledge graph during planning.
- **Knowledge Graph dashboard** — `/knowledge` page with filterable document list. `/knowledge/{DocumentId}` detail page showing content, metadata, outgoing/incoming links, and related goals. 📚 Knowledge nav item in sidebar.

### Bug Fixes

- **Iteration/phase failure color** — Iteration tabs and phase indicators always showed green/successful even when a reviewer requested changes or tests failed. `PipelineDriver` was mapping `PhaseInput.RequestChanges` to `PhaseOutcome.Pass` instead of `PhaseOutcome.Fail`. Stored iteration tabs used a hardcoded `"iter-tab done"` class instead of checking for failed phases.
- **PipelineDriver WorkerOutput** — `PipelineDriver` was overwriting `WorkerOutput` with raw `result.Output` instead of preferring `result.Metrics.Summary`, causing review feedback to be lost.
- **Composer KnowledgeGraph injection** — The Composer factory was missing the `knowledgeGraph` parameter, causing all 9 knowledge tools to never be registered.
- **Clarification deduplication** — Clarification escalation and cancellation handling was deduplicated.
- **Compaction model display** — The Orchestrator dashboard now correctly renders the compaction model reasoning badge.
- **Compaction model configuration** — Added `models.compaction` to `hive-config.yaml` for specifying a separate model for context compaction.

### Dependencies

- **SharpCoder upgraded to 0.8.0** — Picks up `AgentOptions.CompactionClient` for separate compaction model support.

## [0.8.1]

### Added

- **Configurable worker context window** — workers no longer use a hardcoded 100,000-token context window. A three-tier fallback is available via `hive-config.yaml`:
  1. `workers.<role>.context_window` (per-role override)
  2. `orchestrator.worker_context_window` (global default for all workers)
  3. Built-in default of 150,000 tokens

  The resolved value flows through task assignment to workers and drives both context usage percentage and compaction threshold.

  ```yaml
  workers:
    coder:
      context_window: 200000   # per-role override
  orchestrator:
    worker_context_window: 150000  # global default for all workers
  ```

### Fixed

- **GPT-5.x streaming crash** — `CopilotResponsesHandler` was intercepting SSE streaming responses and parsing them as JSON; fixed by passing through `text/event-stream` responses unchanged.
- **Multi-round phase display in Goal Detail** — repeated phases now use occurrence-aware keys and assignment so phase buttons, output, and timeline entries are not duplicated or cross-highlighted.
- **Planning escalation display** — clarifications created when the Brain escalates during the Planning phase are now shown in Goal Detail.
- **Clarification timeline cleanup** — clarification requests now render only once as structured clarification cards, instead of first appearing as an unstructured raw progress entry and then again as a formatted card.
- **Role badge fixes in Goal Detail** — Planning/Brain and Improve/Improver role labels now display correctly in the timeline and summarised iteration views.
- **TaskExecutor logging robustness in tests/CI** — `TaskExecutor` no longer crashes when logging to a closed/disposed console writer during test execution.

## [0.8.0]

### Added

**Multi-round coding iterations.** The Brain can now plan multiple sequential Coding+Testing rounds within a single iteration before reaching Review (e.g. `[coding, testing, coding, testing, review, improve, merging]`). This is useful for large file changes that risk LLM response timeouts, or work that naturally splits into sequential steps with dependencies. Each coding round gets its own phase instruction keyed as `coding-1`, `coding-2`, etc. in the iteration plan. `ValidatePlan` enforces that each Coding round is immediately followed by Testing.

**Worker phase instruction in `get_goal` response.** When a worker calls the `get_goal` tool, the response now includes `current_phase_instruction` — the Brain's specific instruction for the current coding round (e.g. the `coding-2` instruction for the second round). Workers no longer need a separate tool call to retrieve round-specific instructions.

**Worker context usage in Workers dashboard.** Each worker now reports its current session context usage percentage with every 30-second heartbeat. The Workers page displays a colour-coded "Ctx" column (green below 50%, amber 50–79%, red 80%+) for busy workers. Uses the exact token count from the most recent API response (`LastKnownContextTokens`) with fallback to a heuristic estimate before the first API call.

**Clarification bell icon and slide-out drawer.** The always-visible clarification side panel on the Composer page has been replaced by an on-demand slide-out drawer. A 🔔 bell icon with a red count badge appears in the global header whenever a worker requests human clarification — visible from any page. Clicking the bell opens a drawer that slides in from the right with a semi-transparent backdrop. The drawer auto-closes when all pending clarifications are answered. The bell disappears when there are no pending requests.

**Responsive navigation.** The sidebar nav collapses to icon-only mode (52px wide) on viewports ≤ 768px, showing only emoji icons for each nav item. The brand name collapses to the 🐝 emoji. The version badge moved from the nav bottom to the footer, visible at all viewport widths. The footer spans both columns in collapsed mode.

### Changed

**Brain context metrics use master session.** The Brain statistics on the Orchestrator page now always reflect the master session (the long-lived session that accumulates goal summaries), rather than whichever goal fork session happened to be active at poll time. This eliminates wild fluctuations (10% → 70%+) that occurred as the Brain swapped between concurrent goal sessions.

**Brain and Composer context token count is now exact.** Both Brain and Composer `GetStats()` now use `LastKnownContextTokens` (the exact `InputTokenCount` from the most recent API response, including system prompt and tool definition overhead) when available, falling back to the character-based heuristic estimate only before the first API call. `BrainStats.EstimatedContextTokens` renamed to `ContextTokens` to reflect this.

**RetryBudget replaces mutable retry counters.** Mutable `ReviewRetries`/`MaxReviewRetries` and `TestRetries`/`MaxTestRetries` integer pairs on `GoalPipeline` replaced by a thread-safe `RetryBudget` type. Encapsulates remaining/maximum budget with `TryConsume()` and `IsExhausted` properties for cleaner retry tracking.

**Iteration plan phase tracking removed.** Redundant `IterationPlan.CurrentPhaseIndex` tracking system removed. Phase progression is now driven exclusively by the pipeline state machine without a parallel index counter.

### Fixed

**Plan reason pinned above scrollable timeline.** The Brain's plan reason text (📋) was previously inside the scrollable `.iter-content` div, causing it to scroll out of view when the progress timeline filled with entries. It is now rendered above the scroll area as a sibling element, always visible.

**Page header `h1` bottom margin removed.** The `h1` inside `.hive-header` no longer has a bottom margin, eliminating unwanted spacing in the header bar.

**Goals nav icon restored.** The Goals nav link icon was incorrectly changed to 💯 by the responsive nav goal. Restored to the correct 🎯 icon.

**Collapsed nav alignment.** In collapsed nav mode (≤ 768px), nav icons were appearing right-aligned within the 52px column. Fixed by adding `width: 100%` to collapsed nav links and explicit `grid-column: 1` to `.hive-nav`.

**Worker Ctx% always showed 0% during task execution.** `SendPromptAsync` was using the non-streaming `ExecuteAsync` path, which only updates `LastKnownContextTokens` when the entire prompt completes (potentially 5–15 minutes). Heartbeats fired every 30 seconds during that window always read a zero value. Fixed by switching to `ExecuteStreamingAsync` with `ShowToolCallsInStream = true`, which runs the tool-call loop manually and updates `LastKnownContextTokens` after every LLM turn — so heartbeat Ctx% values now reflect live context usage throughout execution.

## [0.7.1]

### Added

**Reviewer `get_test_report` tool.** The reviewer worker now has a `get_test_report()` tool it can call to actively retrieve the tester's structured report (build success, test counts, verdict), preventing spurious rejections when reviewers cannot verify build/test results.

### Changed

**GoalDetail tab labels.** Tab labels now say "Iteration N" instead of "Iter N".
**GoalDetail sticky tabs and phase bar.** Iteration tabs and phase bar are now sticky so only the content area scrolls.
**GoalDetail timeline smart scroll.** Timeline only auto-scrolls to the bottom when new entries arrive; users can scroll up to read older content without being snapped back.
**GoalDetail progress entry layout.** Progress entry layout cleaned up — role badge and status badge appear on the same line; phase label and worker ID removed for a cleaner look.
**GoalDetail release label inline.** Release label moved inline into the metadata row, aligned with status badge, priority, and scope.
**GoalDetail iteration tab spacing.** Iteration tabs now sit flush against the content below with no extra spacing.

### Fixed

**Build success state persists after goal completion.** Build success state now correctly persists after a goal completes — previously it always showed a red failure indicator after goal completion.
**Failed phase auto-selected on tab click.** Clicking a failed iteration tab now automatically selects the failed phase, immediately showing the failure detail.
**Phase detail no longer shows redundant progress reports table.** Phase detail panel no longer shows a redundant progress reports table — only clarifications remain.
**GoalDetail metadata row spacing.** Metadata row now has proper spacing between items so labels don't crowd each other.

## [0.7.0]

### Added

**GoalDetail page redesigned with 3-panel layout.** The Goal Detail page has been restructured into a top metadata strip, a two-column body (left: description/notes/failure info; right: iterations), and a bottom action strip. Iterations are now displayed as horizontal tabs instead of a vertical stack, with colour-coded tab states (green for completed, red for failed, blue for active). When no phase is selected, a live progress timeline shows a chronological feed of progress reports and clarifications from all phases of the current iteration, with auto-scroll.

**Progress reports are now phase and iteration aware.** Progress reports (`report_progress` tool calls) are stored per pipeline rather than in a global circular buffer. Each entry carries the phase name and iteration number, so the dashboard can display progress history for all phases — including completed ones — not just the currently active phase. Clarification entries in the timeline also carry phase and iteration attribution.

**Worker `get_goal` tool is now parameterless.** Workers can call `get_goal` with no arguments to recover the current goal's description after context compaction. The goal ID is injected by the worker runtime, preventing workers from accidentally fetching a different goal (e.g. a predecessor). The `IAgentRunner` interface gained `SetCurrentGoalId()` and `TaskExecutor` calls it when wiring up tasks.

**Parallel goal dispatch.** Goals now execute concurrently up to a configurable `MaxParallelGoals` limit. Each goal runs with its own Brain session forked from a shared master session, allowing multiple goals to progress simultaneously without blocking on each other. When a goal completes, a summary is merged back into the master session so accumulated learnings are retained.

**Composer multi-model support.** The Composer can switch between available LLM models via a dropdown in the chat UI. The model selection is persisted and applied to all subsequent Composer calls. Available models are read from the `hive-config.yaml` configuration.

**Composer context status bar.** A context usage indicator in the Composer chat footer shows the current context window utilisation percentage and displays a live "compacting…" status when context compaction is running.

**Composer goal creation pre-flight checklist.** The Composer's system prompt now includes a checklist of verification steps to run before creating a goal (file existence, repository assignment, code reference accuracy, worker capability constraints, scope sizing) and a policy requiring explicit user approval before dispatching any goal.

**Worker resilient reconnect.** Workers now use an exponential-backoff retry loop when the orchestrator is unavailable at startup, making the system resilient to container startup ordering. Previously, workers would crash if the orchestrator was not yet ready.

**Goals page filter persistence.** The Goals page filter settings (status, priority, repository, release) are now persisted across navigation so they survive page transitions and browser refreshes. A reset button clears all filters.

**Goals page sticky header.** The filter bar and table headers on the Goals page remain pinned while scrolling through goal rows.

### Changed

**Per-goal Brain sessions.** Each goal now receives its own Brain session forked from a persistent master session, replacing the previous single shared session. This prevents context from one goal polluting another, while still allowing the Brain to accumulate learnings across goals via the master session.

**Worker report summary as authoritative output.** Workers' `report_*` tool call summaries are now used as the canonical output across the pipeline (stored in `PhaseOutputs`, shown in the dashboard). Previously, narrative text responses were used, which were less structured.

**Reviewer receives coder output.** The reviewer's prompt now includes the coder's output from the current iteration, giving the reviewer context about what was implemented before inspecting the diff.

**Prompt injections standardised with fenced blocks.** All prompt construction in `DistributedBrain.cs`, `GoalPipeline`, and worker prompts now wraps injected content in fenced delimiters to prevent prompt injection attacks and improve LLM parsing.

**Acceptance criteria verification in hardcoded prompts.** The Reviewer and Tester hardcoded system prompts now include mandatory acceptance criteria verification blocks, instructing them to always read the full goal description and verify every criterion is met — not just that tests pass.

### Fixed

**Worker crashes now fail the goal immediately.** When a worker task returns `TaskOutcome.Failed` (infrastructure failure, unhandled exception), the goal is now immediately marked as failed rather than silently retrying or hanging.

**Missing worker report treated as failure.** When a worker completes its session without calling the mandatory report tool (`report_test_results`, `report_review_verdict`, etc.), the phase is now treated as a failure rather than a silent pass.

**Pipeline store cleanup on goal reset.** When a failed goal is reset to Draft, any out-of-memory pipeline state is now properly cleaned up from the pipeline store, preventing stale state from interfering with the next dispatch.

**Composer model dropdown contrast.** Fixed the model selector dropdown in the Composer chat UI being illegible in dark theme due to insufficient contrast between text and background colours.

**Dashboard elapsed time display.** Fixed the elapsed time display in the Active Goals table to freeze at the final value when goals complete, rather than continuing to increment.

## [0.6.0]

### Added

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
