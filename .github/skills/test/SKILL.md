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

Then run all tests:

```bash
dotnet test <solution-file>
```

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