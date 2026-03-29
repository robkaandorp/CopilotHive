# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] - 2025-07-15

### Added

**Composer & Chat UI:** Composer agent with Chat UI, ask_user with markdown rendering, web_search and web_fetch tools, git tools, list_repositories, get_phase_output with content parameter for brain/worker prompts, phase detail tool.

**Dashboard & UI:** Goals REST API and browser dashboard, goal detail page with inline prompts, planning phase display, dependency visualization, worker output rendering, goal approval/deletion/cancellation from dashboard, configuration page with YAML highlighting and markdown rendering, version display in navigation, release management with Releases page and goal assignment.

**Pipeline & Orchestration:** Brain CodingAgent migration with persistent session, Brain read-only file access and BrainRepoManager, conditional DocWriting phase, goal dependency support (DependsOn), goal cancellation and status management, goal scope (Patch/Feature/Breaking), release management entity with CRUD API and SQLite persistence, per-role worker session persistence via gRPC GetSession/SaveSession.

**Infrastructure:** SQLite goal store (SqliteGoalStore), eager repository cloning at startup, squash merge with Brain-generated commit messages, merge commit hash capture and display, worker output persistence, GitHub Actions CI workflow, multi-architecture Docker support.

**Observability:** Push error visibility, HTTP resilience, structured logging migration, conversation entry metadata, worker utilization metrics, diagnostics file default path (temp directory).

### Changed

**SharpCoder:** NuGet reference updated to 0.5.0.

### Fixed

- Empty repository clone failure.
- Reviewer scope enforcement.
- Eager push CurrentModel display.
- Release form repository dropdown.
- Runner reset test in CI (factory delegate).
- Session save cancellation (no re-throw).

### Removed
