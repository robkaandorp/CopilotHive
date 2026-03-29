# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] - 2025-07-15

### Added
**Composer Chat UI** — Composer agent provides a conversational interface for task creation and management
**Composer ask_user** — Composer prompts the user for input with markdown rendering support
**Composer web_search** — Composer can search the web for up-to-date information
**Composer web_fetch** — Composer can fetch and summarize web pages
**Composer git tools** — Composer can read files, run shell commands, create branches, and push changes
**Composer list_repositories** — Composer lists all configured repositories
**Composer get_phase_output** — Composer retrieves phase output by name
**Composer phase detail** — Composer can view per-phase detail via the phase detail tool
**Composer update_goal** — Composer can update a goal's properties including release assignment
**Goals REST API** — Browse, view, create, update, approve, and cancel goals
**Goal detail page** — Inline prompts, planning phase, dependency visualization, worker output rendering
**SQLite goal store** — Goals persisted to SQLite with full CRUD via SqliteGoalStore
**Brain CodingAgent** — Brain uses CodingAgent for orchestrating development tasks with persistent session
**Brain read-only file access** — Brain reads repository files with BrainRepoManager
**Release management** — Release entity with CRUD API, SQLite persistence, and dashboard UI
**Goal scope** — Goals have scope field (Patch/Feature/Breaking) enforced by reviewer
**Conditional DocWriting** — Brain conditionally includes DocWriting based on goal description
**Eager repository cloning** — Brain clones all repositories at startup for immediate file access
**Worker session persistence** — AgentSession stored per-role via gRPC GetSession/SaveSession
**Squash merge** — Brain generates commit messages for squash merges
**Merge commit capture** — Merge commit hash captured and displayed on goal detail
**Worker output persistence** — Worker output stored and rendered in goal detail page
**Goal cancellation** — Goals can be cancelled from the dashboard
**Goal status management** — Status transitions validated, goals can revert to draft
**CI workflow** — GitHub Actions CI workflow for building, testing, and publishing
**Multi-arch Docker** — Docker images built for multiple architectures
**Push error visibility** — Push errors surfaced in dashboard
**HTTP resilience** — HTTP client configured with retry and timeout policies
**Structured logging** — Application uses structured logging with Serilog
**Configuration page** — Dashboard configuration page with YAML highlighting and markdown rendering
**ConversationEntry metadata** — Conversation entries include metadata fields
**Worker utilization metrics** — Dashboard displays worker utilization and metrics
**Version display** — CopilotHive version shown in dashboard nav/footer
**Version infrastructure** — VersionPrefix in Directory.Build.props for CI-driven versioning

### Changed
**SharpCoder NuGet reference** — Updated from 0.4.4 to 0.5.0

### Fixed
**Empty repository clone failure** — Orchestrator no longer fails on empty GitHub repositories at startup
**Reviewer scope enforcement** — Reviewer prompt includes scope rules matching GoalScope
**Eager push CurrentModel display** — Dashboard shows correct model during premium tier eager pushes
**Release form repository dropdown** — Release form shows all configured repositories
**Diagnostics file default path** — Changed from /app/diagnostics to system temp directory
**Runner reset test in CI** — ResetSessionAsync test uses factory delegate instead of GH_TOKEN
**Session save cancellation** — SaveSessionAsync no longer re-throws OperationCanceledException

### Removed
