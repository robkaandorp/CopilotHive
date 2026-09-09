---
name: build
description: How to build the project. Use this when you need to compile or build the codebase.
---

# Build Skill

## How to Build

First, locate the solution file:

```bash
find . -name '*.slnx' -o -name '*.sln' | head -3
```

Then build with the Linux/bash worker recipe below. This is a foreground shell recipe, not a promise of `cmd.exe` portability. The `timeout_ms=900000` argument is passed outside the shell command as an `execute_bash_command` tool argument; it is an optional positive-millisecond tool argument, not a `dotnet` flag, `AgentOptions` property, sub-agent session timeout, or test assertion timeout.

Choose and record a unique absolute log path under `/tmp` before launching the long call (for example, `/tmp/copilothive-build-attempt-20260731-123903.log`). Keep each attempt in a separate log. Do not rely on a path printed inside the long call: the SDK may discard stdout when a call times out.

```text
execute_bash_command(
  timeout_ms=900000,
  command="set -o pipefail; dotnet build <solution-file> > /tmp/copilothive-build-attempt-20260731-123903.log 2>&1; status=$?; printf '\\nCOPILOTHIVE_VALIDATION_COMPLETE exit_status=%s\\n' \"$status\" >> /tmp/copilothive-build-attempt-20260731-123903.log; tail -n 80 /tmp/copilothive-build-attempt-20260731-123903.log; exit \"$status\""
)
```

The marker is written only after the build process finishes. Capture the original exit status immediately, append the completion marker, show a bounded tail only as a display preview, and exit with the captured status so a failed build cannot be masked. Inspect the complete log for diagnostics and final summaries; the tail is not evidence of the complete result. `timeout_ms` requires SharpCoder 0.20.0 or newer. Existing running workers do not gain this capability merely by reading updated skill text; deployment/runtime pickup is required.

If the build fails, read the complete log and error messages carefully and fix the issues before proceeding.
