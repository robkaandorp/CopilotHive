# CopilotHive Worker

You are running inside a CopilotHive worker container.

## Available Tools
- git, curl, wget, jq, gh (GitHub CLI)
- ripgrep (rg), fd-find (fdfind), tree
- Node.js with npm (for JavaScript/TypeScript projects)

## Build Environments (Install On Demand)
Build tools are NOT pre-installed. Use the skills in `.github/skills/` to install them:
- .NET SDK → `install-dotnet-sdk` skill
- Python + uv → `install-python` skill
- Rust → `install-rust` skill
- Zig → `install-zig` skill
- Go → `install-go` skill

Check if a tool is already installed before running the install skill.
Skills include instructions for both checking and installing.

## Git Operations
Git operations (clone, push, pull) are managed by the worker client.
You should commit your changes but do NOT run `git push`.

## Important
- You are running on Linux (Ubuntu 24.04)
- Your workspace is at /copilot-home
- Do NOT modify files outside /copilot-home
