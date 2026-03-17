# Test Skill

## How to Run Tests

First, locate the solution file:

```bash
find . -name '*.slnx' -o -name '*.sln' | head -3
```

Then run all tests with coverage:

```bash
dotnet test <solution-file> /p:CollectCoverage=true /p:CoverletOutputFormat=text
```

## Reading Results

After running tests, look for the test summary line. Example output:

```
Passed!  - Failed:     0, Passed:   322, Skipped:     0, Total:   322, Duration: 3s
```

Record:
- **total_tests**: the Total count
- **passed_tests**: the Passed count
- **failed_tests**: the Failed count
- **coverage_percent**: the line coverage percentage from the Coverlet table (look for the TOTAL row)

## Writing New Tests

- Use **xUnit** as the test framework
- Place tests in the `tests/` directory
- Name test methods: `MethodName_Scenario_ExpectedBehavior`
- Follow Arrange-Act-Assert pattern
