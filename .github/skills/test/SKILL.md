---
name: test
description: How to run tests and interpret the results. Use this when you need to run the test suite or write new tests.
---

# Test Skill

## How to Run Tests

First, locate the solution file:

```bash
find . -name '*.slnx' -o -name '*.sln' | head -3
```

Then run the full suite with the Linux/bash worker recipe below. This is a foreground shell recipe, not a promise of `cmd.exe` portability. The `timeout_ms=900000` argument is passed outside the shell command as an `execute_bash_command` tool argument; it is an optional positive-millisecond tool argument, not a `dotnet` flag, `AgentOptions` property, sub-agent session timeout, or test assertion timeout.

Choose and record a unique absolute log path under `/tmp` before launching the long call (for example, `/tmp/copilothive-test-attempt-20260731-123903.log`). Keep each attempt in a separate log. Do not rely on a path printed inside the long call: the SDK may discard stdout when a call times out.

```text
execute_bash_command(
  timeout_ms=900000,
  command="set -o pipefail; dotnet test <solution-file> > /tmp/copilothive-test-attempt-20260731-123903.log 2>&1; status=$?; printf '\\nCOPILOTHIVE_VALIDATION_COMPLETE exit_status=%s\\n' \"$status\" >> /tmp/copilothive-test-attempt-20260731-123903.log; tail -n 80 /tmp/copilothive-test-attempt-20260731-123903.log; exit \"$status\""
)
```

The marker is written only after the test process finishes. Capture the original exit status immediately, append the completion marker, show a bounded tail only as a display preview, and exit with the captured status so a failed test run cannot be masked. Inspect the complete log for test counts, final summaries, and failure details; the tail is not evidence of the complete result. `timeout_ms` requires SharpCoder 0.20.0 or newer. Existing running workers do not gain this capability merely by reading updated skill text; deployment/runtime pickup is required.

## Reading Results

After running tests, look for the test summary line. Example output:

```
Passed!  - Failed:     0, Passed:   418, Skipped:     0, Total:   418, Duration: 13s
```

Record:
- **total_tests**: the Total count
- **passed_tests**: the Passed count
- **failed_tests**: the Failed count

## Running a Targeted Subset

When you only changed a specific area, run just the relevant tests instead of the full suite:

```bash
dotnet test <solution-file> --filter "FullyQualifiedName~<NamespaceOrTestClass>"
```

Run the tests for the namespaces/classes your change touches, e.g.:

```bash
dotnet test <solution-file> --filter "FullyQualifiedName~ConfigRepoGitOperationsTests"
```

Join multiple filters with `|`:

```bash
dotnet test <solution-file> --filter "FullyQualifiedName~NsOne|FullyQualifiedName~NsTwo"
```

## Opt-in Coverage

Coverage collection is opt-in, not part of the default test run:

```bash
dotnet test <solution-file> --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

Caveat: under the collector, WebApplicationFactory-based tests are unreliable
(transient/deterministic `BadImageFormatException`; the exit-135 teardown crash).
A coverage run must NEVER be the single gating run — if a coverage run fails
WebApplicationFactory tests, re-run without coverage before concluding anything.

## Writing New Tests

- Use **xUnit** as the test framework
- Place tests in the `tests/` directory
- Name test methods: `MethodName_Scenario_ExpectedBehavior`
- Follow Arrange-Act-Assert pattern