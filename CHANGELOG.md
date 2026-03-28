# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- **Composer `web_search` context window overflow** — search results now have per-result content truncated to 500 characters, preventing context window overflow:
  - Added `const int MaxContentChars = 500` constant in `WebSearchAsync` method for maintainability
  - Each search result content is truncated to 500 characters with "…" suffix when exceeding the limit
  - A single search call now returns ~3K tokens instead of 30-50K+ tokens (10-20x reduction)
  - Prevents Brain session from hitting context window limits after just 3-4 user prompts with web search
  - Updated `WebSearch_WithApiKey_FormatsResults` test to verify short content does NOT have truncation marker
  - Added `WebSearch_TruncatesLongContent` test verifying 600-character content is truncated to 500 chars + "…"

### Changed
- **Composer `web_fetch` default `max_lines` reduced** — changed default from 200 to 100 lines:
  - 200 lines of web content could still be 5-15K tokens; 100 lines is more proportionate
  - The `max_lines` parameter remains available for the model to request more if needed

### Added
- **Composer `list_repositories` tool** — new tool for querying the live hive-config.yaml to list all configured repositories:
  - `ListRepositoriesAsync` method reads from `_hiveConfig` at call time (not a stale snapshot), so it reflects the current state even when `hive-config.yaml` is updated without restart
  - Returns formatted Markdown list with repository name, URL, and default branch for each configured repo
  - Returns "No repositories configured." when no repos are set in the config
  - Registered as `list_repositories` in `BuildComposerTools()` with description "List all configured repositories with their names, URLs, and default branches."
  - System prompt updated to mention new capability: "List configured repositories (list_repositories)"
  - Includes 6 xUnit tests covering: null config, empty list, single repo, multiple repos, tool registration, and system prompt mention

### Fixed
- **Worker container restart failure** — `docker/worker/entrypoint.sh` now handles config repo restarts correctly:
  - Replaces unconditional `git clone` with a clone-or-pull pattern that handles three cases:
    1. **First start** — directory doesn't exist → clone
    2. **Restart with existing repo** — `.git` exists → fetch + reset to latest
    3. **Corrupt/partial clone** — directory exists but no `.git` → rm + clone fresh
  - Config repo now has latest changes after restart (not stale from previous run)
  - `git config user.email/name` is set after both clone and pull paths

### Added
- **Inline prompt display in Goal Detail** — Brain prompts and worker prompts are now displayed inline within each phase on the Goal Detail page, replacing the flat conversation log:
  - `PhaseViewInfo` now includes `BrainPrompt` (the prompt sent to the Brain to generate the worker prompt) and `WorkerPrompt` (the Brain's crafted prompt sent to the worker)
  - `IterationViewInfo` now includes `PlanningBrainPrompt` and `PlanningBrainResponse` for the iteration planning phase
  - `DashboardStateService.GetGoalDetail()` extracts prompts from tagged `ConversationEntry` metadata (`Iteration` and `Purpose` fields) using new `ExtractPlanningPrompts()` and `ExtractCraftPrompts()` helpers
  - `GoalDetail.razor` displays prompts as collapsible sections above Worker Output: Brain Prompt (muted styling) and Worker Prompt (with role name)
  - Planning phase displays Planning Response and Planning Prompt when expanded
  - All prompts are rendered as Markdown using `RenderMarkdown()` with `chat-msg-content` styling
  - The flat "Brain Conversation" section at the bottom of the page has been removed — conversation data is now shown contextually within phases
  - Completed iterations (from `IterationSummary`, no live pipeline) gracefully show null prompts — sections are simply not displayed
  - Includes 10 xUnit tests for `ExtractPlanningPrompts`, `ExtractCraftPrompts`, and prompt population in `GetGoalDetail` including null handling for completed iterations

### Added
- **ConversationEntry metadata** — `ConversationEntry` now tracks iteration context and purpose, enabling conversation history to be analyzed by iteration without heuristic parsing:
  - `ConversationEntry` record extended with `Iteration` (int?) and `Purpose` (string?) properties with default null values for backward compatibility
  - Purpose values: `"planning"` — iteration planning prompt/response, `"craft-prompt"` — Brain generating worker prompts, `"worker-output"` — structured output summary from workers, `"error"` — error messages
  - All conversation entries in `DistributedBrain.PlanIterationAsync` and `DistributedBrain.CraftPromptAsync` are tagged with iteration and purpose
  - Worker output entries in `GoalDispatcher.DriveNextPhaseAsync` are tagged with iteration and purpose
  - `PipelineStore` persists and loads `Iteration` and `Purpose` via new `iteration` and `purpose` columns with automatic schema migration for existing databases
  - Legacy entries (null iteration/purpose) load correctly, preserving backward compatibility
  - Existing code creating `ConversationEntry(role, content)` continues to compile unchanged due to default parameters

### Added
- **Composer `get_phase_output` tool** — new tool for drilling into worker output from specific phases of a goal iteration:
  - `GetPhaseOutputAsync` method takes `goalId`, `iteration` (1-based), `phase` name, and optional `max_lines` (default 200)
  - Phase name matching is case-insensitive (Coding, Testing, Review, DocWriting, Improve)
  - Returns worker output from `PhaseResult.WorkerOutput` if available, falls back to `IterationSummary.PhaseOutputs` dictionary using role key mapping (e.g., `"Coding"` → `"coder-{iteration}"`)
  - Output truncated to `max_lines` with notice (`"... (truncated, N lines total)"`)
  - Clear error messages: "Goal not found", "Iteration N not found", "Phase 'X' not found in iteration N", "No output recorded for phase X in iteration N"
  - Registered as `get_phase_output` in `BuildComposerTools()` with description "Get the raw worker output for a specific phase within an iteration."
  - System prompt updated to mention new capability: "Drill into phase output (get_phase_output)"
  - Phase outputs are only available for worker phases (Coding, Testing, Review, DocWriting, Improve). Planning phase is not supported for `get_phase_output` since it is executed internally by the orchestrator Brain, not by a worker.
  - Includes 20 xUnit tests covering: happy path, case-insensitive matching, fallback to PhaseOutputs, role key mapping for all 5 phases, truncation, and error cases (goal not found, iteration not found, phase not found, no output recorded, invalid inputs)

### Changed
- **Composer `get_goal` enriched iteration display** — iteration details now show per-phase breakdown instead of just a phase count:
  - Old format: `- **Iteration 1:** 4 phases, 840/840 tests, review: reject`
  - New format shows each phase on its own line with name, result, duration, and test counts for Testing phases:
    ```
    ### Iteration 1 (review: reject)
    - Coding: pass (45.2s)
    - Testing: pass (120.1s) — 840/840
    - Review: fail (30.5s)
    ```
  - Phase durations formatted to 1 decimal place
  - Testing phase includes test counts inline when available (e.g., "840/840")
  - Review verdict shown in header suffix when available
  - Includes 3 xUnit tests covering: per-phase detail display, review verdict suffix handling, no-review-verdict case, Testing phase without test counts

### Added
- **Remote branch cleanup on goal deletion** — when a Failed goal is deleted, its remote feature branches are automatically cleaned up from all associated repositories:
  - `BrainRepoManager.DeleteRemoteBranchAsync(repoName, branchName)` — deletes the remote branch and local tracking branch from the specified repository clone; best-effort (logs warning on failure, doesn't throw)
  - `Composer.DeleteGoalAsync` — after successful store deletion, calls `DeleteRemoteBranchAsync` for each repository in `goal.RepositoryNames` when `goal.Status == GoalStatus.Failed`
  - `DELETE /api/goals/{id}` endpoint — same branch cleanup logic added to the API endpoint via `IServiceProvider`
  - Branch naming convention: `copilothive/{goalId}` (e.g., `copilothive/my-goal`)
  - Only Failed goals trigger branch cleanup (Draft goals may not have branches yet)
  - Cleanup is best-effort — goal deletion succeeds even if branch deletion fails (e.g., branch doesn't exist, remote unreachable)
  - Includes 5 xUnit tests for `DeleteRemoteBranchAsync` covering: no clone exists, clone exists with no branch, local branch deletion, warning logging on failure
  - Includes 3 integration tests for `Composer.DeleteGoalAsync` covering: Failed goal with repo manager (cleanup attempted), Draft goal with repo manager (no cleanup), Failed goal with git failures (still succeeds)
  - Includes 3 API endpoint tests for `DELETE /api/goals/{id}` covering: Failed goal returns 204 No Content, goal is removed from store, branch cleanup is attempted

### Added
- **Composer web research tools** — `web_search` and `web_fetch` tools are now available in the Composer for researching information on the web via Ollama's web search and fetch APIs:
  - `web_search` — searches the web for information, returns titles, URLs, and content snippets; supports `max_results` parameter (clamped to 1-10, default 5)
  - `web_fetch` — fetches a web page and returns its title, content, and extracted links; supports `max_lines` parameter for content truncation (default 200)
  - Both tools require `OLLAMA_API_KEY` environment variable; return clear error messages when not configured ("❌ Web search is not available — no OLLAMA_API_KEY configured.")
  - Tools are conditionally registered — only appear in LLM tool list when API key is present
  - System prompt dynamically mentions web capabilities only when configured
  - Uses named HTTP client `"ollama-web"` with 15-second timeout via `IHttpClientFactory`
  - Authorization via `Authorization: Bearer {OLLAMA_API_KEY}` header
  - Graceful error handling for HTTP errors, network failures, timeouts, and invalid JSON responses
  - Includes 24 xUnit tests covering: search formatting, fetch formatting, fetch truncation, missing API key errors, HTTP error handling, network failure handling, authorization header verification

### Changed
- **Multi-architecture Docker support** — Docker images now support both `amd64` and `arm64` architectures:
  - Both `docker/orchestrator/Dockerfile` and `docker/worker/Dockerfile` use `ARG TARGETARCH` to receive the target platform from Docker BuildKit
  - Runtime identifiers dynamically mapped via `${TARGETARCH/amd64/x64}`: Docker `amd64` → `linux-x64`, `arm64` → `linux-arm64`
  - Building without explicit `--platform` defaults to host architecture; `--platform linux/arm64` produces arm64 binaries
  - Runtime base images (`mcr.microsoft.com/dotnet/aspnet:10.0`, `ubuntu:24.04`) already support multi-arch

### Fixed
- **Push error visibility** — git push failures on workers are now reported back to the orchestrator and Brain instead of being silently swallowed:
  - `TaskExecutor` captures push errors in a `pushErrors` list and appends them to `copilotOutput` under `[Git Push Errors]` section
  - Push errors are added to `TaskMetrics.Issues` so they appear in goal tracking and metrics
  - `GoalDispatcher.BuildWorkerOutputSummary` includes a `⚠️ Git push FAILED — changes were not pushed to the remote` warning when `FilesChanged > 0` and `Pushed == false`
  - Warning log emitted when a task has file changes but push failed: "Task {TaskId} had {Files} file changes but push failed"
  - Push failures still don't cause the task to fail (intentional — partial success is valid for some phases like reviewer)
  - Tests added: `IncludesPushFailureWarning_WhenFilesChangedButNotPushed`, `OmitsPushFailureWarning_WhenPushedSuccessfully`, `OmitsPushFailureWarning_WhenNoFilesChanged`, `HandleTaskCompletionAsync_LogsWarning_WhenFilesChangedButNotPushed`, `HandleTaskCompletionAsync_NoWarning_WhenPushedSuccessfully`

### Fixed
- **QEMU segfault when building arm64 Docker images in CI** — fixed segfault during multi-architecture container builds by cross-compiling instead of emulating:
  - Both `docker/orchestrator/Dockerfile` and `docker/worker/Dockerfile` now use `--platform=$BUILDPLATFORM` on the build stage `FROM` line
  - The build stage runs natively on the host architecture (amd64), using .NET's cross-compilation to produce arm64 binaries via the `-r` flag
  - The runtime stage continues to use the target platform's base image (no `--platform` flag), correctly pulling arm64 variants when targeting arm64
  - This avoids running the .NET SDK under QEMU emulation, which causes JIT segfaults on arm64 builds

### Fixed
- **SQLite "readonly database" error in CI** — fixed `GoalsApiEndpointTests` and other `HiveTestFactory`-based tests that failed in GitHub Actions with `SQLite Error 8: 'attempt to write a readonly database'`:
  - Root cause: Multiple test classes using `IClassFixture<HiveTestFactory>` created separate factory instances that raced on the process-wide `STATE_DIR` environment variable, causing one factory to delete another's temp directory while SQLite was still writing to it
  - Solution: Replaced `IClassFixture<HiveTestFactory>` with `[Collection("HiveIntegration")]` on all 5 test classes (`GoalsApiEndpointTests`, `HealthCheckTests`, `HealthEndpointTests`, `WorkerPoolStatsEndpointTests`, `UtilizationEndpointTests`)
  - Added `[CollectionDefinition("HiveIntegration")]` class in `HiveTestFactory.cs` implementing `ICollectionFixture<HiveTestFactory>` to ensure a single shared factory instance across the entire test run
  - Test isolation is preserved: each test uses unique goal IDs via `UniqueId()` helper

### Added
- **Goal cancellation** — InProgress or Pending goals can now be cancelled via the API, dashboard UI, or Composer chat:
  - `GoalDispatcher.CancelGoalAsync(string goalId, CancellationToken ct)` — cancels active pipelines (moves to Failed state) or Pending goals (updates status directly); returns `true` if cancelled, `false` if already Done/Completed/Failed
  - `POST /api/goals/{id}/cancel` endpoint — returns `200 OK` on success, `404 Not Found` if goal doesn't exist, `400 Bad Request` if goal is not InProgress or Pending
  - `GoalDetail.razor` — shows "⏹ Cancel Goal" button for InProgress and Pending goals with browser confirmation dialog; errors displayed inline (not silently swallowed)
  - `Composer.CancelGoalAsync` tool — `cancel_goal` function available in Composer chat; validates goal status before calling dispatcher; returns descriptive error messages for non-cancellable goals
  - `GoalManager.GetGoalAsync` — new method to retrieve a goal by ID from any registered source
  - 11 xUnit tests (`GoalDispatcherCancelTests`) covering: cancel InProgress pipeline, cancel Pending goal without pipeline, reject cancel for Done/Failed/Completed goals, reject cancel for unknown goal IDs

### Added
- **GitHub Actions CI workflow** — automated build, test, and container image push to GitHub Container Registry (ghcr.io):
  - New `.github/workflows/ci.yml` workflow triggered on pushes to `develop` branch only
  - `test` job — restores dependencies (`dotnet restore`), builds in Release mode (`dotnet build`), and runs the full xUnit test suite (`dotnet test`)
  - `build-and-push` job — depends on successful test completion, builds and pushes both orchestrator and worker Docker images to GHCR
  - Images published: `ghcr.io/robkaandorp/copilothive-orchestrator` and `ghcr.io/robkaandorp/copilothive-worker`
  - Tags: `latest` and commit SHA (`sha-{short-sha}`) for traceability
  - Docker layer caching enabled via GitHub Actions cache (`type=gha`)
  - Uses `secrets.GITHUB_TOKEN` with `packages: write` permission for GHCR authentication
  - Build context: repository root (`.`), Dockerfiles located at `docker/orchestrator/Dockerfile` and `docker/worker/Dockerfile`

### Added

- **Brain-generated squash merge commit messages** — merge commits now use concise Brain-generated summaries instead of verbose goal descriptions:
  - `IDistributedBrain.GenerateCommitMessageAsync()` — new interface method for requesting concise commit summaries (~72 char subject + 2-4 bullet body)
  - `DistributedBrain` implementation uses `ExecuteBrainAsync` with retry policy; returns null on any failure for graceful fallback
  - `GoalDispatcher.GenerateMergeCommitMessageAsync()` — new helper that calls Brain first, falls back to `BuildSquashCommitMessage` on null/exception
  - Commit message preserved format: `Goal: {goalId} — {brainSummary}` (fallback to `Goal: {goalId} — {description}`)
  - Tests added for Brain success, null return, and exception fallback scenarios

### Fixed
- **BrainRepoManagerTests CI failures** — 5 tests in `BrainRepoManagerTests.cs` now use temporary directories instead of hardcoded absolute paths (`/app/state`, `/custom/path`), fixing `UnauthorizedAccessException` on GitHub Actions ubuntu-latest runner:
  - Tests implement `IDisposable` to clean up temp directories after execution
  - Path assertions use `StartsWith`/`EndsWith` against temp-based paths instead of hardcoded values
  - Tests pass on both Linux (GitHub Actions) and Docker (worker environment)
- **GoalDetail.razor delete error handling** — delete operation failures now display inline error messages instead of silently failing:
  - Added `_deleteError` field to store error content
  - `DeleteCurrentGoal()` method now reads error response and updates `_deleteError` on failure
  - Error message displayed next to the delete button when present

### Added
- **"Revert to Draft" button for Pending goals in Goals list** — `Goals.razor` now includes a "↩ Revert to Draft" button for goals with `Pending` status (icon-only style matching delete button):
  - Button appears in the Actions column for Pending goals with `title="Revert to Draft"` tooltip
  - Calls `PATCH /api/goals/{id}/status` with `{ "status": "Draft" }` on click
  - Updates local state and refreshes the list on success
  - Inline error display via `_revertErrors` dictionary on failure
  - `RevertToDraft()` method and `_revertErrors` field follow same pattern as existing `ApproveGoal()`

- **"Revert to Draft" button for Pending goals** — `GoalDetail.razor` now includes a "↩ Revert to Draft" button for goals with `Pending` status (lines 26-33):
  - Button appears next to "▶ Approve" for Draft goals (now also shown for Pending)
  - Calls `PATCH /api/goals/{id}/status` with `{ "status": "Draft" }` on click
  - Refreshes goal detail and updates local state on success
  - Inline error display via `_revertError` field on failure
  - `_revertError` field and `RevertToDraft()` method follow same pattern as existing `ApproveGoal()`

### Changed
- **Approve button in Goals list is now icon-only** — `Goals.razor` Actions column now shows the approve button as "▶" icon only (removed "Approve" text) for Draft goals:
  - Changed from `<button>▶ Approve</button>` to `<button title="Approve">▶</button>`
  - The `title` attribute provides a tooltip on hover for discoverability
  - Reduces column width for better display at half-screen browser layouts
  - Styling matches the existing delete button pattern (icon-only)

- **PATCH `/api/goals/{id}/status` endpoint validation** — Program.cs (lines 347-352) now validates allowed status transitions:
  - Only `Draft → Pending` and `Pending → Draft` transitions are permitted via the public API
  - Returns `400 Bad Request` with clear error message for invalid transitions (e.g., `InProgress → Draft`, `Draft → Completed`)
  - `404 Not Found` returned when goal does not exist
  - Internal transitions (`InProgress`, `Completed`, `Failed`) are handled by `GoalDispatcher.UpdateGoalStatusAsync` (not the API)

### Changed
- **Composer `update_goal` tool transition validation** — `Composer.cs` (lines 550-554) now validates status transitions:
  - Added validation to ensure only `Draft → Pending` and `Pending → Draft` transitions are allowed
  - Returns clear error message: "❌ Invalid transition from {current} to {requested}. Only Draft→Pending and Pending→Draft are allowed."
  - Complements the API validation to ensure consistent behavior across both UI and Composer chat

### Fixed
- **Worker model display in dashboard** — Workers list and Worker Detail pages now show the actual model being used for the current task (including premium tier upgrades) instead of the static config default:
  - `ConnectedWorker.CurrentModel` property added — populated when a task is assigned (`task.Model`), cleared when the worker becomes idle
  - `DashboardStateService.WorkerInfo.CurrentModel` added — flows from `ConnectedWorker` to the dashboard via `GetSnapshot()`
  - `Workers.razor` and `WorkerDetail.razor` updated — prefer `CurrentModel` when available, fall back to config default only when idle

### Added
- **Composer git tools** — the Composer now has 5 read-only git tools for inspecting repository commit history, diffs, branches, and blame information:
  - `git_log` — view commit history with options for branch, path, date filtering, and format (oneline/short/full)
  - `git_diff` — compare changes between refs with stat-only and path filtering options
  - `git_show` — view details of a specific commit
  - `git_branch` — list local or remote branches with optional glob pattern filtering
  - `git_blame` — show line-by-line authorship for files with optional line range
  - All tools execute against the Brain's persistent repo clones via `BrainRepoManager.GetClonePath()`
  - Output is truncated to 500 lines (configurable) with truncation notice
  - Invalid repository names return an error listing available repositories
  - Parameters are passed via `ArgumentList.Add()` to prevent command injection
  - Includes 20 xUnit tests (`ComposerToolTests`) covering null repo manager, unknown repos, parameter validation, and real git operations

### Added
- **Visible Planning phase in iteration timeline** — `GoalDetail.razor` now shows a dedicated "Planning" phase in the iteration timeline:
  - Planning phase is always displayed as the first phase box in both current and completed iterations
  - During active planning (`GoalPhase.Planning`), only the Planning phase is shown (no fallback phases), with `active` status and ⏳ indicator
  - After planning completes, Planning shows as `completed` with ✓ icon, followed by the actual planned phases
  - Completed iterations from history also display a completed Planning phase at the start
- **Plan reasoning display** — the Brain's iteration plan reasoning (`IterationPlan.Reason`) is now visible in the Goal Detail page:
  - Displayed as subtle italic text below the phase bar with 📋 prefix (e.g., "📋 Standard first iteration with coding, testing, and review")
  - Helps users understand why the Brain chose a particular iteration strategy
  - `IterationViewInfo.PlanReason` property carries the reasoning from `DashboardStateService` to the UI
- **Goal Detail page back button** — `GoalDetail.razor` now includes a "← Goals" back link above the goal header for quick navigation to the goals list (`/goals`); styled with `color: var(--accent)` to match the dashboard design

### Changed
- **Worker output rendering in Goal Detail** — iteration phase details now render worker output as Markdown instead of plain text:
  - Uses the existing `RenderMarkdown()` method (Markdig with `UseAdvancedExtensions().DisableHtml()`)
  - Wrapped in `<div class="chat-msg-content">` for consistent dark-theme styling (code blocks, tables, lists, headings)
  - Previously displayed as raw `<pre>` text without formatting

### Fixed
- **Dependency link readability in Goal Detail** — dependency goal IDs are now clearly visible on the dark background:
  - Added `class="goal-link"` to dependency links in both the active pipeline view and stored goal view
  - The `goal-link` CSS class sets `color: var(--accent)` (`#58a6ff`) and `text-decoration: none`
  - Previously rendered as browser-default dark blue/purple, which was unreadable on the `--bg: #0d1117` dark theme

### Changed
- **Reset Brain Session button styling** — improved visual appearance and UX of the Brain reset button on the Orchestrator dashboard:
  - Changed from unstyled `btn btn-danger` to `chat-action-btn chat-action-btn--danger` classes for consistent dashboard design
  - Added 🔄 icon prefix to the button label for visual clarity
  - Added ⏳ spinner icon during reset operation for progress indication
  - Added `.chat-action-btn--danger` CSS modifier in `site.css` — destructive actions show red hover state (`var(--red)`) to signal intent
- **Squash merge for feature branches** — `BrainRepoManager.MergeFeatureBranchAsync` now performs squash merges instead of standard merge commits:
  - All commits from a feature branch are combined into a single commit on the base branch
  - Commit message follows the format `Goal: {goalId} — {summary}` (e.g., `Goal: add-user-auth — Add user authentication with JWT tokens`)
  - The `commitMessage` parameter was added to `MergeFeatureBranchAsync` signature for the squash commit message
  - `GoalDispatcher.BuildSquashCommitMessage` helper builds the commit message from goal ID and description
  - Subject line is truncated to 120 characters when needed; full description appears in commit body
  - Handles empty squash results gracefully (logs and returns current HEAD)
  - Merge conflict handling via `git merge --abort` continues to work correctly with `--squash`
  - Includes 8 xUnit tests for `BuildSquashCommitMessage` covering short descriptions, long descriptions (truncation), multi-line descriptions, empty descriptions, and edge cases

### Added
- **Reset Brain Session button** — Orchestrator dashboard page now includes a "Reset Brain Session" button in the Brain section:
  - Button appears only when the Brain is connected (stats available)
  - Shows browser confirmation dialog with clear warning before resetting
  - Disabled during reset operation to prevent double-clicks
  - Resets the Brain's persistent `AgentSession`, clearing all conversation history
  - Deletes the persisted session file (`brain-session.json`)
  - Rebuilds the `CodingAgent` with fresh system prompt — re-reads `orchestrator.agents.md` from disk so edits take effect immediately
  - Thread-safe via `_brainCallGate` semaphore
  - Dashboard stats update automatically after reset to show new session state
  - Includes 2 xUnit tests for `ResetSessionAsync` (reload from disk, without agents manager)
- **Worker output persistence** — worker outputs are now captured and persist through goal completion:
  - `PhaseResult.WorkerOutput` property stores the raw worker output for each phase inline with its result
  - `IterationSummary.PhaseOutputs` dictionary keyed by `{role}-{iteration}` (e.g., "coder-1") stores all outputs for the iteration
  - `GoalDispatcher.BuildIterationSummary()` populates `WorkerOutput` from live pipeline outputs and builds the `PhaseOutputs` dictionary
  - `FileGoalSource` serializes worker outputs to YAML and round-trips them through `goals.yaml`
  - `SqliteGoalStore` persists outputs via `phase_outputs_json` column with automatic schema migration
  - `DashboardStateService.GetGoalDetail()` prefers persisted `PhaseResult.WorkerOutput` over live pipeline data, ensuring completed goals display output correctly
  - Includes 15 xUnit tests for `PhaseResult.WorkerOutput` and `IterationSummary.PhaseOutputs` round-trip through SQLite, YAML, and the dashboard

### Added
- **Goal dependency visualization in dashboard** — Goals and Goal Detail pages now display dependency relationships:
  - `GoalDetail.razor` shows a "Dependencies" section listing each dependency goal ID as a clickable link with status indicator (✅ Completed, ⏳ Pending, 🔄 InProgress, ❌ Failed, ❓ Unknown)
  - `Goals.razor` shows a 🔗 icon for goals with all dependencies completed, or ⏳ icon if any dependency is unsatisfied
  - Goals with no dependencies show no indicator in the list view and hide the Dependencies section in detail view
  - Dependency status is fetched from `DashboardStateService` and `GoalStore` for real-time display
- **YAML syntax highlighting for hive-config.yaml on Configuration page** — the `hive-config.yaml` tab now renders with CSS-based syntax highlighting:
  - `Configuration.razor` uses `@((MarkupString)HighlightYaml(content))` for YAML files
  - Regex-based highlighting wraps keys, comments, booleans, and numbers in `<span>` elements with CSS classes
  - Dark-theme CSS styling in `site.css` using existing variables: keys (`--accent`), comments (`--text-muted`), booleans (`#79c0ff`), numbers (`#f2cc60`)
  - Agent `.md` files continue to render as Markdown (unchanged)
- **Markdown rendering for agent files on Configuration page** — `.agents.md` files are now rendered as formatted HTML using Markdig (`UseAdvancedExtensions`) instead of plain text:
  - `Configuration.razor` detects `.md` files and renders them with `@((MarkupString)RenderMarkdown(content))`
  - Static `MarkdownPipeline` field using same pattern as `GoalDetail.razor` and `ComposerChat.razor`
  - Dark-theme CSS styling in `site.css` for headings, lists, code blocks, links, and paragraphs (`.config-markdown` class)
  - `hive-config.yaml` and other non-`.md` files continue to render as `<pre><code>` (unchanged)

### Changed
- **Configuration page UI** — removed the `goals.yaml` tab from `Configuration.razor`; goals are now managed exclusively via the Goals page and API
- **Global scrollbar styling** — extracted thin scrollbar CSS from `.chat-messages` in `site.css` and applied globally to all scrollable elements (config content, config tabs, composer chat, goal detail, goals list); uses consistent `rgba(110, 118, 129, 0.4)` color scheme across the dashboard

### Added
- **Merge commit link in goal detail page** — completed goals now display their merge commit hash as a clickable GitHub link in the dashboard (`GoalDetail.razor`):
  - Shows short hash (7 characters) with 🔗 prefix, e.g., "🔗 a1b2c3d"
  - Links to `https://github.com/{owner}/{repo}/commit/{full-hash}` in a new tab
  - Falls back to plain text when repository URL is unavailable
  - `.git` suffix is automatically stripped from repository URLs
  - Appears in both active-pipeline view and stored-goal fallback view
  - `GoalDetailInfo.RepositoryUrl` property added to expose the resolved repository URL (with 8 xUnit tests for `GetRepositoryUrl`)
- **Goal approval from dashboard UI** — goals in Draft status can now be approved (scheduled) directly from the Goals Browser, transitioning their status from Draft to Pending:
  - **Goals list view (`Goals.razor`)** — added "Actions" column with an "▶ Approve" button for goals with `Draft` status; clicking calls `PATCH /api/goals/{id}/status` with `{ "status": "Pending" }` and immediately updates the UI without page reload; inline error messages displayed on failure
  - **Goal detail view (`GoalDetail.razor`)** — added "▶ Approve" button in the header, positioned to the right of the goal title, only visible for Draft goals; same PATCH API call and immediate UI refresh behavior; button disappears after successful approval
- **Goal deletion from dashboard UI** — goals in Draft or Failed status can now be deleted directly from the Goals Browser:
  - **Goals list view (`Goals.razor`)** — added 🗑️ trash can icon button on each row for goals with `Draft` or `Failed` status; clicking shows a browser confirmation dialog ("Are you sure you want to delete goal '{id}'?") and refreshes the list on confirm
  - **Goal detail view (`GoalDetail.razor`)** — added red-styled "🗑️ Delete Goal" button at the bottom of the detail page, only visible for Draft or Failed goals; shows the same confirmation dialog and navigates back to the goals list on successful deletion
- **Goals API error handling** — `POST /api/goals` endpoint now properly handles `ArgumentException` (invalid goal ID format) with 400 Bad Request and `SqliteException` with constraint violation (duplicate ID) with 409 Conflict, instead of returning 500 Internal Server Error
- **Goals API integration tests** — `GoalsApiEndpointTests` class with 20 xUnit tests covering all Goals REST API endpoints (`GET /api/goals`, `POST /api/goals`, `GET /api/goals/{id}`, `PATCH /api/goals/{id}/status`, `DELETE /api/goals/{id}`, `GET /api/goals/search`); tests verify HTTP status codes, response bodies, error handling, and end-to-end integration with `HiveTestFactory`

### Removed
- **Dead merge code path** — Removed `MergeViaTempCloneAsync` method and its standalone `RunGitAsync` helper from `GoalDispatcher` (the temp-clone fallback was dead code — `BrainRepoManager` is always registered in `Program.cs`); `_repoManager` field is now non-nullable

### Added
- **Merge commit hash capture and persistence** — After successful merges, the SHA-1 hash is now captured and stored throughout the goal lifecycle:
  - `BrainRepoManager.MergeFeatureBranchAsync` returns `Task<string>` with the 40-character hex SHA of the merge commit
  - `Goal.MergeCommitHash` property stores the hash on the goal entity
  - `GoalUpdateMetadata.MergeCommitHash` propagates the hash during goal completion
  - `PipelineSnapshot.MergeCommitHash` persists the hash for restart recovery
  - `GoalDetailInfo.MergeCommitHash` exposes the hash in dashboard goal details
  - `SqliteGoalStore` includes schema migration for new `merge_commit_hash` column; includes 6 xUnit tests for round-trip persistence, null handling, and schema migration
  - `FileGoalSource` propagates `MergeCommitHash` via `UpdateGoalStatusAsync`; includes 2 xUnit tests
  - `DashboardStateService` populates `GoalDetailInfo.MergeCommitHash` from pipeline or stored goal; includes 3 xUnit tests
  - `PipelineStore` persists `MergeCommitHash` in pipeline snapshots with 4 xUnit tests for round-trip, null handling, and restore scenarios
- **Dependency-aware goal dispatch** — `GoalManager.GetNextGoalAsync` now filters out pending goals whose `DependsOn` dependencies are not yet `Completed`; blocked goals are skipped and logged at Debug level with their unsatisfied dependency IDs; includes 9 xUnit tests covering blocked goal skipping, unblocked goal dispatching, multi-dependency scenarios, non-existent dependency blocking, empty dependencies, priority ordering among eligible goals, and debug logging verification
- **Cross-source dependency resolution** — `GoalManager` dependency filtering now resolves dependencies from all registered sources, not just pending goals, so goals depending on already-completed prerequisites are correctly unblocked on startup
- **DependsOn support** — goals can now declare dependencies on other goals via a list of goal IDs that must complete before this goal becomes eligible for dispatch; persisted across SQLite (`depends_on` JSON column), FileGoalSource YAML (`depends_on` field), Composer's `create_goal` tool (optional `depends_on` parameter), and dashboard `GoalDetailInfo`; includes 10 xUnit tests for round-trip persistence, parameter parsing, and schema migration
- **PipelineLifecycleIntegrationTests** — integration tests covering multi-pipeline concurrent lifecycle, full pipeline create→advance→persist→restore→continue→complete→remove, conversation/phase output accumulation, and metrics survival across restore for `GoalPipelineManager` and `PipelineStore` (4 xUnit tests)
- **PipelineFlowIntegrationTests** — integration test suite covering cross-component behavior of `PipelineStateMachine`, `GoalPipelineManager`, `TaskBuilder`, and `BranchCoordinator` across happy-path, multi-iteration failure recovery, retry exhaustion, and branch coordination scenarios (4 xUnit tests)
- **Composer agent** — `Composer` class for conversational goal decomposition and management; uses SharpCoder `CodingAgent` with a persistent session, streaming responses, and 7 LLM-callable tools: `create_goal` (creates as Draft), `approve_goal` (Draft→Pending), `update_goal`, `delete_goal` (Draft/Failed only), `get_goal`, `list_goals`, `search_goals`; includes codebase tools (`read_file`, `glob`, `grep`) for informed goal creation; configurable independently via top-level `composer:` block in hive-config.yaml
- **Composer Chat UI** — Blazor Server page at `/composer` with streaming chat interface; real-time text deltas via `IAsyncEnumerable<StreamingUpdate>`, Enter-to-send, session reset, cancel support; full Markdown rendering using **Markdig** (`UseAdvancedExtensions`) for tables, code blocks, lists, blockquotes; chat history restored from persistent session on page navigation; auto-scroll to bottom; modern thin scrollbar
- **Composer dashboard stats** — Orchestrator page shows Composer model in the Model Configuration table and a dedicated Composer section with context usage, message count, cumulative tokens, and progress bar
- **SharpCoder streaming** — `ExecuteStreamingAsync` on `CodingAgent` returning `IAsyncEnumerable<StreamingUpdate>` with `TextDelta` and `Completed` variants; `BuildResponseFromUpdates` reconstructs `ChatResponse` from accumulated updates; `BuildDiagnostics` extracted for reuse (SharpCoder 0.4.0-alpha.1)
- **Tool call argument fix** — `CopilotChoiceMergingHandler.FixToolCallArguments` sanitises outgoing requests: replaces `"null"` or `""` tool_call arguments with `"{}"` to fix Claude models via Copilot API (Anthropic `tool_use.input` requires a valid dictionary)
- 32 `ComposerToolTests` covering all 7 Composer tools, validation, and error cases
- 4 `FixToolCallArguments` tests for null, empty, valid, and no-tool-call cases
- **SQLite goal store** — `SqliteGoalStore` as the primary source of truth for goals, replacing `FileGoalSource` and `ApiGoalSource` in the orchestrator pipeline; full CRUD, text search, status filtering, iteration tracking, and import from goals.yaml (one-time bootstrap migration on startup)
- `IGoalStore` interface — extends `IGoalSource` with `GetAllGoalsAsync`, `GetGoalAsync`, `CreateGoalAsync`, `UpdateGoalAsync`, `DeleteGoalAsync`, `SearchGoalsAsync`, `GetGoalsByStatusAsync`, `AddIterationAsync`, `GetIterationsAsync`, `ImportGoalsAsync`
- **Goals REST API** — expanded endpoints: `GET /api/goals/{id}`, `DELETE /api/goals/{id}`, `GET /api/goals/search?q=…&status=…`; existing `GET/POST/PATCH` endpoints now backed by SQLite
- **Goals Browser dashboard** — new `/goals` page with search bar, status/priority filter dropdowns, and sortable table linking to goal detail
- **GoalDetail SQLite fallback** — goal detail page now shows full iteration history, phase results, test counts, failure reasons, and phase durations for completed/failed goals (loaded from SQLite when no active pipeline exists)
- **Worker output in Brain conversation** — `GoalDispatcher.BuildWorkerOutputSummary` creates structured summaries (verdict, git stats, test metrics, issues, truncated raw output) injected into the Brain conversation after each worker completion, enabling informed replanning after failures
- **Docwriting-aware reviewer prompts** — `DistributedBrain.CraftPromptAsync` detects when docwriting preceded review in the iteration plan and instructs the reviewer that doc changes are expected
- **Empty choices array handling** — `CopilotChoiceMergingHandler` synthesizes a minimal stop choice when Copilot API returns `"choices": []` (end-of-conversation signal), preventing SDK crash
- **HTTP resilience** — `Microsoft.Extensions.Http.Resilience` added to both Worker and Orchestrator; all LLM API calls use 3 retries with exponential backoff (5s base) and 2-minute per-attempt timeout
- 21 new `SqliteGoalStoreTests` covering CRUD, search, status queries, iteration tracking, import, and metadata roundtrip
- 3 new `CopilotChoiceMergingHandlerTests` for empty choices, passthrough, and merging
- 6 new `GoalDispatcherBuildWorkerOutputSummaryTests`

### Changed
- `GoalManager` now uses `SqliteGoalStore` as its sole goal source (was `ApiGoalSource` + `FileGoalSource`)
- `DashboardStateService` uses `IGoalStore` for goal enumeration (was `ApiGoalSource`); now also injects `Composer` for stats
- Health endpoint (`GET /health`) backed by `SqliteGoalStore` (was `ApiGoalSource`)
- Smoke test skill preserves persistent volumes (`docker compose down` without `-v`); no longer references goals.yaml or CopilotHive-Config for goal setting (goals created via Composer)
- `ComposerConfig` moved to top-level in `HiveConfigFile` (sibling of `Orchestrator`/`Workers`, not nested under `Orchestrator`)

### Added
- **Model name in completion logs** — task and phase completion logs now include the model name that was used by the worker; `HiveOrchestratorService.HandleTaskComplete` logs "Task {TaskId} completed by {WorkerId}: {Status} (model={Model})"; `GoalDispatcher` logs "Pipeline {GoalId} task completed (phase={Phase}, status={Status}, model={Model})" and "Phase {Phase} for goal {GoalId} completed in {DurationSeconds:F1}s (model={Model})"; includes 3 xUnit tests verifying model name appears in both task completed and phase completed log messages, and "unknown" is logged when model is empty
- **Goal elapsed time logging** — when a goal completes, `GoalDispatcher` logs the total elapsed time as "Goal {goalId} completed in {elapsed}" (e.g., "Goal abc-123 completed in 4m 32s"); elapsed time is formatted with the new `DurationFormatter` utility which outputs human-readable durations like "45s", "4m 32s", or "1h 12m 5s"; `total_duration_seconds` field is now written to goals.yaml on goal completion alongside `completed_at`
- `DurationFormatter` — new utility class with 9 xUnit tests for human-readable duration formatting; formats `TimeSpan` or seconds values into concise strings (e.g., "32s", "4m 32s", "1h 12m 5s")
- **Brain context-usage logging** — after each Brain call (`CraftPromptAsync`, `InterpretResultAsync`, `PlanIterationAsync`), logs the session context usage as a percentage: "Brain context usage: 45.2% (58000/128000 tokens) after <method-name>"; computed from `InputTokensUsed` vs `_maxContextTokens`; includes 4 xUnit tests verifying percentage calculation, zero-context-window handling, caller name inclusion, and exact message format
- **WorkerPool.ConnectedWorkerCount** — new public read-only property (also on `IWorkerPool`) that returns the current number of registered connected workers; backed by `_workers.Count`; includes XML documentation and 2 xUnit tests (class now has 23 total [Fact] tests)
- `PrintBanner()` now prints the current UTC start time below the ASCII art in the format `Started at yyyy-MM-dd HH:mm:ss UTC`
- **GoalDispatcher source-count startup log** — `ExecuteAsync` now logs the number of registered goal sources at startup (format: "GoalDispatcher starting with {SourceCount} goal source(s)")
- **Brain context compaction logging** — `CompactContextAsync` logs an Information-level message before and after compaction showing token count, message count, and percentage reduction (format: "Brain context compaction: {TokensBefore} → {TokensAfter} tokens ({ReductionPercent}% reduction), {MessagesBefore} → {MessagesAfter} messages")
- **Brain automatic compaction trigger** — context automatically compacts when usage reaches 80% of `_maxContextTokens`
- **Brain CodingAgent migration** — Brain now uses SharpCoder's `CodingAgent` instead of raw `IChatClient`, gaining built-in file tools, session management, and automatic context compaction
- **Single persistent Brain session** — one `AgentSession` carries context across all goals (replaces per-goal `ConcurrentDictionary<string, AgentSession>`)
- **Brain read-only file access** — Brain can read project structure, config, and code via `read_file`, `glob`, `grep` tools on persistent repo clones
- **Sequential goal processing** — goals process one at a time so the Brain accumulates learnings across goals
- **Brain session persistence** — session saved to `brain-session.json` after each Brain call, loaded on startup for crash recovery
- **BrainRepoManager** — manages persistent repo clones at `{stateDir}/repos/{repoName}` for Brain file access and merge operations
- **Merge via brain clone** — `PerformMergeAsync` reuses the brain clone instead of creating temp directories; on failure, clone is reset to clean state
- `BrainRepoManager.MergeFeatureBranchAsync` — fetch, merge, push in the persistent brain clone

### Removed
- `CleanupGoalSessionAsync` and `ReprimeSessionAsync` from `IDistributedBrain` — no longer needed with single persistent session
- Per-goal `AgentSession` management (`GetOrCreateSession`, `_sessions` dictionary)
- `SendToBrainCoreAsync` — replaced by `ExecuteBrainAsync` which delegates to CodingAgent
- `FunctionInvokingChatClient` wrapping in Brain — CodingAgent handles tool invocation internally
- Auto-rebase complexity in merge flow (sequential processing makes conflicts unlikely)

### Changed
- Brain system prompt updated to allow file reading (was "do NOT run tools or file operations")
- Brain `WorkDirectory` set to `repos/` parent folder so all repositories are visible simultaneously
- Updated `DistributedBrain` from `IChatClient` direct usage to `CodingAgent` orchestration
- `GoalDispatcher.DispatchNextGoalAsync` now checks `GetActivePipelines()` before dispatching
- `GoalDispatcher.DispatchNextGoalAsync` now includes the goal's priority level in the dispatch log message (e.g., "Priority=High")

### Added
- `SharpCoderRunnerSummarizeMessageTests` — xUnit test suite with 10 [Fact] tests covering the `SummarizeMessage` helper method via reflection; tests verify tool call logging format, tool result format, argument truncation (100 chars), result preview truncation (200 chars), null handling, and plain text fallback behavior
- `SharpCoderRunner.SendPromptAsync` logging improvements — task execution now logs worker role and model: "Executing task as {role} with model {model}. WorkDir: {workDir}"; task completion logs elapsed time, status, and tool call count: "Task finished in {elapsed}s (status={status}, toolCalls={toolCalls})" using `System.Diagnostics.Stopwatch`
- `SharpCoderRunnerLoggingTests` — xUnit test suite with 7 [Fact] tests and 1 [Theory] (4 cases) verifying `SendPromptAsync` logging: role and model in task start message, elapsed time, status, and tool call count in task completion message
- `GET /health` endpoint now includes a `sharpCoderVersion` field reflecting the version of the SharpCoder NuGet package loaded at runtime (e.g., `"0.2.0.10"`)
- `HealthResponse.SharpCoderVersion` property with XML documentation
- `HealthEndpointTests` — 2 new xUnit tests (`GetHealth_HasSharpCoderVersionField`, `GetHealth_SharpCoderVersion_MatchesSemanticVersionFormat`) to verify the SharpCoder version field presence and semantic version format (14 total tests in the class)
- `WorkerLoggerTests` — xUnit test suite with 22 [Fact] tests covering `WorkerLogger.Info`, `Error`, `Debug`, and `LogBlock` output format; tests verify standard messages, empty messages, special character preservation, stdout/stderr routing, verbose mode behavior, log block truncation logic, and cross-method stream isolation
- `GrpcMapperTests` — comprehensive xUnit test suite with 27 tests (21 [Fact] + 6 [Theory]) covering:
  - WorkTask and TaskResult round-trip conversions (domain → gRPC → domain) preserving all fields
  - Null/empty handling for BranchSpec, TaskMetrics, GitChangeSummary, repositories, and metadata
  - BranchAction and TaskOutcome enum bidirectional mappings (all values)
  - WorkerRole enum mappings via `ToGrpcRole()` and `ToDomainRole()` for all roles (Coder, Tester, Reviewer, DocWriter, Improver, MergeWorker)
  - Edge cases: zero test counts, 0% and 100% coverage, empty task IDs/prompts
  - Unknown enum values throw `InvalidOperationException` (no silent fallbacks); `TaskStatus.Unspecified` and `TaskStatus.InProgress` throw appropriately
- `GET /health` endpoint now includes an `"uptime"` field formatted as `"HH:mm:ss"` (hours:minutes:seconds), allowing hours to exceed 24 for long-running servers; also includes raw `UptimeSpan` (TimeSpan) for programmatic parsing
- `HealthEndpointTests.GetHealth_UptimeField_MatchesHhMmSsFormat()` xUnit test verifies the uptime field is present and matches the expected format
- `GoalId.Validate(string)` static method in new `Configuration/GoalId.cs` for validating goal identifiers — IDs must be non-empty, lowercase kebab-case (letters, digits, hyphens only; no leading/trailing hyphens) to match git branch naming convention
- Goal ID validation in `FileGoalSource.MapToGoal()` — validates each goal ID after parsing from goals.yaml
- Goal ID validation in `ApiGoalSource.AddGoal()` — validates goal ID when adding a new goal via API
- `GoalIdTests` — 6 xUnit tests (5 valid cases: `fix-build-error`, `add-feature`, `abc`, `a1b2`, `a-1-b`; 8 invalid cases covering empty/null, uppercase, spaces, leading/trailing hyphens, underscores)
- `GoalDispatcherDispatchLoggingTests` — xUnit test class with 1 [Fact] test verifying `DispatchNextGoalAsync` logs the goal's priority level (e.g., "Priority=High") in the dispatch log message
- **Phase duration logging in GoalDispatcher** — `DriveNextPhaseAsync` now logs the wall-clock duration of each phase in seconds when it completes (format: "Phase {Phase} for goal {GoalId} completed in {DurationSeconds:F1}s"); uses the pipeline's `PhaseStartedAt` timestamp compared to `DateTime.UtcNow` to calculate the duration
- `GoalDispatcherPhaseDurationLoggingTests` — xUnit test class with 1 [Fact] test verifying the phase duration log message is emitted when a phase completes

### Changed
- Worker message logging in `SharpCoderRunner.cs` — the message loop now uses the new `SummarizeMessage` helper to produce informative summaries:
  - Messages containing `FunctionCallContent` log as `tool:{name}({key}="{value}")` with the first argument (truncated to 100 chars)
  - Messages containing `FunctionResultContent` log as `result:{callId} → "{preview}"` with the result preview (truncated to 200 chars)
  - Plain text messages retain existing behavior (truncated to 200 chars)
  - Previously, `[assistant]` and `[tool]` messages with empty `msg.Text` would log empty content, making debugging difficult
- Test method name typos in `SharpCoderRunnerSummarizeMessageTests` ('Tole' → 'Tool' in several test names)
- `InternalsVisibleTo` attribute in `CopilotHive.Worker.csproj` exposing internals to `CopilotHive.Tests` for unit testing

### Changed
- Orchestrator logging migrated from `Console.WriteLine` to `ILogger` with structured properties:
  - `DistributedBrain.cs` — Brain SDK events (AssistantMessage, Usage, SessionIdle, SessionError) now logged via `ILogger.LogDebug` with structured fields (`Source`, `Length`, `Model`, `InputTokens`, `OutputTokens`, `Cost`, `Duration`, `EventType`) instead of `Console.WriteLine` with formatted strings
  - `Program.cs` — Startup messages (gRPC server ports, Brain connection status, config loading) now logged after application build via `ILogger<Program>` with structured properties
  - `AgentsManager.cs` — AGENTS.md update and rollback operations now logged via injected `ILogger<AgentsManager>?` (optional, null-safe) with structured role names and version info
  - `MetricsTracker.cs` — Iteration metrics recording and test regression warnings now logged via injected `ILogger<MetricsTracker>?` with structured iteration, test, and coverage data
  - `DockerWorkerManager.cs` — Worker spawning, cleanup, and stoppage now logged via injected `ILogger<DockerWorkerManager>?` with structured container, port, role, and model information
- Optional logger injection added to `AgentsManager`, `MetricsTracker`, and `DockerWorkerManager` constructors — when omitted (defaults to null), log output is suppressed for backward compatibility
- Service registration in `Program.cs` updated to inject `ILogger` instances for `AgentsManager` and `MetricsTracker` via dependency injection
- Logging levels assigned consistently: `LogDebug` for SDK events and diagnostic detail, `LogInformation` for key operational events, `LogWarning` for error conditions and alerts

### Added (Previous)
- `GoalDispatcherBuildIterationSummaryTests` — xUnit test class with 2 tests verifying `BuildIterationSummary()` correctly handles `ImproverSkipped` flag: ensures exactly one "Improve" phase entry appears in output (no duplicates) regardless of whether PhaseDurations already recorded an "Improve" entry
- `IterationSummary` YAML-level test for null `TestCounts` omission — verifies that when `IterationSummary.TestCounts` is null, the serialised YAML output omits the `test_counts` key entirely
- `IterationSummary` YAML-level test for null `PhaseResult` — verifies that when a phase result entry in YAML has a null `result` field, `FileGoalSource.ReadGoalsAsync()` throws `InvalidOperationException` rather than silently defaulting
- `ConnectedWorkerTests.cs` — comprehensive xUnit test suite for `ConnectedWorker` class with 6 tests covering constructor initialization, state transitions (MarkBusy/MarkIdle), heartbeat updates, message channel I/O, and stale detection logic.
- `PhaseDurations` tracking in goals.yaml — GoalUpdateMetadata, Goal, and GoalFileEntry now include per-phase wall-clock durations (in seconds); FileGoalSource reads and writes phase_durations on goal completion; GoalDispatcher wires pipeline.Metrics.PhaseDurations to the completion metadata; 4 new xUnit tests verify reading/writing phase durations across multiple scenarios.
- `WorkerUtilizationService` — computes per-role worker utilization metrics from the current pool state; includes overall utilization fraction (0.0–1.0), per-role breakdown, and bottleneck role detection (utilization > 0.8).
- `WorkerUtilizationMetrics` DTO with `OverallUtilization` (double), `RoleBreakdown` (dictionary of role name to utilization fraction), and `BottleneckRoles` (list of role names exceeding 80% utilization).
- `GET /health/utilization` endpoint that returns current worker pool utilization metrics as JSON; registered in `Program.cs` and backed by `WorkerUtilizationService`.
- `GoalPhaseExtensions.ToDisplayName()` extension method that returns human-friendly display names for all `GoalPhase` enum values (e.g. `"Doc Writing"` for `DocWriting`, `"Improvement"` for `Improve`); uses explicit switch expression covering all 9 enum values and throws `InvalidOperationException` for unhandled values.
- `GoalPipeline.GetDisplayName(GoalPhase)` static helper that returns human-friendly display names for all pipeline phases (e.g. `"Doc Writing"` for `DocWriting`, `"Improvement"` for `Improve`).
- `WorkerRoleExtensions.ToDisplayName()` extension method that returns human-friendly display names for all `WorkerRole` enum values (e.g. `"Doc Writer"` for `DocWriter`, `"Merge Worker"` for `MergeWorker`); throws `InvalidOperationException` for unhandled values.
- `GoalExtensions.ToDisplayName()` extension methods for `GoalPriority` (returns "Critical", "High", "Normal", "Low") and `GoalStatus` (returns "Pending", "In Progress", "Completed", "Failed", "Cancelled"); both use explicit switch expressions and throw `InvalidOperationException` for unhandled values.
- `Goal.Notes` list field and `GoalUpdateMetadata.Notes` for recording non-fatal observations in goals.yaml (e.g. "Improver skipped: timeout")
- `IterationMetrics.ImproverSkipped` and `ImproverSkipReason` fields for tracking when the improve phase was skipped
- `IterationPlan.NextPhaseAfter(GoalPhase)` helper method for finding the next phase in a plan
- Brain retry mechanism — `AskAsync` retries up to 2 times on timeout, transient errors, and JSON parse failures (5-second backoff)
- Dirty-worktree safety net — `EnsureCleanWorktreeAsync` re-prompts Copilot up to 2 times if uncommitted changes remain after task execution
- `GitOperations.HasUncommittedChangesAsync()` for detecting dirty worktrees
- `IterationSummary` — structured summary of a completed pipeline iteration, containing iteration number, per-phase results (name, result, duration), test counts, review verdict, and notes; automatically appended to `Goal.IterationSummaries` after each iteration completes (success or failure)
- `PhaseResult` — per-phase execution result (name, result: "pass"/"fail"/"skip", duration_seconds)
- `TestCounts` — aggregate test counts (total, passed, failed) for a test run
- `Goal.IterationSummaries` — list of `IterationSummary` entries tracking metrics from each completed iteration
- `GoalUpdateMetadata.IterationSummary` — optional structured summary to append during goal completion/failure
- `FileGoalSource` now reads and writes `iteration_summaries` in goals.yaml with full round-trip fidelity
- `GoalDispatcher.BuildIterationSummary()` — constructs `IterationSummary` from pipeline metrics, marking the failed phase as "fail" and tracking skipped phases (e.g. improver timeout)
- `IterationSummaryTests` — 7 xUnit tests verifying `IterationSummary` structure, YAML serialisation, round-tripping via `FileGoalSource`, null handling, and multi-iteration appending

### Changed
- Extracted `OrchestratorVersion` string into `Constants.OrchestratorVersion` public const in `Constants.cs`; `HiveOrchestratorService` now references the constant instead of a hardcoded literal.
- Extracted `CleanupIntervalSeconds` and `StaleTimeoutMinutes` constants from `StaleWorkerCleanupService` into a new dedicated static class `CleanupDefaults` (in `CopilotHive.Services`) for improved discoverability and reuse.
- Improve phase is now non-blocking — failures (Brain timeout, dispatch errors) are logged as warnings and recorded in goal notes/metrics; the pipeline continues to Merging instead of failing the goal
- Git diff comparisons now use three-dot syntax (`origin/{baseBranch}...HEAD`) instead of `HEAD~1`, correctly detecting all changes on a feature branch
- DocWriter scope restricted — no longer edits source code (.cs files) or runs builds; reviews XML doc comments and flags issues in `DOC_REPORT.xml_doc_issues` but only modifies .md files
- Brain prompt instructions hardened — "NEVER include git checkout/branch/switch/push commands" and "NEVER include framework-specific build/test commands"
- Improver prompt no longer includes agents.md file contents (files are on disk); only lists file paths, saving thousands of tokens
- Commit instructions in agents.md simplified to single line `git add -A && git commit`
- Brain reviewer instructions include `origin/` prefix for diff commands (worker clones only have remote tracking refs)
- Coverage collection switched from `coverlet.msbuild` to `--collect:"XPlat Code Coverage"` collector approach (resolves package conflict)

### Fixed
- **DELETE `/api/goals/{id}` endpoint validation** — endpoint now returns `400 Bad Request` with message "Only Draft or Failed goals can be deleted" when attempting to delete goals with statuses other than Draft or Failed (previously allowed deletion regardless of status, which was inconsistent with `Composer.DeleteGoalAsync` restrictions); `404 Not Found` returned when goal does not exist; includes 1 xUnit test (`DeleteGoal_PendingGoal_Returns400BadRequest`)
- Duplicate "Improve" phase entries in `BuildIterationSummary()` — when `ImproverSkipped=true` and PhaseDurations already contained an "Improve" entry, the summary would produce two entries for the same phase; now explicitly removes any existing "Improve" entry before appending the skipped one
- Null `Result` field in `PhaseResultEntry` from YAML now throws `InvalidOperationException` instead of silently defaulting to `"pass"`, consistent with codebase convention of no silent fallbacks (treats null Result like null Id)
- Root cause of coder no-ops — Brain was generating `git checkout -b feature/...` commands in coder prompts, causing coders to commit on wrong branches; TaskExecutor then detected 0 changes on the infrastructure branch
- Removed conflicting `coverlet.msbuild` 6.0.2 package (conflicted with `coverlet.collector` 8.0.0)
- BOM removal from `CopilotHive.Worker.csproj` — file now starts with `<Project Sdk="Microsoft.NET.Sdk">` instead of `\uFEFF<Project Sdk="Microsoft.NET.Sdk">`

## Removed
- WORKER_ROLE env var, workers are now always generic.
- Update xunit nuget package to xunit.v3.

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
