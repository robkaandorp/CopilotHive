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

Then run all tests with coverage:

```bash
dotnet test <solution-file> --collect:"XPlat Code Coverage" --results-directory ./TestResults
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

For coverage, parse the Cobertura XML in the TestResults directory:
```bash
cat TestResults/*/coverage.cobertura.xml | grep '<coverage' | head -1
```
The `line-rate` attribute is the coverage percentage (0.37 = 37%).

## Writing New Tests

- Use **xUnit** as the test framework
- Place tests in the `tests/` directory
- Name test methods: `MethodName_Scenario_ExpectedBehavior`
- Follow Arrange-Act-Assert pattern
