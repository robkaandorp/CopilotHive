# Skills

This directory contains skill files that describe how to build, test, and set up
the project. The orchestrator injects these into worker prompts so that framework-specific
commands (like `dotnet build` or `npm test`) are never hardcoded in the orchestration logic.

## Available Skills

| Skill | File | Purpose |
|-------|------|---------|
| build | `build.md` | How to build the project |
| test | `test.md` | How to run tests and read results |
| install-sdk | `install-sdk.md` | How to install the SDK in a worker container |

## Customising Skills

Edit the skill files to match your project's toolchain. For example, a Node.js project
would replace the `dotnet build` command in `build.md` with `npm run build`.

The orchestrator reads these files at runtime and passes their content to workers,
so changes take effect on the next iteration without rebuilding the Docker image.
