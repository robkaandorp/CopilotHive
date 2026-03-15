# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
