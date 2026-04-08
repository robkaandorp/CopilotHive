using CopilotHive.Metrics;
using CopilotHive.Workers;

#pragma warning disable CS0618 // Obsolete members tested for backward compatibility

namespace CopilotHive.Tests.Metrics;

/// <summary>
/// Unit tests for <see cref="IterationMetrics"/> covering default values, computed
/// properties, and data recorded via direct property assignment.
/// </summary>
public sealed class IterationMetricsTests
{
    // ── Default values ───────────────────────────────────────────────────────

    /// <summary>
    /// A freshly constructed <see cref="IterationMetrics"/> must have sensible zero/false
    /// defaults for every numeric and boolean property.
    /// </summary>
    [Fact]
    public void NewInstance_NumericProperties_DefaultToZero()
    {
        var m = new IterationMetrics();

        Assert.Equal(0, m.Iteration);
        Assert.Equal(0, m.TotalTests);
        Assert.Equal(0, m.PassedTests);
        Assert.Equal(0, m.FailedTests);
        Assert.Equal(0.0, m.CoveragePercent);
        Assert.Equal(0, m.IntegrationTestsTotal);
        Assert.Equal(0, m.IntegrationTestsPassed);
        Assert.Equal(0, m.ReviewIssuesFound);
        Assert.Equal(0, m.RetryCount);
        Assert.Equal(0, m.ReviewRetryCount);
        Assert.Equal(0, m.TestRetryCount);
        Assert.Equal(0, m.PromptTokens);
        Assert.Equal(0, m.CompletionTokens);
    }

    /// <summary>
    /// A freshly constructed instance must have false for all boolean properties.
    /// </summary>
    [Fact]
    public void NewInstance_BooleanProperties_DefaultToFalse()
    {
        var m = new IterationMetrics();

        Assert.False(m.BuildSuccess);
        Assert.False(m.RuntimeVerified);
        Assert.False(m.ImproverSkipped);
    }

    /// <summary>
    /// A freshly constructed instance must have empty collections and null optional fields.
    /// </summary>
    [Fact]
    public void NewInstance_CollectionsAndOptionals_HaveCorrectDefaults()
    {
        var m = new IterationMetrics();

        Assert.Empty(m.Issues);
        Assert.Empty(m.ReviewIssues);
        Assert.Empty(m.PhaseDurations);
        Assert.Empty(m.AgentsMdVersions);
        Assert.Empty(m.Custom);
        Assert.Equal(string.Empty, m.TestReportSummary);
        Assert.Null(m.Verdict);
        Assert.Null(m.ReviewVerdict);
        Assert.Null(m.ImproverSkipReason);
    }

    /// <summary>
    /// <see cref="IterationMetrics.Timestamp"/> is initialised to a UTC timestamp that
    /// is close to <see cref="DateTime.UtcNow"/> at construction time.
    /// </summary>
    [Fact]
    public void NewInstance_Timestamp_IsCloseToUtcNow()
    {
        var before = DateTime.UtcNow;
        var m = new IterationMetrics();
        var after = DateTime.UtcNow;

        Assert.InRange(m.Timestamp, before, after);
        Assert.Equal(DateTimeKind.Utc, m.Timestamp.Kind);
    }

    // ── PassRate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IterationMetrics.PassRate"/> must return the correct ratio when there
    /// are tests.
    /// </summary>
    [Theory]
    [InlineData(10, 10, 1.0)]
    [InlineData(10, 8,  0.8)]
    [InlineData(10, 5,  0.5)]
    [InlineData(10, 0,  0.0)]
    [InlineData(1,  1,  1.0)]
    public void PassRate_WithTests_ReturnsCorrectRatio(int total, int passed, double expected)
    {
        var m = new IterationMetrics { TotalTests = total, PassedTests = passed };
        Assert.Equal(expected, m.PassRate);
    }

    /// <summary>
    /// When there are no tests, <see cref="IterationMetrics.PassRate"/> must return 0
    /// instead of dividing by zero.
    /// </summary>
    [Fact]
    public void PassRate_ZeroTotalTests_ReturnsZero()
    {
        var m = new IterationMetrics { TotalTests = 0, PassedTests = 0 };
        Assert.Equal(0.0, m.PassRate);
    }

    // ── IntegrationPassRate ──────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IterationMetrics.IntegrationPassRate"/> must return the correct ratio
    /// when integration tests exist.
    /// </summary>
    [Theory]
    [InlineData(4, 4, 1.0)]
    [InlineData(4, 3, 0.75)]
    [InlineData(4, 0, 0.0)]
    public void IntegrationPassRate_WithTests_ReturnsCorrectRatio(int total, int passed, double expected)
    {
        var m = new IterationMetrics
        {
            IntegrationTestsTotal = total,
            IntegrationTestsPassed = passed,
        };
        Assert.Equal(expected, m.IntegrationPassRate);
    }

    /// <summary>
    /// When there are no integration tests, <see cref="IterationMetrics.IntegrationPassRate"/>
    /// must return 0 to avoid a divide-by-zero.
    /// </summary>
    [Fact]
    public void IntegrationPassRate_ZeroTotal_ReturnsZero()
    {
        var m = new IterationMetrics { IntegrationTestsTotal = 0, IntegrationTestsPassed = 0 };
        Assert.Equal(0.0, m.IntegrationPassRate);
    }

    // ── Recording / mutation ─────────────────────────────────────────────────

    /// <summary>
    /// All metrics properties are mutable and can be set after construction.
    /// Verifies that written values round-trip correctly.
    /// </summary>
    [Fact]
    public void Properties_CanBeSetAndReadBack()
    {
        var m = new IterationMetrics
        {
            Iteration = 3,
            BuildSuccess = true,
            TotalTests = 50,
            PassedTests = 48,
            FailedTests = 2,
            CoveragePercent = 91.5,
            IntegrationTestsTotal = 10,
            IntegrationTestsPassed = 9,
            RuntimeVerified = true,
            Verdict = TaskVerdict.Pass,
            TestReportSummary = "All good",
            ReviewIssuesFound = 2,
            ReviewVerdict = ReviewVerdict.Approve,
            RetryCount = 1,
            ReviewRetryCount = 1,
            TestRetryCount = 0,
            PromptTokens = 2000,
            CompletionTokens = 800,
            ImproverSkipped = true,
            ImproverSkipReason = "timeout",
            Duration = TimeSpan.FromMinutes(5),
        };

        Assert.Equal(3, m.Iteration);
        Assert.True(m.BuildSuccess);
        Assert.Equal(50, m.TotalTests);
        Assert.Equal(48, m.PassedTests);
        Assert.Equal(2, m.FailedTests);
        Assert.Equal(91.5, m.CoveragePercent);
        Assert.Equal(10, m.IntegrationTestsTotal);
        Assert.Equal(9, m.IntegrationTestsPassed);
        Assert.True(m.RuntimeVerified);
        Assert.Equal(TaskVerdict.Pass, m.Verdict);
        Assert.Equal("All good", m.TestReportSummary);
        Assert.Equal(2, m.ReviewIssuesFound);
        Assert.Equal(ReviewVerdict.Approve, m.ReviewVerdict);
        Assert.Equal(1, m.RetryCount);
        Assert.Equal(1, m.ReviewRetryCount);
        Assert.Equal(0, m.TestRetryCount);
        Assert.Equal(2000, m.PromptTokens);
        Assert.Equal(800, m.CompletionTokens);
        Assert.True(m.ImproverSkipped);
        Assert.Equal("timeout", m.ImproverSkipReason);
        Assert.Equal(TimeSpan.FromMinutes(5), m.Duration);
    }

    /// <summary>
    /// Collections on <see cref="IterationMetrics"/> can be populated and queried.
    /// </summary>
    [Fact]
    public void Collections_CanBePopulatedAndQueried()
    {
        var m = new IterationMetrics();
        m.Issues.Add("test failure A");
        m.Issues.Add("test failure B");
        m.ReviewIssues.Add("style issue");
        m.PhaseDurations["Coding"] = TimeSpan.FromSeconds(90);
        m.PhaseDurations["Testing"] = TimeSpan.FromSeconds(30);
        m.AgentsMdVersions["coder"] = "v1.2";
        m.Custom["extra"] = 42;

        Assert.Equal(2, m.Issues.Count);
        Assert.Contains("test failure A", m.Issues);
        Assert.Single(m.ReviewIssues);
        Assert.Equal(TimeSpan.FromSeconds(90), m.PhaseDurations["Coding"]);
        Assert.Equal(TimeSpan.FromSeconds(30), m.PhaseDurations["Testing"]);
        Assert.Equal("v1.2", m.AgentsMdVersions["coder"]);
        Assert.Equal(42, m.Custom["extra"]);
    }

    // ── PassRate aggregation consistency ────────────────────────────────────

    /// <summary>
    /// When <see cref="IterationMetrics.PassedTests"/> equals
    /// <see cref="IterationMetrics.TotalTests"/>, PassRate must be exactly 1.0.
    /// </summary>
    [Fact]
    public void PassRate_AllTestsPass_IsOne()
    {
        var m = new IterationMetrics { TotalTests = 100, PassedTests = 100 };
        Assert.Equal(1.0, m.PassRate);
    }

    /// <summary>
    /// PassRate and IntegrationPassRate are independent — setting integration counts
    /// does not affect the unit-test pass rate.
    /// </summary>
    [Fact]
    public void PassRate_IndependentOfIntegrationTests()
    {
        var m = new IterationMetrics
        {
            TotalTests = 10,
            PassedTests = 7,
            IntegrationTestsTotal = 5,
            IntegrationTestsPassed = 3,
        };

        Assert.Equal(0.7, m.PassRate, precision: 10);
        Assert.Equal(0.6, m.IntegrationPassRate, precision: 10);
    }
}
