# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] - 2025-07-15

### Added

**Composer Agent & Chat UI.** A conversational Composer agent at `/composer` provides streaming chat for goal decomposition and management. It offers goal CRUD tools (create, approve, update, delete, cancel, list, search), codebase inspection tools, five read-only git tools, web search and fetch via Ollama, phase output inspection with brain and worker prompt access, repository listing, and interactive user questions with multiple-choice and free-text responses. The chat interface supports Enter-to-send, auto-scroll, full Markdown rendering, session reset with confirmation, and automatic context overflow recovery that resets the session and notifies the user.

**Brain & Orchestration.** The Brain now uses SharpCoder's CodingAgent with a single persistent session that carries context across all goals. It has read-only file access to all target repositories via built-in file tools on clones eagerly created at startup, automatic context compaction when usage approaches the limit, and session persistence to disk for crash recovery. Goals process sequentially so the Brain accumulates learnings across iterations. The Brain also generates concise commit messages for squash merges, falling back to a deterministic format on failure.

**Dashboard & UI.** The Goals browser at `/goals` provides search, status, priority, and repository filters with a sortable table and inline actions. The Goal detail page shows collapsible Brain and worker prompts, a Planning phase with reasoning display, dependency visualization with per-dependency status indicators, worker output rendered as Markdown, and clickable merge commit hash links. A Configuration page offers YAML syntax highlighting and Markdown rendering for agent files. A Releases page supports release creation, detail view, and goal-to-release assignment. The CopilotHive version is displayed in the navigation bar from assembly metadata.

**Goal Management.** Goals are persisted in SQLite as the primary store, with full CRUD exposed through a REST API. The API supports approval (Draft→Pending), cancellation of in-progress goals, deletion of draft and failed goals with remote branch cleanup, and revert to Draft for pending goals. Goals carry a scope (Patch, Feature, Breaking), an optional dependency list of prerequisite goal IDs, and an optional release assignment. Goal identifiers are validated as lowercase kebab-case. Status transitions are validated at both the API and Composer layers.

**Worker Sessions.** Workers support per-role session persistence via gRPC, allowing agent conversations to resume across iterations rather than starting fresh each time. The gRPC protocol exchanges session state between the orchestrator and worker containers, ensuring continuity within a role.

**Pipeline Features.** The DocWriting phase is conditionally included or skipped based on goal content, reducing unnecessary work. Feature branches are squash-merged with Brain-generated commit messages, and the resulting merge commit hash is captured, stored on the goal, and displayed as a clickable link. Worker output from each pipeline phase is stored inline on the iteration summary and persisted to both SQLite and YAML. The improver phase is non-blocking: failures are recorded in goal notes and metrics but never abort the pipeline.

**Infrastructure.** A GitHub Actions CI workflow runs on pushes to the develop branch, executing the full test suite before building and pushing multi-architecture Docker images (amd64 and arm64) to GHCR using cross-compilation to avoid QEMU segfaults. HTTP calls to LLM APIs use a resilience policy with retries and exponential backoff. All orchestrator logging has been migrated to structured logging with named properties throughout.

**Observability.** The Brain logs context usage percentage after every call and token and message counts after compaction. Phase wall-clock durations are tracked and logged on completion. Task start and completion logs include the assigned model name and elapsed time. A worker utilization endpoint reports per-role breakdown and identifies bottleneck roles. The startup banner includes the UTC start time and goal source count.

### Changed

**SharpCoder & Core Pipeline.** SharpCoder was updated from 0.4.4 to 0.5.0, enabling the CodingAgent-based Brain and worker session persistence. The pipeline phase order is now Coding → Testing → Review → DocWriting → Improve → Merge. The orchestrator runs exclusively in server mode — the CLI flag has been removed. The Brain's working directory is set to the repos parent folder so all repositories are visible simultaneously, and the Brain system prompt now permits file reading and explicitly forbids git branch management commands and framework-specific build commands.

**Goal Storage, Workers & DocWriter.** SQLite is now the sole goal source; the file-based source is used only for YAML round-tripping. Docker Compose uses a single generic worker service with a configurable replica count, replacing fixed-role containers. The DocWriter role is restricted to documentation files only and no longer edits source code or runs builds; it instead reviews XML doc comments and flags issues. The improver prompt references file paths rather than file contents, and the coverage collector was switched to the XPlat approach to eliminate a package conflict.

### Fixed

**Stability & Correctness.** Startup no longer fails when cloning an empty repository. Worker container restarts use a clone-or-pull pattern to survive restarts cleanly. Session save operations no longer re-throw cancellation exceptions. Brain prompt instructions were hardened to prevent generation of git branch and checkout commands, eliminating the root cause of coder no-op commits on the wrong branch. Duplicate entries in the iteration summary's improve phase are now removed before appending. Null phase results throw explicitly rather than silently defaulting to a passing verdict. The SQLite readonly error in CI was resolved by eliminating test collection sharing. BrainRepoManager tests use isolated temporary directories. The Coverlet package conflict was resolved by removing the conflicting variant.

**UI & API.** The releases form repository dropdown now shows all configured repositories. Delete errors on the Goal detail page are shown as inline messages. Dependency links use an accent color for readability on dark backgrounds. The health endpoint uptime field is formatted to support durations exceeding 24 hours. Goal deletion enforces status restrictions correctly, returning a clear error for non-deletable states. Status transition validation is consistent between the API and Composer. Web search results are truncated per-result to prevent context overflow, and web fetch defaults to a shorter line limit. The diagnostics file path defaults to the system temp directory rather than a hardcoded app path.

### Removed

**Legacy Code & Per-Goal Sessions.** The legacy CLI-mode orchestrator, legacy Copilot client abstraction layer, and all associated dead code paths were removed, eliminating thousands of lines of unused infrastructure. Per-goal agent session management was replaced entirely by the single persistent Brain session. Fixed-role Docker Compose services were replaced by the generic worker pool. The goals.yaml tab was removed from the Configuration page since goals are now managed through the Goals page. The conflicting Coverlet package and the empty metrics folder were also removed.
