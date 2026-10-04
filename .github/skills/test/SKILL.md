---
name: test
description: How to run tests and interpret the results. Use this when you need to run the test suite or write new tests.
---

# Test Skill

## How to Run Tests

**Prerequisite:** if `command -v dotnet` fails, load and follow the `install-dotnet-sdk`
skill first. Do not record the exit-127 run as a test result.

First, locate the solution file:

```bash
find . -name '*.slnx' -o -name '*.sln' | head -3
```

Then run the full suite with the Linux/bash worker recipe below. `dotnet test` runs in **Microsoft.Testing.Platform (MTP)** mode for this repository (opted in via `global.json`), so a solution file is passed with `--solution`. MTP needs the built test executable: `dotnet test` alone builds the Debug configuration and runs it, whereas `--no-build` skips the build and runs whatever the selected configuration last produced — so if you pass `--no-build` you must also pass the matching configuration (e.g. `-c Release --no-build` after `dotnet build CopilotHive.slnx -c Release`) or the run fails with "Zero tests ran" and exit code 1. This is a foreground shell recipe, not a promise of `cmd.exe` portability. The `timeout_ms=900000` argument is passed outside the shell command as an `execute_bash_command` tool argument; it is an optional positive-millisecond tool argument, not a `dotnet` flag, `AgentOptions` property, sub-agent session timeout, or test assertion timeout.

Choose and record a unique absolute log path under `/tmp` before launching the long call (for example, `/tmp/copilothive-test-attempt-20260731-123903.log`). Keep each attempt in a separate log. Do not rely on a path printed inside the long call: the SDK may discard stdout when a call times out.

```text
execute_bash_command(
  timeout_ms=900000,
  command="set -o pipefail; dotnet test --solution <solution-file> > /tmp/copilothive-test-attempt-20260731-123903.log 2>&1; status=$?; printf '\\nCOPILOTHIVE_VALIDATION_COMPLETE exit_status=%s\\n' \"$status\" >> /tmp/copilothive-test-attempt-20260731-123903.log; tail -n 80 /tmp/copilothive-test-attempt-20260731-123903.log; exit \"$status\""
)
```

The marker is written only after the test process finishes. Capture the original exit status immediately, append the completion marker, show a bounded tail only as a display preview, and exit with the captured status so a failed test run cannot be masked. This still holds in MTP mode: the process exits non-zero when tests fail (the platform reports a non-success exit code such as `2` for failed tests, `8` for zero tests discovered), so the marker doubles as the failure signal. Inspect the complete log for test counts, final summaries, and failure details; the tail is not evidence of the complete result. `timeout_ms` requires SharpCoder 0.20.0 or newer. Existing running workers do not gain this capability merely by reading updated skill text; deployment/runtime pickup is required.

## Reading Results

After running tests, look for the test run summary block. Example output from this repository (MTP mode, xunit.v3 4.x):

```
Test run summary: Passed!
  total: 11127
  failed: 0
  succeeded: 11126
  skipped: 1
  duration: 5m 00s 698ms
```

Record:
- **total_tests**: the `total` count
- **passed_tests**: the `succeeded` count
- **failed_tests**: the `failed` count

`skipped` is reported alongside them. The run also prints a per-assembly line such as
`... CopilotHive.Tests.dll (net10.0|x64) passed (4m 56s 611ms)`, or `failed with N error(s)`
followed by `Exit code: <n>` when tests fail. Use the `Test run summary:` block for the counts —
it is the MTP counterpart of VSTest's `Passed!  - Failed: 0, Passed: …` line.

## Running a Targeted Subset

When you only changed a specific area, run just the relevant tests instead of the full suite.
The VSTest-style filter syntax still works in MTP mode with xunit.v3 4.x:

```bash
dotnet test --solution <solution-file> --filter "FullyQualifiedName~<NamespaceOrTestClass>"
```

Run the tests for the namespaces/classes your change touches, e.g. (688 tests selected):

```bash
dotnet test --solution <solution-file> --filter "FullyQualifiedName~ConfigRepoGitOperationsTests"
```

Join multiple filters with `|` (33 tests selected, 24 + 9):

```bash
dotnet test --solution <solution-file> --filter "FullyQualifiedName~StaleWorkerCleanupServiceTests|FullyQualifiedName~DurationFormatterTests"
```

MTP also offers its own filter options (`--filter-class`, `--filter-namespace`, `--filter-method`,
`--filter-query`) if you prefer them; the `--filter "FullyQualifiedName~…"` form is kept because
it works unchanged. A filter that matches nothing exits non-zero (code 8) instead of silently
passing, so a typo is never mistaken for a green subset.

## Opt-in Coverage

Coverage collection is opt-in, not part of the default test run. This repository uses the
Microsoft.Testing.Platform coverage extension (`Microsoft.Testing.Extensions.CodeCoverage`):

```bash
dotnet test --solution <solution-file> --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory ./TestResults
```

The run prints the generated artifact (`In process file artifacts produced:
- .../TestResults/coverage.cobertura.xml`) and still reports the normal test summary. Add
`-c Release --no-build` (matching the configuration you built) when a build already exists — a
bare `--no-build` selects the default Debug configuration and fails with "Zero tests ran".

Caveat: re-verified in MTP mode with the CodeCoverage extension — the historical
`WebApplicationFactory` `BadImageFormatException`/exit-135 teardown crash was **not re-observed**
(full suite with coverage: exit 0, no such failures). It is unproven rather than disproven, so a
coverage run must still NEVER be the single gating run — if a coverage run fails
WebApplicationFactory tests, re-run without coverage before concluding anything.

## Writing New Tests

- Use **xUnit** as the test framework
- Place tests in the `tests/` directory
- Name test methods: `MethodName_Scenario_ExpectedBehavior`
- Follow Arrange-Act-Assert pattern