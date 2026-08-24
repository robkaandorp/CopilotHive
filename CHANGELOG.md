## [0.33.1] — 2026-08-24

### Fixed

- **GitHub OAuth "Correlation failed" on plain-HTTP non-localhost hosts** — when the dashboard is served over `http://` on a hostname other than localhost (e.g. an internal docker swarm / LAN), Chromium-based browsers refuse to store the OAuth correlation cookie (it carries the `Secure` attribute), breaking the sign-in handshake. The correlation cookie's `SecurePolicy` can now be relaxed by setting the new `ALLOW_INSECURE_OAUTH=true` environment variable. Only enable this on trusted internal networks — OAuth cookies are otherwise sent in the clear. Documented in the README Authentication section. (`fix-oauth-correlation-insecure-http`)

## [0.33.0] — 2026-08-22

### Added

- **NuGet publish monitoring** — `NuGetPublishMonitorService` polls the NuGet API after a release to verify packages have landed. Emits `PackagePublished`/`PackagePublishTimedOut` events. Per-repo `publish_nuget` config. Startup scan for releases completed while offline. `PackagePublished` is an opt-in active event.
- **Autonomous composer mode** — all 9 supported active-event types are now available as opt-in active events, with "Autopilot" (select all 9) and "Normal" (select 4 defaults) preset buttons on the configuration page, enabling full autonomous operation where the Composer runs in a loop driven by events.

### Fixed

- **Goals overview page auto-refresh** — the Goals page now subscribes to `DashboardStateService.OnStateChanged` and auto-updates without manual refresh.
- **CI monitor log extraction** — `ParseTestFailuresFromLogs` now strips GitHub Actions timestamp prefixes so structured issue creation works instead of falling back to the 500-char tail snippet.
- **Flaky `GoalActorTests.DisposeAsync_RunningActor_CancelsQueuedReplies`** — replaced the 1000-message flood with a deterministic `OnBeforeReadAsync` test gate in `Actor.cs`.
- **NuGet test CI failure** — `CreateNuGetApiHandler_HasGzipAndDeflateDecompression` now tests the handler factory directly instead of booting a full `WebApplicationFactory`.

## [0.32.0] — 2026-08-20

### Added

- **TargetRepositoryNames** — Goals can now specify which repos are editable targets vs read-only reference repos. Composer `create_goal` and `update_goal` tools support `target_repositories`. The merge worker only merges target repos. Brain prompts label target/source repos.
- **SharpCoder.Providers integration** — CopilotHive now uses the `SharpCoder.Providers` NuGet package (v0.17.0) instead of its own `ChatClientFactory`. Old `CopilotHive.Shared/AI/` code deleted. Provider tests migrated to SharpCoder.Providers.Tests.
- **GitHub OAuth `workflow` scope** — OAuth token now includes the `workflow` scope, allowing workers to push changes to GitHub Actions workflow files.

### Fixed

- **Worker removal ABA bug** — `WorkerPool.RemoveWorker` is now instance-aware. `WorkStream` binds to one exact `ConnectedWorker` instance, so stale streams can't evict replacement workers.
- **Issue store concurrency** — `IssueStore.UpdateIssueAsync` is serialized with a per-instance `SemaphoreSlim`, preventing overlapping database operations.
- **Stale review document** — `review_goal` resets the review document before each review call, preventing stale `NeedsChanges` verdicts from influencing the reviewer.
- **Progress document cleanup** — Progress document is reset when a goal is cancelled and re-dispatched, so no stale iteration content confuses the Brain.
- **Flaky tests** — `ComposerActorTests.OverflowRecoveryThrows` and `SendMessage_StreamsText` fixed with deterministic TCS synchronization.
- **ChatClient disposal** — Copilot `HttpClient`/handler chain is now owned by an `OwnedCopilotChatClient` decorator, with proper disposal and exception aggregation.

## [0.31.0] — 2026-08-19

### Added

- **Event bus startup scan** — On restart, the `EventBusStartupScanner` queries recent state changes (goals completed/failed, issues raised/resolved, releases completed) since the last Composer session activity and pre-populates the `ComposerEventSubscriber` buffer. Uses `AgentSession.LastActivityAt` as the cutoff with a 60-minute fallback. (`copilothive-event-bus-startup-scan`)
- **Composer actor architecture (Phase 1)** — Extracted `ComposerActor` using the `Actor<TMessage>` mailbox pattern. The actor owns the streaming loop, CTS, and streaming state. Streaming and session mutation operations are serialized through the actor mailbox. (`copilothive-composer-actor-phase1`)
- **Composer actor architecture (Phase 2)** — Added compaction callbacks (exactly-once, no duplication from `ContextCompactor`), `SessionLoadedFromDisk` caching, `SubmitAnswer`/`CancelQuestion` through the actor with synchronized `PendingQuestion`. (`copilothive-composer-actor-phase2`)
- **Active event bus mode** — When significant system events occur (goal completed, goal failed, CI failed, issue raised), the Composer automatically receives a `[System Notification]` message and can take autonomous action. The `ActiveEventInjector` subscribes to `IEventBus`, filters by configured event types, and `Tell`s the `ComposerActor` — the actor queues notifications while streaming and launches them from the terminal handler. Includes throttling and batching. Configurable via `composer.event_notifications` in `hive-config.yaml`. (`copilothive-active-mode-actor`, `copilothive-active-mode-injector`, `copilothive-active-mode-config-ui`)
- **Active mode configuration UI** — Toggle button in the Composer chat window (🔔 Active / 🔕 Passive / 🔕 Off) and a Configuration page section for detailed settings (mode, active events, throttle seconds). Shows "⚠️ Restart required" when toggled. (`copilothive-active-mode-config-ui`)
- **CI monitor log fetching** — When CI fails, `CiMonitorService` fetches actual test failure logs from the GitHub Actions API. Auto-created issues now contain the test name, error message, and stack trace instead of just a check run name and URL. Includes secret sanitization, bounded streaming, per-runId caching, and redirect-safe authentication. (`copilothive-fix-ci-monitor-tests`)

### Fixed

- **Reasoning effort mapping** — `ReasoningEffort.ExtraHigh` now maps to provider-specific values: `"xhigh"` for Copilot, `"max"` for Ollama (via `OllamaExtraHighReasoningClient` using `ChatOptions.RawRepresentationFactory`), and clamped to `High` for GitHub Models. Previously `"extra_high"` was sent to all providers but not recognized by any. (`copilothive-fix-reasoning-effort-mapping`)
- **Streaming gate CI failures** — Fixed CI test failures caused by `[Conditional("DEBUG")]` on a test seam (compiled out in Release) and streaming gate ordering in compact methods. (`copilothive-fix-streaming-gate-order`)
- **CI test flakiness** — Fixed overflow recovery test race and flaky startup log test (replaced timing-based `Task.Delay` with `SignalingLogger` + TCS). (`copilothive-fix-ci-failures-2`)
- **Stale worker reclamation** — `StaleWorkerCleanupService` now uses `LastActivityAt` (last task-specific WorkStream message) instead of `CurrentTaskStartedAt` (wall clock). Active workers are no longer reclaimed when their total task duration exceeds the timeout. Heartbeats don't count as activity (preserves hung-call detection). (`copilothive-fix-stale-worker-activity`)

## [0.30.0] — 2026-08-17

### Added

- **CI Monitoring** — CopilotHive optionally monitors CI status after goal merges via the GitHub check-runs API. On success, a `CiSucceeded` event is published to the Composer event bus. On failure, issues are automatically created from test failures and a `CiFailed` event is published. Configurable per repository via `monitor_ci` and `ci_timeout_minutes` in `hive-config.yaml` or the Configuration dashboard page. Includes startup scan for restart recovery. (`copilothive-ci-monitor-config`, `copilothive-ci-monitor-service`, `copilothive-ci-monitor-startup-scan`, `copilothive-ci-monitor-config-ui`)

### Fixed

- **.gitignore hygiene** — Added `*.log` and `*.trx` patterns to `.gitignore` to prevent generated test/build output from being committed. (`copilothive-fix-gitignore-log`)
- **CI Monitor edit layout** — Fixed the CI Monitor column layout in the Configuration page's repository edit row. Checkbox, timeout input, and "min" label now stay on one line via flex layout. (`copilothive-fix-ci-monitor-edit-layout`)

## [0.29.0] — 2026-08-16

### Added

- **Composer Event Bus** — A typed event bus (`IEventBus` / `EventBus`) that broadcasts system events (goal completed, goal failed, goal dispatched, issue raised, issue resolved, release completed) to subscribers. The `ComposerEventSubscriber` buffers events and delivers them to the Composer as a passive notification — pending events are prepended as a `[System Events since your last message]` block to the user's next chat message, shown as a muted "System" message in the chat UI. Events are produced by `GoalLifecycleService` (goal completed/failed), `GoalDispatchService` (goal dispatched), `ApiEndpoints` (issue raised/resolved, release completed), `HiveOrchestratorService` (worker-raised issues), `BrainTools` (Brain-raised issues), and `ComposerIssueTools` (Composer-created issues). (`copilothive-event-bus-core`, `copilothive-event-bus-api-producers`, `copilothive-event-bus-tool-producers`, `copilothive-event-bus-chat-ui`)

### Changed

- **NuGet dependencies updated** — Updated SharpCoder to 0.16.1 and all other NuGet packages to their latest stable versions. (`copilothive-update-nuget-v029`)

### Fixed

- **STATE_DIR test race** — `ProgressDocumentTests` intermittently failed because tests in different xUnit collections ran in parallel and could overwrite the process-wide `STATE_DIR` environment variable. Fixed by setting `maxParallelThreads: 1` in `xunit.runner.json` to fully serialize all tests. (`copilothive-fix-progressdoc-telemetry-isolation`)
- **PlanRejectContractTests polling race** — The test's polling loop exited on the `InProgress` status update before `FailNewGoalAsync` recorded the `Failed` update. Fixed by replacing the poll with a `TaskCompletionSource` for deterministic synchronization on cleanup completion. (`copilothive-fix-planreject-test-timeout`)

## [0.28.1] — 2026-08-15

### Added

- **Goal-to-issue backlinks** — The Goal Detail page now shows a "Linked Issues" card listing issues that reference the goal (via `SourceGoalId` or `LinkedGoalId`), with title, status badge, type, and severity. Issues are deduplicated by ID. The `GET /api/issues` endpoint and `IIssueStore.GetIssuesAsync` now support a `linked_goal_id` filter parameter. (`copilothive-goal-detail-issue-backlinks`)

### Fixed

- **Issue title column overflow** — Long issue titles (e.g. full unit test names) no longer push table columns off-screen. Titles are constrained to 300px with ellipsis truncation and a hover tooltip showing the full text. (`copilothive-fix-issues-overview`)
- **Closed issues hidden by default** — The Issues page status filter now defaults to "Active (excl. closed)" instead of "All statuses". Closed issues only appear when the user explicitly selects "Closed" in the filter. (`copilothive-fix-issues-overview`)
- **IssueStore persistence-error conflation** — `IssueStore.CreateIssueAsync` now only wraps primary-key constraint violations as `InvalidOperationException` (the documented duplicate-ID signal). Other `DbUpdateException`s propagate as 500 errors instead of being misleadingly reported as 409 "already exists". (`copilothive-fix-issuestore-persistence-conflation`)
- **GoalDetail double refresh** — `GoalDetail.razor` no longer calls `RefreshAsync` twice on initial render, eliminating duplicated store queries and API requests on first load. (`copilothive-fix-goaldetail-double-refresh`)

## [0.28.0] — 2026-08-14

### Added — Issue Tracking System

- **Issue data model & persistence** — `Issue` entity with `IssueType`, `IssueSeverity`, `IssueStatus` enums, EF Core configuration, and `IssueStore` with full CRUD. (`copilothive-issue-data-model`)
- **Worker `raise_issue` tool** — Workers can raise issues via the existing gRPC tool-call mechanism. (`copilothive-issue-worker-tool`)
- **Brain `raise_issue` tool** — The Brain can raise issues during planning or review when it notices problems out of scope for the current goal. (`copilothive-issue-brain-tool`)
- **REST API for issues** — `GET`, `POST`, `PATCH`, `DELETE` endpoints under `/api/issues` with filtering by type, severity, status, and repository. (`copilothive-issue-api`)
- **Issues dashboard page** — `/issues` page with filterable table, detail expansion, triage controls (status/severity/type dropdowns), and inline create form. (`copilothive-issue-dashboard`)
- **Composer issue-management tools** — `create_issue`, `list_issues`, `get_issue`, `update_issue` tools so the Composer can manage issues conversationally. (`copilothive-issue-composer-tools`)
- **Goal-issue linking** — `update_issue` supports `linked_goal_id` with tri-state semantics (null=unchanged, empty=clear, non-empty=set). (`copilothive-issue-composer-linked-goal`)

### Fixed

- **Issue detail dropdown preselection** — Triage dropdowns in the Issues page now show the issue's actual values instead of the first/last option. Root cause was `ToSnakeCase(Enum)` serializing via the abstract `System.Enum` type (converter never applied, all values became `{}`). Fixed by making the helper generic. Also includes concurrent `LoadIssues` protection via generation counter and single-expansion editing. (`copilothive-fix-issue-dropdowns`, `copilothive-fix-issue-dropdowns-v2`, `copilothive-fix-issue-dropdowns-v3`, `copilothive-fix-issue-dropdowns-root-cause`)
- **No-op retry iteration display** — Iterations where the coder produces no file changes now persist their iteration summary before the retry, so the dashboard shows correct iteration data. (`copilothive-fix-noop-retry-iteration-display`)
- **Flaky merge-test CI failure** — `ResetToRemoteAsync_AbortsActiveMerge` test no longer depends on human-readable `git status` output containing "unmerged". (`copilothive-fix-merge-test-ci-flaky`)
- **Flaky cancelled-commit test** — `SaveModelConfigAsync_LiveTokenCancelledDuringCommit` no longer races between cancellation and `ReleaseGate.TrySetResult`. Removed the `TrySetResult` call and added a bounded timeout. (`copilothive-fix-cancelled-commit-test`)
- **Test parallelism crashes** — Added `xunit.runner.json` with `maxParallelThreads: 4` to prevent `BadImageFormatException`/SIGBUS crashes under high parallel load. (`copilothive-fix-test-parallelism`)

## [0.27.1] — 2026-08-14

### Fixed

- **Release JSON deserialization error** — The global `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)` added in v0.27.0 changed the server's wire format for unannotated enums (e.g. `ReleaseStatus.Released` → `"released"`), but several Razor pages deserialized responses without the matching converter, causing "The JSON value could not be converted to ReleaseStatus" errors when marking a release as Released. Fixed by creating a shared `JsonSerializerOptions` with the converter in `CopilotHive.Dashboard`, used by all affected pages (`ReleaseDetail.razor`, `Releases.razor`, `ComposerChat.razor`, `Configuration.razor`). (`copilothive-fix-release-json-deserialization`)

## [0.27.0] — 2026-08-14

### Added

- **Per-assignment reasoning effort** — Reasoning effort (`none`/`low`/`medium`/`high`/`extra_high`) is now a separate config field on every model assignment (orchestrator, workers, premium workers, composer, sub-agent models) instead of being encoded as a `:suffix` in model names. Config validation at startup ensures every model assignment has a valid `reasoning_effort`. Breaking config change — existing configs must add `reasoning_effort` fields. No migration. (`copilothive-reasoning-config-schema`, `copilothive-reasoning-worker-transport`, `copilothive-reasoning-brain-composer`, `copilothive-reasoning-api-persistence`, `copilothive-reasoning-suffix-removal`, `copilothive-reasoning-overload-cleanup`, `copilothive-reasoning-enum-api`)
- **Reasoning effort in dashboard** — Configuration page now has separate reasoning effort dropdowns for every model assignment. Orchestrator page shows reasoning per role (standard + premium), per LLM session, and for Brain/Composer. Workers page shows reasoning per displayed model. Composer chat has an editable reasoning dropdown that live-updates the running Composer. (`copilothive-reasoning-dashboard-ui`, `copilothive-reasoning-session-dto`, `copilothive-reasoning-ui-fixes`)
- **Brain config repo access** — The Brain now has read-only `list_config_files` and `read_config_file` tools to access the config repo's `agents/*.agents.md` files, enabling accurate improver prompts based on the actual config repo content. (`copilothive-brain-config-repo-tools`)
- **Duplicate available_models crash fix** — `GetSubAgentModels()` no longer crashes on duplicate model names (last-wins). (`copilothive-reasoning-config-schema`)

### Changed

- **Reasoning effort is now an enum** — All API contracts, DTOs, and runtime code use `ReasoningEffort?` enum instead of `string?`. Global `JsonStringEnumConverter` with snake_case naming for JSON serialization. `string?` only in YAML config classes and gRPC proto. (`copilothive-reasoning-enum-api`)
- **Model names are plain** — Model names no longer carry `:reasoningEffort` suffixes anywhere in the system. Model option values in dropdowns are plain names. (`copilothive-reasoning-suffix-removal`)
- **Composer model switch requires reasoning** — `SwitchModelAsync` now requires both model and reasoning effort parameters. The Composer chat sends both together. (`copilothive-reasoning-enum-api`)
- **Example agents.md files renamed** — The project repo's `agents/*.agents.md` files renamed to `*.agents.md.example` to avoid confusion with the config repo's live files. (`a7e80f0`)

### Fixed

- **Failed-merge iteration display** — Iterations that pass review but fail the squash merge are now properly persisted and shown in the dashboard iteration tab bar. `PipelineDriver.HandleMergeFailureAsync` now marks the Merging phase as failed, builds/persists the iteration summary, then starts the retry. (`copilothive-fix-merge-fail-iteration-display`)

## [0.26.0] — 2026-08-04

### Added

- **Knowledge graph document lifecycle cleanup** — Automatic deletion of transient `progress-{goalId}` and `review-{goalId}` knowledge documents when they are no longer needed. Three cleanup triggers: release completion (deletes docs for all goals in a released release), goal deletion (deletes docs when a Draft or Failed goal is deleted), and a one-time startup sweep (deletes docs for orphaned goals or goals in already-Released releases). The `KnowledgeGraph` now has graph-wide method serialization (`_graphLock`), an atomic batch delete+commit API (`DeleteDocumentsAndCommitAsync`), case-insensitive ID/collection semantics, and a `ConfigRepoPath` property. `ConfigRepoManager` gains `PushOnlyAsync` for non-destructive push-on-no-diff and `--ignore-unmatch` for idempotent retry. (`copilothive-kg-graph-sync-and-persistence`, `copilothive-kg-doc-cleanup-service`, `copilothive-kg-doc-cleanup-on-release`, `copilothive-kg-doc-cleanup-on-goal-delete`, `copilothive-kg-doc-cleanup-startup-sweep`)
- **Knowledge dashboard folder view** — Documents are now grouped by topic in collapsible folder sections with per-folder document counts. Sub-topic folders nest inside topic folders. Expand/collapse state persists via localStorage. Archived documents are hidden by default and only shown when the status filter explicitly selects "Archived". (`copilothive-knowledge-folder-view`, `copilothive-knowledge-subtopic-folders`, `copilothive-knowledge-folder-order`)
- **Release deletion** — Planning releases with no attached goals can be deleted via a "Delete Release" button on the release detail page. `DELETE /api/releases/{id}` endpoint with atomic conditional delete (all preconditions in one `ExecuteDeleteAsync` — no TOCTOU race). Three checks: Planning status, no goals attached, not Executing. (`copilothive-release-delete-api`, `copilothive-release-delete-ui`)
- **Improver sub-agent file-write clarification** — The improver's hardcoded system prompt now explains that sub-agents can request file-write access via `enable_file_writes=true`, preventing the improver from unnecessarily asking the orchestrator to apply file edits. (`copilothive-improver-prompt-and-agents-edits`)

### Fixed

- **Banner print crash on closed console** — `PrintBanner()` in `Program.cs` no longer throws `ObjectDisposedException` when `Console.Out` is closed during `WebApplicationFactory` host creation in tests. The `ConsoleOutput` test collection now has `DisableParallelization = true` to prevent cross-collection console interference. (`copilothive-fix-banner-objectdisposed`)
- **Improver test environment dependency** — The former `ExecuteAsync_ImproverDefaultPath_ResolvesToDefaultConfigRepoAgentsDir` test no longer reads real agents.md files from the CI environment. Renamed to `ExecuteAsync_ImproverInjectedPath_ResolvesToAgentsDir` using an injected non-existent temp path. (`copilothive-fix-improver-test-env-dependency`)
- **Local Docker version display** — Local Docker builds now show `v<VersionPrefix>-local` (e.g. `v0.26.0-local`) in the dashboard footer instead of `v0.1.0-local`. Uses MSBuild's `VersionSuffix=local` mechanism instead of a hardcoded Dockerfile default. (`copilothive-fix-local-version-default`)

## [0.25.1] — 2026-08-02

### Fixed

- **Per-clone git identity for worker target-repo clones** — `GitOperations.CloneRepositoryAsync` now configures `user.email` and `user.name` locally after each fresh clone, preventing commit failures from missing git identity.

### Changed

- **Improver system prompt drops "tool call contracts" from the safety-constraints list** — The hardcoded Improver prompt now reads "git workflow, test requirements or output format compliance" instead of "git workflow, test requirements, output format compliance, or tool call contracts."

### Added

- **Improver general-guidance rule** — agents.md edits must contain GENERAL, broadly-applicable software-development patterns and coding-style conventions, not incident-specific lessons tied to particular files, classes, or past iterations.

## [0.25.0] — 2026-08-02

### Added

- **Composer-chat image/PDF attachments** — File picker and clipboard paste support for attaching images (PNG/JPG/JPEG/GIF/WEBP) and PDFs to Composer messages, with vision-capable sub-agent delegation via `start_sub_agent image_paths`. Singleton `ComposerAttachmentService` with single-gate lifecycle, 20-MiB cap, extension allowlist. Compact inline 📎 attachment trigger and pending chip. Clipboard paste streams JS→.NET blobs via `IJSStreamReference`. (`copilothive-composer-attach-service`, `copilothive-composer-attach-wiring`, `copilothive-composer-attach-ui`, `copilothive-attach-paste-image`, `copilothive-attach-picker-compact`)
- **Releases page redesign** — Older released releases collapse to a default window (always showing Planning); each repo's releases display on a single horizontally-scrollable row (fixing orphaned-card wrapping); compact "Show older" toggle button. (`copilothive-releases-collapse-older`, `copilothive-releases-horizontal-strip`, `copilothive-releases-toggle-size`)
- **Background-tasks panel UX** — Sub-agent result area is scrollable with markdown rendering; task text spans full header width. Panel defaults to collapsed on page load. (`copilothive-bg-tasks-scrollable`, `copilothive-bg-tasks-default-collapsed`)
- **Conditional Improve in planner** — The Brain's planning prompt now recommends the Improve phase only for retries or iterations with prior issues (review/test failures, rejections), instead of unconditionally. (`copilothive-brain-improve-conditional`)

### Changed

- **SharpCoder updated to 0.16.0** — Adds `SubAgentOptions.AdditionalImagesRoot` for resolving image paths outside the primary working directory.

### Removed

- **`always_improve` config property** — The dead `OrchestratorConfig.AlwaysImprove` property and its dashboard checkbox have been removed. The Improve phase behavior is governed by the Brain's R3/R4 prompt guidance, not this toggle. (`copilothive-remove-always-improve`)

### Fixed

- **CI improver config-repo path** — The worker improver config-repo path is now injectable (default `/config-repo`) so CI/non-Docker environments can run improver tests against per-test temp directories. (`copilothive-fix-config-repo-path-ci`)
- **Push-fail warning includes changed-file paths** — When a worker push fails, the orchestrator warning now includes repository-relative changed-file paths (NUL-delimited `git diff` parsing), not just a count. (`copilothive-push-fail-file-names`)
- **Read-only role push-fail false positive** — The misleading "push failed" warning is now suppressed for read-only roles (reviewer) that didn't change the branch. A moved-repo-with-zero-diff note warns instead. (`copilothive-readonly-no-push-warn`)

## [0.24.0] - 2026-08-01

### Breaking Changes

- **Reject-not-fix plan validation** — The pipeline now REJECTS invalid iteration plans instead of silently auto-fixing, reordering, or substituting a default plan. An actionable rejection reason is fed back to the Brain for a bounded replan (3 attempts). If no valid plan is produced within the budget, the goal FAILS. This replaces the former auto-adjust behavior where the system would insert missing phases and notify the Brain. Affected goals: `copilothive-plan-validator-reject-not-fix` (pure `ValidatePlanStrict` with block-based R1-R7 grammar), `copilothive-plan-reject-not-fix-loop` (wiring to `PlanResult.Failed` propagation across the 5-layer planning stack), `copilothive-plan-parser-reject-unknown-phases` (ingestion surfaces unknown/invalid phase names instead of silently dropping them), and `copilothive-brain-plan-grammar-prompt` (Brain's hardcoded planning prompt teaches R1-R7 + reject-and-replan).

### Added

- **Composer tooling enhancements** — `get_release` tool (returns release record + attached goals), `get_goal` enrichment (Release/Depends On/document-links summary lines), `list_goals` release-filter (defaults to dashboard-Unreleased semantics with labeled output), `search_goals` opt-in release filter (no hidden default — omitted means no filtering).
- **Sub-agent vision support** — Informational `SupportsVision` flag flows end-to-end through the sub-agent model catalog (`ModelEntry` → `SubAgentModelEntry` → gRPC → workers). Dashboard displays a tri-state control (Unset/True/False).
- **Composer Background-tasks panel** — Live sub-agent progress displayed in the Composer chat via SharpCoder 0.14.0's `SubAgentChanged` event, using a channel-fed consumer pattern with immutable snapshots.

### Fixed

- **Flaky CI endpoint tests** — `GoalDispatcher` hosted-service dispatch loop gated with `IsEnvironment("Testing")` check to prevent background dispatch from racing endpoint tests that create/delete Pending goals.
- **`ask_user` options JSON array** — Composer `ask_user` tool's `options` parameter changed from `string` to `string[]` for correct `AIFunctionFactory` JSON schema generation, with strict content validation (min 2 entries, no blanks, no duplicates, max 50).

## [0.23.0] - 2026-07-29

### Added

- **Sub-Agents (sub-sessions)** — the Composer, Brain, and all worker roles (coder, tester, reviewer, docwriter, improver) can now delegate self-contained subtasks (codebase exploration, verification sweeps, large-text summarization) to background sub-sessions via SharpCoder's `start_sub_agent` tool. Only a summary (plus status metadata) returns to the calling session, keeping its context clean. Sub-sessions run read-only by default and can never exceed the parent agent's capabilities (capability ceiling). Catalog flows from the orchestrator to workers over gRPC (`TaskAssignment.sub_agent_models`). Bumps SharpCoder to **0.13.1** (which includes the sub-agent runtime and the client-ownership leak fix).
  - Composer wiring (`ComposerAgentService`): 4 concurrent, gated on a configured model catalog + repo file access; system-prompt guidance.
  - Brain wiring (`GoalBrainActor`/`BrainActor`/`DistributedBrain`): delegates planning exploration; immutable catalog snapshot shared by prompt + options; 2 concurrent.
  - Worker wiring (`SharpCoderRunner` + gRPC catalog round-trip through `WorkTask` + `GrpcMapper`): all roles; per-prompt agent disposal; role capability ceilings (reviewer read-only, improver no-bash).
- **Sub-Agent model catalog** — new optional `models.sub_agent_models` config section curates which models are offered to sub-agents (falls back to `models.available_models` when unset). Each model entry gains a `description` field (strength/cost/speed hints shown to the LLM via `list_sub_agent_models`). Fully editable in the dashboard: a **Description** field in the Available Models tab and a new **Sub-Agent Models** tab (add/edit/remove, persisted to `hive-config.yaml`).

### Changed

- **Sub-agent catalog defaults** — a curated `sub_agent_models` entry that omits `context_window` (or `reasoning_effort`/`description`) now inherits the value from the same-named entry in `available_models` instead of surfacing `null`. Merging is per-field, case-insensitive, and never mutates config (`HiveConfigFile.GetSubAgentModels`).
- **`HiveConfigFile.ReloadFrom`** now preserves `ModelEntry.Description` and `Models.SubAgentModels` across live config reloads.

### Fixed

- **Configuration model tables layout** — the Available Models and Sub-Agent Models tables are wider (~1100px); model names no longer wrap and the Edit/Remove (and Save/Cancel) action buttons always render horizontally instead of stacking.

## [0.22.0] - 2026-07-28

### Added
- **Event-driven dispatch** — `GoalReadyNotifier` wake-up signal replaces pure polling for goal dispatch. When a goal transitions to `Pending` status (via Composer approve, API update, or goal creation), the polling loop wakes immediately instead of waiting for the 5-second interval. The `SemaphoreSlim`-based signal coalesces concurrent notifications and falls back to the 5-second timeout when no notification arrives. The polling loop remains the single consumer — no concurrent dispatch.
- **TaskCompletionService** — Extracted `HandleTaskCompletionAsync` from `GoalDispatcher` into a new `internal sealed TaskCompletionService`. All guards preserved: no pipeline, already Done/Failed, stale task. Phase transition via `PipelineDriver.DriveNextPhaseAsync`, exception handling, dashboard notification, and persistence.
- **GoalDispatchService** — Extracted `DispatchNextGoalAsync`, `DrainRedispatchQueueAsync`, and `CreateProgressDocumentAsync` from `GoalDispatcher` into a new `internal sealed GoalDispatchService`. All dispatch behaviors preserved: goal selection, status update, verification, repo sync, pipeline creation, brain fork, plan iteration, progress doc, prompt crafting, dispatch, persist. Redispatch queue behaviors preserved: drain, role mapping, prompt resolution, dispatch.

### Changed
- **GoalDispatcher reduced to coordinator** — After extracting `TaskDispatchService`, `TaskCompletionService`, and `GoalDispatchService`, `GoalDispatcher` is now a thin coordinator with the polling loop, goal lifecycle (cancel, resume, retry, redispatch), clarification routing, and maintenance scheduling. All dispatch, completion, and task creation logic is delegated to focused services.
- **Startup flow preserved** — `RestoreActivePipelinesAsync` → `SyncAgentsFromConfigRepoAsync` → `Task.Delay(_startupDelay)` → first dispatch → loop. The startup delay remains unchanged. Stale-reset signals during restore are consumed on the first loop iteration after the initial dispatch.

## [0.21.0] - 2026-07-28

### Added

- **SQLite WAL mode** — Enabled Write-Ahead Logging on the SQLite database at startup. WAL allows concurrent readers alongside one writer, reducing "database is locked" errors that occurred when event-driven dashboard notifications triggered reads during goal status writes. The WAL setting is validated at startup and logged.
- **Ollama model prefix** — `ModelDiscoveryService` now prefixes discovered Ollama model IDs with `ollama-cloud/` or `ollama-local/` based on which environment variable is set. Previously, Ollama models were saved without a provider prefix, causing `ChatClientFactory` to route them to the wrong provider (GitHub Copilot instead of Ollama Cloud).
- **TaskDispatchService** — Extracted `DispatchToRole` and `ResolveRepositories` from `GoalDispatcher` into a new `internal sealed TaskDispatchService`. `GoalDispatcher` constructs it unconditionally and delegates via one-line forwarding wrappers. All dispatch behaviors preserved: premium model selection, reasoning suffix, context/compaction metadata, iteration SHA, tester report metadata, improver branch downgrade, task building, tracking, registration, queue, idle worker dispatch, repository failure handling.

### Changed

- **Redundant `AddDbContext` removed** — `Program.cs` no longer registers both `AddDbContext` (scoped) and `AddDbContextFactory` (transient). Only `AddDbContextFactory` remains. Startup migration uses factory-created context instead of scoped context.
- **`_dispatchedGoals` and `_resumeLocks` removed** — `GoalDispatcher` no longer uses these concurrency dictionaries. Double-dispatch prevention uses `_pipelineManager.GetByGoalId()` (pipeline existence check). Resume serialization uses a single `SemaphoreSlim _resumeLock` instead of per-goal `ConcurrentDictionary<string, SemaphoreSlim>`.
- **SharpCoder updated to 0.12.1** — Includes `UsageCapturingChatClient` fix for inflated `LastKnownContextTokens` from `FunctionInvokingChatClient` aggregate usage.

### Fixed

- **GoalBrainActor status race** — `RegisterSessionStatus("idle")` was called in the `finally` block after the reply was set, causing tests to see `"active"` instead of `"idle"` when reading the registry after the LLM call completed. Fixed by moving the status update before the reply.

## [0.20.0] - 2026-07-28

### Added

- **Event-driven dashboard** — `DashboardNotifier` service replaces 3-second timer-based polling. The dashboard now refreshes immediately on state changes (goal dispatch, completion, failure, phase transitions, worker register/remove, task assignment, API mutations) instead of waiting for the next poll cycle. A slow 10-second timer remains for time-based displays (elapsed durations, uptime, logs).
- **Heartbeat throttle** — Worker heartbeat notifications are throttled per worker: immediate on busy/idle state change, immediate on >=5pp context usage change (accumulated from last-notified baseline), 30-second fallback. Bounded by `MaxHeartbeatEntries` (200) with oldest-entry eviction. Unknown workers excluded.

### Removed

- **`FileGoalSource` and goals.yaml support** — Deleted `FileGoalSource.cs` and all goals.yaml import paths. Removed `--goals-file` CLI argument and bootstrap logic from `Program.cs`. Removed `CommitGoalsToConfigRepoAsync` from `GoalLifecycleService` and call sites in `GoalDispatcher.cs` and `PipelineDriver.cs`. Removed `ImportGoalsAsync` from `IGoalStore` and `GoalStore`. **Breaking change for YAML-only deployments:** run v0.19.x once with `--goals-file=goals.yaml` to import goals into SQLite. Verify via the dashboard or `GET /api/goals` before upgrading to v0.20.0+.

### Changed

- **Documentation** — `README.md` updated to remove all operational, format, and feature-list goals.yaml references; added pre-upgrade migration notice. `VISION.md` updated to reference `hive-config.yaml` instead of `goals.yaml`.
- **DashboardStateService simplified** — `_cachedPendingGoals` cache removed (dead code — DB is sole source). `GoalManager` dependency removed. Timer slowed from 3s to 10s (time-based displays only). `TimerPeriod` internal property for testable verification. `DashboardNotifier` optional constructor param (backward compatible).
- **PipelineDriver** — premature `AdvanceTo(Done)` before `MarkGoalCompletedAsync` removed. `GoalLifecycleService` handles the phase advance.

## [0.19.0] - 2026-07-27

### Added

- **Actor-only Brain execution** — The `GoalBrainContext` legacy path has been removed. `BrainActor` and `GoalBrainActor` child actors are now the sole execution path for all Brain LLM calls. No feature flag, no fallback. The `Actor<TMessage>` base class with `System.Threading.Channels` mailbox serializes all state access — no locks, no `AsyncLocal`, no CAS lease protocol, no tombstones.
- **BrainActor authoritative capabilities** — `BrainActor` now manages the `LlmSessionRegistry` lifecycle (register on connect, update on merge/model change, unregister on delete/shutdown). `ConnectAsync` persists the master session. `GetStats` returns complete statistics including cumulative token counts.
- **Session migration** — One-time migration copies legacy `brain-master.json` and `brain-goal-*.json` files from the state directory to the actor's `actors/` subdirectory. A `.migrated` marker file prevents re-migration.
- **Reset durability** — `ResetSessionAsync` deletes all session files in both directories, verifies no survivors remain, and starts a fresh actor. Reset-restart failure leaves the brain in a recoverable degraded state (`_connected = false`, `ConnectAsync` can recover).
- **Multi-round plan validation** — `BrainTools.ValidateIterationPlan` and `BrainPlanParser.MapIterationPlan` now accept occurrence-suffixed phase names (e.g., `coding-1`, `testing-2`) and normalize them to base `GoalPhase` values. Previously, multi-round plans with suffixed names were rejected and retried.

### Changed

- **~525 lines deleted from `DistributedBrain.cs`** — `GoalBrainContext` class, `CreateGoalBrainContextAsync`, `ExecuteBrainViaContextAsync`, `BuildBrainTools`, `AsyncLocal<GoalBrainContext?>`, `_deletingGoals` tombstone, `_goalContexts`/`_activePipelines` ConcurrentDictionaries, `MirrorAsync`/`MirrorFireAndForget`/`FireShadowNote` shadow helpers, `SaveSessionCoreAsync`, `RefreshMasterSessionRegistry`, `_sessionLock` usage in per-goal operations, and the `UseBrainActors` config flag.
- **`UseBrainActors` config flag removed** — actors are always used. Removed from `OrchestratorConfig` and `HiveConfigFile.ReloadFrom`.
- **`StartShadowActorAsync` renamed to `StartBrainActorAsync`** — no longer a "shadow".
- **`_resetting` made visible across threads** — uses `Volatile.Read`/`Volatile.Write` for compile-safe memory visibility without `volatile` keyword (avoids CS0420 with `TreatWarningsAsErrors`).
- **`ConnectAsync` actor startup is mandatory** — failure rolls back `_connected`, unregisters `brain-master`, disposes partial actor, and throws. Second `_disposing` check catches Connect-vs-Dispose race.
- **Lifecycle cancellation** — LLM execution passes caller `ct` to actor (cancels LLM). Lifecycle messages use caller `ct` for reply wait only (actor operation completes on its own with loop token).

### Fixed

- **Multi-round plan validation** — Phase names with occurrence suffixes (`coding-1`, `testing-2`) are now properly accepted and normalized, instead of being rejected and causing retry loops.

## [0.18.0] - 2026-07-26

### Added

- **Actor base class** — `Actor<TMessage>` base class extracted from the three actor prototypes, eliminating ~280 lines of duplicated lifecycle code. Provides channel mailbox, Start/Dispose lifecycle with a single `_lifecycleLock`, message loop with drain, and virtual hooks for shutdown, fault handling, and timeout.
- **BrainActor shadow** — When `use_brain_actors: true` in config, a `BrainActor` runs alongside `DistributedBrain` with an isolated state directory (`actors/`). Lifecycle operations are mirrored via awaited best-effort `MirrorAsync`. Pipeline registration/deregistration is fire-and-forget. Reset disposes the old actor, deletes shadow state files, and starts a fresh actor.
- **Authoritative LLM execution** — When the flag is on, `ExecuteBrainAsync` routes LLM calls to `GoalBrainActor` child actors as the authoritative path. `SummarizeAndMergeAsync` routes its summary LLM call through `ExecuteBrainAsync`. Tool parity achieved with seven total tools — five from the shared `BrainTools` factory plus two custom tools. Plan validation extracted to `BrainTools.ValidateIterationPlan`. Falls back to `GoalBrainContext` path when actor is unavailable.
- **Shadow note injection logging** — Shadow note injection lifecycle events (fired, completed, canceled) logged at Information level; failures at Warning level. Authoritative LLM execution logs actor routing at Information level.
- **BrainActor startup log** — Shadow actor startup logs a success message with the state directory path.
- **Clean Code principles in system prompts** — Goal reviewer checks complexity, goal size, simpler alternatives, and root-cause patching; Brain prompt enforces small functions, SRP, DRY, and YAGNI; Composer splits large goals and prefers deleting over adding code.
- **Diagnostic logging** — `UpdateGoalAsync` now logs with call stack when goal status changes, revealing stale-state overwrites.

### Fixed

- **Stale goal object status** — `CreateProgressDocumentAsync` passed a stale `Goal` object (still Pending) to `UpdateGoalAsync`, overwriting InProgress back to Pending in the DB. Root cause: in-memory `goal` object not synced after `UpdateGoalStatusAsync`. Fix: sync `goal.Status` and `goal.StartedAt` after the DB update succeeds.

### Changed

- **Version bumped** from 0.17.0 to 0.18.0.

## [0.17.0] - 2026-07-25

### Added

- **Per-goal Brain contexts** — `DistributedBrain` now uses independent `GoalBrainContext` instances per goal with dedicated `CodingAgent`, `IChatClient`, and `AgentSession`. Different goals' Brain LLM calls run truly in parallel without a global gate.
- **Extend iterations** — Goals that fail due to iteration exhaustion can be resumed with additional iterations via API endpoint, Composer tool, or dashboard button.
- **`git_fetch` Composer tool** — Fetch remote branches and inspect commits not available locally, with input validation and option-injection protection.
- **`get_recent_logs` Composer tool** — Read recent application log entries directly from the dashboard log sink, with filtering by level, category, and message text.

### Fixed

- **Stale Pending goal status** — Goals showing as Pending on the dashboard after dispatch (cache `TryAdd` → direct DB assignment).
- **Stuck InProgress goals** — UNIQUE constraint violation in `UpdateGoalStatusAsync` prevented `MarkGoalFailedAsync` from completing; stale pipelines not reconciled on restart.
- **Premature context compaction** — SharpCoder 0.12.0 fixes inflated `LastKnownContextTokens` that caused compaction at 19% real context usage.
- **EF Core value-comparer warnings** — Collection properties with JSON value converters now have proper `ValueComparer` for change tracking.
- **Diagnostic logging for status updates** — Goal status update path now logs before/after with verification re-read.

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
