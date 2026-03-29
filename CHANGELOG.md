# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] - 2025-07-15

### Added

**Composer Agent & Chat UI.** A conversational Composer agent at `/composer` provides streaming chat for goal decomposition and management. It offers goal CRUD tools, codebase inspection, git operations, web search and fetch capabilities, phase output inspection, repository listing, and interactive user questions. The interface supports keyboard shortcuts, auto-scroll, full Markdown rendering, session reset, and automatic context overflow recovery.

**Brain & Orchestration.** The Brain now operates as a Composer with read-only access to all target repositories, manages memory automatically when usage approaches the limit, and persists its session across crashes. Goals process sequentially so the Brain accumulates learnings across iterations. The Brain also generates concise commit messages for squash merges, falling back to a deterministic format on failure.

**Dashboard & UI.** The Goals browser at `/goals` provides search, status, priority, and repository filters with a sortable table and inline actions. The Goal detail page shows collapsible prompts, a Planning phase with reasoning display, dependency visualization with status indicators, worker output rendered as Markdown, and clickable merge commit links. A Configuration page offers syntax highlighting and Markdown rendering for agent files. A Releases page supports release creation and goal-to-release assignment. The version is displayed in the navigation bar from assembly metadata.

**Goal Management.** Goals are persisted in SQLite as the primary store, with full CRUD exposed through a REST API. The API supports approval, cancellation of in-progress goals, deletion with remote branch cleanup, and revert to Draft for pending goals. Goals can have dependencies and be assigned to releases, with status transitions validated at both the API and Composer layers.

**Worker Sessions.** Workers support per-role session persistence, allowing agent conversations to resume across iterations rather than starting fresh each time. Session state flows between the orchestrator and worker containers, maintaining continuity throughout a role's lifecycle. This preserves context across the coding, testing, and review phases for each worker assignment.

**Pipeline Features.** The DocWriting phase is conditionally included based on goal content, reducing unnecessary work. Feature branches are squash-merged with Brain-generated commit messages, and the resulting merge commit hash is captured and displayed as a clickable link. Worker output from each phase is stored inline on the iteration summary and persisted to both SQLite and YAML. The improver phase is non-blocking: failures are recorded in goal notes and metrics but never abort the pipeline.

**Infrastructure.** A GitHub Actions CI workflow runs on pushes to the develop branch, executing the full test suite before building and pushing multi-architecture Docker images to GHCR. HTTP calls to LLM APIs use a resilience policy with retries and exponential backoff. Logging has been migrated to structured logging with named properties throughout.

**Observability.** The Brain logs context usage after every call and token counts after compaction. Phase durations are tracked and logged on completion. Task logs include the assigned model name and elapsed time. A worker utilization endpoint reports per-role breakdown and identifies bottlenecks. The startup banner includes the UTC start time and goal source count.

### Changed

**SharpCoder & Core Pipeline.** SharpCoder was updated from 0.4.4 to 0.5.0, enabling persistent Brain and worker sessions. The pipeline phase order is now Coding → Testing → Review → DocWriting → Improve → Merge. The orchestrator runs exclusively in server mode — the CLI flag has been removed. The Brain is configured to access all target repositories simultaneously and is explicitly restricted from issuing advanced git operations.

**Goal Storage, Workers & DocWriter.** SQLite is now the sole goal source; the file-based source is used only for configuration file editing. Docker Compose uses a single generic worker service with configurable replicas, replacing fixed-role containers. The DocWriter role is restricted to documentation files only and no longer edits source code or runs builds, instead reviewing XML doc comments and flagging issues.

### Fixed

**Stability & Correctness.** Startup now handles empty repository clones, and worker containers reconnect automatically after crashes. Brain restrictions prevent generation of branch operations on the wrong branch, eliminating no-op commits. Session saves no longer re-throw cancellations, and null phase results throw explicitly rather than defaulting silently.

**UI & API.** The releases form shows all configured repositories, and delete errors appear as inline messages on the Goal detail page. Dependency links use accent colors for dark mode readability. Goal deletion enforces status restrictions with consistent validation between the API and Composer. Web search results are truncated to prevent overflow.

### Removed

**Legacy Code & Sessions.** The legacy CLI-mode orchestrator, legacy Copilot client abstraction layer, and all associated dead code paths were removed, eliminating thousands of lines of unused infrastructure. Per-goal agent session management was replaced by the single persistent Brain session. The goals.yaml tab was removed from the Configuration page, and a conflicting coverage package was eliminated.
