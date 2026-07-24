## [Unreleased]

## [0.16.0] - 2026-07-24

### Added

- **Release Automation** — Per-repository release configuration with automatic merge of the working branch to a configurable target branch (e.g. `main`) and version tagging when a release is marked as Released. Pre-release validation ensures all goals are completed and repositories are configured. Rollback support deletes created tags on failure (merges are not reverted). Dashboard shows a "Validate" button with validation feedback, per-repository merge/tag results, and disables the "Unrelease" button since git operations are irreversible. (`copilothive-release-config`, `copilothive-release-git-ops`, `copilothive-release-execution`, `copilothive-release-dashboard`)

- **Repository Branch Dropdowns** — The repository configuration edit form now uses dropdowns populated from actual remote branches instead of text inputs for default branch, release merge target, and release tag branch. Branches are fetched on-demand when the edit form opens. (`copilothive-repo-branch-dropdown`, `copilothive-default-branch-dropdown`)

### Fixed

- **Parallel Goal Execution Regression** — Fixed a regression where session management operations unnecessarily held the Brain's LLM call gate (`_brainCallGate`), blocking the dispatch loop and preventing parallel goal execution. `ForkSessionForGoalAsync` and `RegisterExistingGoalSession` were moved to a separate lightweight `_sessionLock` that doesn't block Brain LLM calls. `DeleteGoalSession` still acquires `_brainCallGate` (since it mutates active session state) followed by `_sessionLock`, but this doesn't block parallelism since it's called during goal completion, not during dispatch. (`copilothive-fix-parallel-execution-regression`)

- **Nothing-to-Commit Error** — Fixed "Update failed: git exited with code 1: nothing to commit, working tree clean" error when saving a repository edit without making changes. `CommitFileAsync`, `CommitAllChangesAsync`, and `DeleteFileAsync` now check for staged changes before committing. (`copilothive-fix-nothing-to-commit`)

- **Validation Feedback** — Added visible "✅ Validation passed — ready to release" message when release validation succeeds. (`copilothive-fix-validation-feedback`)

- **Validation List Indentation** — Fixed validation error bullet list touching the red card border on the Release Detail page. (`copilothive-fix-validation-list-indent`)

- **Repository Action Buttons** — Fixed Edit and Remove buttons in the repository configuration table stacking vertically; now horizontal with gap. (`copilothive-fix-repo-action-buttons`)

## [0.15.0] - 2026-07-23

### Added

- **Pre-Execution Goal Review** — An optional pre-execution review process where a capable model reviews goal descriptions before dispatch. The reviewer has full access to the goal, linked knowledge documents, and the source code. Produces a verdict (Approved/NeedsChanges) and a review document in the knowledge graph. The Composer can trigger reviews via a `review_goal` tool and automatically acts on `NeedsChanges` feedback by reading the review document and updating the goal. Dashboard shows review status badge, "Review Goal" button, and a warning on the Approve button when changes are needed. (`copilothive-goal-review-status-field`, `copilothive-goal-review-execution`, `copilothive-composer-review-tool`, `copilothive-goal-review-dashboard`)

- **LLM Session Dashboard** — A unified view of all active LLM sessions in the orchestrator container (Brain master, Brain per-goal, Composer, Goal Review) on the Orchestrator dashboard page. Shows session type, associated goal, model, context usage bar, status, and last activity time. Uses a thread-safe in-memory registry with `LlmSessionType` enum for type safety. (`copilothive-llm-session-registry`, `copilothive-llm-session-integration`, `copilothive-llm-sessions-dashboard`)

- **Linked Documents Above Description** — Linked knowledge documents (progress documents, review documents) now appear above the goal description on the Goal Detail page. (`copilothive-move-linked-docs-above-description`)

### Fixed

- **STATE_DIR Environment Variable Races** — Fixed CI test failures caused by process-wide `STATE_DIR` races between test collections. `ProgressDocumentTests` added to `HiveIntegration` collection, all test factories save/restore previous `STATE_DIR` instead of clearing to `null`. (`copilothive-fix-state-dir-env-var-cleanup`)

- **LLM Session Icons** — Fixed swapped Brain and Composer icons in the LLM Sessions table. Replaced stringly-typed `SessionType` with `LlmSessionType` enum for compile-time safety. (`copilothive-fix-llm-session-icons`, `copilothive-fix-brain-session-icon`)

## [0.14.0] - 2026-07-22

### Added

- **Goal Progress Narratives** — Workers now write reflective narratives (what they tried, what worked, what they struggled with) via a new `report_narrative` tool call. Narratives are stored on the pipeline and appended to a living progress document in the knowledge graph. The progress document is created when a goal is dispatched, linked to the goal via the `documents` field, and updated with the Brain's iteration plan, worker narratives, and Brain summary after each phase. The Composer can read progress documents to answer human questions about goal execution. (`copilothive-worker-narrative-rpc`, `copilothive-progress-document-lifecycle`)

- **`get_current_time` Tool** — Added a `get_current_time` tool to the Brain and Composer that returns the current UTC date, time, ISO timestamp, and timezone. The LLM calls it on demand when it needs the date for changelog entries, release notes, or other date-sensitive content. (`copilothive-get-current-time-tool`)

- **Linked Documents on Goal Detail Page** — The goal detail page now shows a "Linked Documents" section with clickable links to knowledge graph documents attached to the goal, including progress documents. (`copilothive-goal-detail-documents`)

- **Brain Progress Document Guidance** — The Brain's hardcoded system prompt now instructs it to read the goal's progress document before planning new iterations, giving it access to worker narratives and qualitative context beyond structured phase outputs. (`copilothive-brain-progress-document-guidance`)

- **Improver Progress Document** — The improver's prompt now includes the goal's progress document content, giving the improver qualitative context (worker narratives, brain plan, brain summary) for making targeted AGENTS.md improvements instead of relying only on quantitative metrics. The improver system prompt also now explicitly discourages changelog-style "Iteration History" entries in agents.md files. (`copilothive-improver-progress-document`)

### Changed

- **NuGet Package Updates** — Updated 12 packages in CopilotHive and 9 in SharpCoder to their latest stable versions, including Microsoft.Extensions.AI.OpenAI 10.8.1, Microsoft.NET.Test.Sdk 18.8.1, YamlDotNet 18.1.0, and System.CommandLine 2.0.10. (`copilothive-update-nuget-packages`)

- **Progress Document Title** — Simplified to use the goal ID directly (`Progress: {goalId}`) instead of attempting to extract a title from the goal description. (`copilothive-fix-progress-document-title`)

- **Progress Document Formatting** — Added blank lines after section headings (Brain Plan, narratives, Brain Summary) and between Phases/Reasoning for improved readability. (`copilothive-fix-progress-document-formatting`)

### Fixed

- **Config Repo Git Conflict Recovery** — `ConfigRepoManager.CommitFileAsync` now recovers from `git pull` merge conflicts by aborting the merge, resetting, and retrying with rebase. A `ResetToRemoteAsync` method allows `DispatcherMaintenance` to auto-recover when the config repo is stuck in a broken state. Previously, a single conflict would break all config repo operations for the entire session. (`copilothive-config-repo-conflict-recovery`)

- **Brain Context Usage Logging** — Fixed misleading context usage percentage that used cumulative input tokens instead of the current session token estimate (e.g., showing 155% when actual usage was 17%). (`copilothive-fix-brain-context-usage-logging`)

- **Console Output Test Isolation** — Added missing `[CollectionDefinition("ConsoleOutput")]` so xUnit serializes tests that redirect `Console.Out`/`Console.Error`, preventing cross-test output leakage. (`copilothive-fix-console-output-collection`)

- **Progress Document Test CI** — Fixed `ProgressDocumentTests` failing in CI due to `STATE_DIR` defaulting to `/app/state`. (`copilothive-fix-progress-test-state-dir`)

## [0.13.0] - 2026-07-17

### Added

- **Available Models Management** — Users can now add, edit, and remove models from the `models.available_models` list via the Configuration page. A "Browse Provider Models" button queries GitHub Copilot (`GET https://api.githubcopilot.com/models`) and/or Ollama (`GET /api/tags`) for available models, auto-filling model names and context windows. New `ModelEntry.ReasoningEffort` field allows per-model reasoning effort configuration (none/low/medium/high/extra_high) instead of using `:suffix` in model names. (`copilothive-available-models-management`)

- **Full In-App Configuration** — All `hive-config.yaml` settings are now editable from the dashboard. New tabs for Repositories (add/edit/remove with auto-clone), Orchestrator settings (max_iterations, max_retries, max_parallel_goals, always_improve, verbose_logging, brain_max_steps, branch_cleanup_delay_hours), Worker context windows (per-role), and Composer settings (max_steps). Changes are written back to `hive-config.yaml` and hot-reloaded. (`copilothive-full-config-management`)

- **GitHub OAuth Authentication** — Single-user GitHub OAuth authentication. When `GITHUB_OAUTH_CLIENT_ID` and `GITHUB_OAUTH_CLIENT_SECRET` environment variables are set, all dashboard pages and REST endpoints require authentication. The first GitHub user to sign in becomes the admin. The OAuth access token is stored in the database (`users` table) and used for Copilot API access, eliminating the need for `GH_TOKEN`. When OAuth env vars are not set, the system runs in "open mode" (backward compatible). Login page with "Sign in with GitHub" button, logout, user profile (avatar + username) in nav bar. (`copilothive-github-oauth-backend`, `copilothive-github-oauth-ui-v2`)

- **Composer Session Compaction** — Two new buttons in the Composer chat: "Compact" (full compaction via `ForceCompactAsync`) and "Compact 50%" (partial compaction via `CompactOldestPercentAsync` from SharpCoder 0.11.0). The partial compaction summarizes only the oldest 50% of tokens, keeping the newest 50% verbatim — gentler than full compaction. (`copilothive-composer-compact-button`, `copilothive-composer-compact-partial`)

- **SharpCoder 0.11.0** — Upgraded from 0.10.0, adding `CompactOldestPercentAsync` for partial context compaction. (`sharpcoder-partial-compaction`, `sharpcoder-bump-version-0110`, `sharpcoder-v0110-changelog-readme`)

### Changed

- **Model Dropdowns Show Reasoning Effort** — All model dropdowns (Configuration page Models tab and Composer chat) now display reasoning effort (e.g., `copilot/claude-sonnet-4.6 (high)`) and use composite `name:effort` values so the selected model matches the config and reasoning effort is preserved on save. (`copilothive-fix-model-dropdowns-reasoning`, `copilothive-fix-composer-model-dropdown`)

- **Context Window Resolution Fix** — `HiveConfigFile.TryGetContextWindowForModel` and `TryGetReasoningEffortForModel` now strip known reasoning suffixes before matching against `AvailableModels`, fixing incorrect context window percentages (e.g., 330% instead of 27%) after restart. (`copilothive-fix-context-window-suffix-lookup`)

### Removed

- **Legacy goals.db Migration Code** — Removed ~250 lines of `MigrateGoalsDatabase` and helper methods. All installations have been updated to v0.12.0. (`copilothive-remove-legacy-goalsdb-migration`)

- **Redundant Context Window Fields** — Removed `OrchestratorConfig.BrainContextWindow`, `OrchestratorConfig.WorkerContextWindow`, and `ComposerConfig.ContextWindow` — these were fallbacks from before per-model context windows were available via `AvailableModels`. Resolution now falls from model-specific directly to `DefaultBrainContextWindow` (150K). Per-role `WorkerConfig.ContextWindow` is preserved. (`copilothive-remove-redundant-context-windows`)

### Fixed

- **Available Models Bugs** — Reasoning suffix stripping on startup, browse modal scrollable list with fixed buttons, URL-encoded model name in PUT/DELETE endpoints. (`copilothive-fix-available-models-bugs`)

- **CI Test Failure** — `AvailableModelsEndpointTests` failed in CI because `CustomEndpointFactory` didn't set `STATE_DIR` environment variable. (`copilothive-fix-available-models-ci`)

## [0.12.0] - 2026-06-25

### Added

- **SharpCoder 0.10.0 with Chunked Compaction** — SharpCoder upgraded from 0.9.0 to 0.10.0, adding `AgentOptions.CompactionMaxTokens` for chunked compaction. When old messages exceed the compaction model's context window, they are split into token-budgeted chunks, each summarized separately, and concatenated. CopilotHive wires `CompactionMaxTokens` from `HiveConfigFile.TryGetContextWindowForModel` in DistributedBrain, Composer, and workers (via `task.Metadata["compaction_max_tokens"]`). (`copilothive-bump-sharpcoder-0-10-0`)

- **Pre-Migration Database Backups** — Both `GoalStore` and `PipelineStore` now create timestamped SQLite backups via `SqliteConnection.BackupDatabase()` before any schema changes. Backups stored in `backups/` subdirectory, last 10 retained. In-memory databases skip backup. (`copilothive-db-backup-before-migration`)

- **Entity Framework Core Persistence** — CopilotHive's persistence layer migrated from raw ADO.NET (hand-written SQL) to EF Core with SQLite. A single `CopilotHiveDbContext` manages all tables (goals, releases, iterations, pipelines, conversations, task_mappings) in a single `copilothive.db` file. `GoalStore` and `PipelineStore` both use `IDbContextFactory<CopilotHiveDbContext>`. The legacy `goals.db` is automatically migrated to `copilothive.db` on first startup. (`copilothive-ef-core-dbcontext`, `copilothive-fix-goalstore-abstraction`, `copilothive-rewrite-goalstore-ef-core`, `copilothive-rewrite-pipelinestore-ef-core`)

- **EF Core Schema Reconciliation** — Startup now reconciles the database schema by creating missing tables via `EnsureSchemaUpToDate` (using `GenerateCreateScript()` + `CREATE TABLE IF NOT EXISTS`) and applying pending EF Core migrations via `Database.MigrateAsync()`. This makes upgrades safe on existing databases and enables restoring backups from older versions. (`copilothive-fix-schema-evolution-crash`, `copilothive-ef-core-migrations`)

- **Backup & Restore** — `BackupService` creates tar.gz archives containing the database (`copilothive.db`), Brain session files (`brain-master.json`, `brain-goal-*.json`), Composer session (`composer-session.json`), metrics, and data protection keys. Backups are downloadable via REST API (`POST /api/backup`, `GET /api/backup`, `GET /api/backup/{filename}`) and a "Backup" tab on the Configuration dashboard page. Restore via `POST /api/backup/restore` creates a safety backup before replacing files. Old backups pruned to last 10. (`copilothive-backup-feature`, `copilothive-restore-feature`)

- **Brain Worker Context Boundary** — The Brain's system prompt now explicitly states that workers have per-role sessions and cannot see other roles' output. The Brain must include specific rejection reasons and test failure details in worker prompts — never "see previous output". (`copilothive-brain-worker-context-prompt`)

- **Brain Branch Visibility** — The Brain's system prompt now explains that its file tools see the base branch, not the worker's feature branch. Worker changes are NOT lost on retry — the Brain should plan fixes, not full reimplementations. (`copilothive-brain-branch-visibility`)

### Changed

- **Network Timeout Fix** — `OpenAIClientOptions.NetworkTimeout` set to 30 minutes in `ChatClientFactory.CreateCopilotClient`, preventing `TaskCanceledException` from the default 100-second SSE stream read timeout during long streaming LLM calls. (`copilothive-fix-network-timeout`)

- **Pipeline Driver Retry Context** — `HandleNewIterationAsync` now includes actual tester/reviewer output from the previous iteration's `PhaseLog` in the `additionalContext`, instead of the vague "Test failures: see previous output." message. Output truncated to 3000 chars. (`copilothive-fix-pipeline-driver-context`)

- **Code Quality: Split Program.cs** — REST endpoint definitions extracted to `ApiEndpoints.cs`, database migration helpers to `DatabaseMigration.cs`. `Program.cs` now contains only DI registration and startup orchestration. (`copilothive-split-program-cs`)

- **Code Quality: Split HiveConfigFile.cs** — Config section classes (`RepositoryConfig`, `WorkerConfig`, `OrchestratorConfig`, `ModelsConfig`, `ComposerConfig`) extracted into separate files. (`copilothive-split-hiveconfigfile`)

- **Code Quality: Split DashboardStateService** — Progress report methods extracted into `ProgressReportService`. (`copilothive-split-dashboard-state-service`)

## [Unreleased]

## [0.11.0] - 2026-04-14

### Added

- **Configuration Hot-Reload** — `HiveConfigFile.ReloadFrom(fresh)` copies updated properties onto the live singleton after each `SyncRepoAsync` cycle, so `hive-config.yaml` changes take effect without an orchestrator restart. `DispatcherMaintenance.SyncAgentsFromConfigRepoAsync` triggers the reload after `LoadConfigAsync` re-reads from disk. The `GET /api/config/models` endpoint reflects the new values within one sync cycle. (`copilothive-reload-config-on-sync`)

- **Live Brain System Prompt Reload** — When `orchestrator.agents.md` changes, `DistributedBrain.InjectOrchestratorInstructionsAsync` reloads `_systemPrompt` and calls `RecreateAgent()`. The master session is preserved. The legacy `OnCompacted` re-injection workaround has been removed — it was redundant because SharpCoder's `CodingAgent` rebuilds the system prompt every turn from `AgentOptions.SystemPrompt`. (`copilothive-brain-reload-agents-md`)

- **Live Brain Model Switching** — New `IDistributedBrain.UpdateModelAsync(model, maxContextTokens?, ct)` swaps the Brain's chat client, reasoning effort, and context window in place — no restart, no session loss. `ConfigModelService` invokes it automatically when the user saves a new orchestrator model from the Configuration page. The dashboard reflects the new model and context window within one refresh cycle (~3 seconds). (`copilothive-update-brain-model-on-config-change`)

- **Premium Worker Models** — Each `WorkerConfig` now has an optional `premium_model` field. When the Brain escalates a phase to the `"premium"` tier (via the `model_tiers` section of `orchestrator.agents.md`), the dispatcher uses the role's premium model for that phase only. The Configuration page shows a premium-model dropdown for each of the 5 worker roles; saving commits standard, premium, and compaction model changes in one PATCH. Falls back to the role's standard model when no premium is configured. (`copilothive-config-premium-models`)

- **Unified Model Lists** — `HiveConfigFile.GetComposerAvailableModels(fallback)` returns model names from the global `Models.AvailableModels` list. The Composer is constructed with this list, and `ComposerHub` endpoints serve it. `/api/composer/models/switch` validates against the global list. Eliminates the previous redundancy where `ComposerConfig.Models` and `Models.AvailableModels` were maintained independently. (`copilothive-composer-global-model-list`)

- **Model-Specific Context Window Resolution** — New `HiveConfigFile.TryGetContextWindowForModel(string)` looks up a model's context window in `Models.AvailableModels`. `GetContextWindowForRole` now falls back through a 4-step chain: per-role override → model-specific (from global list) → orchestrator default → built-in default. Brain and Composer initialization resolve context windows via the global model list. `Composer._maxContextTokens` is mutable and updates on `SwitchModelAsync` when switching to a model with a different context window. (`copilothive-global-model-context-windows`)

### Dependencies

- **SharpCoder upgraded to 0.9.0** — Both `CopilotHive.csproj` and `CopilotHive.Worker.csproj` now reference `SharpCoder` 0.9.0, picking up the new `ContextCompactor` deduplication (shared `CompactMessageSliceAsync` core method) and the system-message-preservation behavior that makes the `OnCompacted` re-injection workaround unnecessary. (`copilothive-update-sharpcoder-090`)

## [0.10.0] - 2026-04-13

### Added

- **In-App Model Configuration** — Users can now change model configuration from the Configuration dashboard page. A new "Models" tab provides dropdown selectors for the orchestrator (Brain), composer, per-role workers, and compaction model, populated from the `available_models` list in `hive-config.yaml`. Changes take effect on the next goal dispatch and are written back to `hive-config.yaml` via `ConfigModelService`. Backend includes: `ModelEntry`/`AvailableModels` config model, `ConfigRepoManager.WriteConfigAsync` for YAML round-trip serialization, `ConfigModelService` singleton for applying and persisting changes, and REST endpoints `GET /api/config/models` and `PATCH /api/config/models`.
- **Draft goal editing** — Draft goals are now fully editable via the Composer's `update_goal` tool. Description, priority, scope, repositories, depends_on, and documents can all be changed on Draft goals (previously only status and release were editable).
- **"Unrelease" button** — The release detail page now has an "Unrelease" button that reverts a Released release back to Planning, allowing edits and goal reassignment.
- **Automatic branch cleanup** — `DispatcherMaintenance` periodically deletes `copilothive/{goal-id}` feature branches from target repositories after a configurable delay once the goal is completed and merged.
- **Composer operating procedures in system prompt** — The Composer's system prompt now includes an explicit startup instruction to read `memory-composer-operating-procedures` and the idea-to-implementation transition convention, ensuring these procedures survive session resets.
- **Knowledge graph consultation guidance in Composer prompt** — The Composer's system prompt includes explicit guidance to proactively consult the knowledge graph during conversations.
- **Tokenized multi-term search** — Both `KnowledgeGraph.Search()` and `GoalStore.SearchGoalsAsync()` now use tokenized multi-term matching (AND logic) instead of single contiguous substring matching. Queries are split on whitespace, hyphens, underscores, and punctuation, so `"docker worker"` matches `remove-docker-worker-dead-code` and `"idea to implementation"` matches documents containing `"Idea-to-Implementation"`. Document IDs are also now searchable.
- **Content snippets in `list_documents`** — The `list_documents` tool now shows a 200-character content snippet for each document (matching `search_knowledge`), making it easier to scan and identify documents without calling `read_document` on each.
- **Improved `list_goals` descriptions** — The `list_goals` tool now truncates descriptions to 150 characters (up from 80), strips leading markdown heading markers, and replaces newlines with spaces for cleaner single-line display.

### Removed

- **Docker worker dead code** — Removed the unused Docker-based worker management code (`DockerWorkerManager`, `IWorkerManager`, `WorkerInfo`, `FakeWorkerManager`), the `DockerImage` and `BasePort` config fields from `OrchestratorConfig`, `DefaultBasePort` from `Constants.cs`, and the `Docker.DotNet` NuGet package. The system exclusively uses the gRPC-based worker architecture.

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

**Plan reason pinned above scrollable timeline.** The Brain's plan reason text (📝) was previously inside the scrollable `.iter-content` div, causing it to scroll out of view when the progress timeline filled with entries. It is now rendered above the scroll area as a sibling element, always visible.

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
