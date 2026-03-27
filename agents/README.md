# Agent Templates

These `*.agents.md` files are **default templates** for a CopilotHive-style multi-agent
software development workflow. They define the behaviour of each worker role.

## Roles

| File | Role | Responsibility |
|------|------|----------------|
| `orchestrator.agents.md` | Orchestrator / Product Owner | Plans iterations, crafts worker prompts |
| `coder.agents.md` | Coder | Implements changes, writes unit tests, commits |
| `tester.agents.md` | Tester | Verifies builds, runs tests, writes integration tests |
| `reviewer.agents.md` | Reviewer | Reviews diffs for correctness and quality |
| `docwriter.agents.md` | Doc Writer | Updates CHANGELOG, README, and inline docs |
| `improver.agents.md` | Improver | Refines agent instructions based on iteration results |

## Customising for Your Project

Copy these templates and edit them to match your project's conventions:

- **Tech stack**: Update the coder's documentation and thread-safety sections to match your language.
- **Build & test tooling**: Update references to build/test skills to match your project's commands.
- **Doc style**: Update the doc writer's inline documentation guidance to match your language's
  conventions (e.g. JSDoc, docstrings, XML doc comments).
- **Quality standards**: Adjust the orchestrator's quality standards and phase list to match
  your team's requirements.

## Runtime Override

At runtime, CopilotHive loads agent instructions from a config repository.
The config repo's `agents/` folder takes precedence — any matching `*.agents.md` file
in the config repo overrides the local template.

## Self-Improvement

The **Improver** worker reads iteration metrics (test results, retry counts, issues) and edits
these files directly to improve agent behaviour over time — creating a self-improving feedback loop.

The `history/` subfolder is used by AgentsManager for version tracking and rollback.
