using CopilotHive.Shared;
using CopilotHive.Worker;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for <see cref="AgentsMdSize"/> — the shared source of truth for the
/// <c>*.agents.md</c> character limit and for the size line reported after an edit.
/// </summary>
public sealed class AgentsMdSizeTests
{
    /// <summary>
    /// The limit is 8,000 characters, and the worker's existing constant must alias it so
    /// executor enforcement and the reporting tools can never drift apart.
    /// </summary>
    [Fact]
    public void MaxCharacters_Is8000_AndWorkerConstantAliasesTheSharedValue()
    {
        Assert.Equal(8000, AgentsMdSize.MaxCharacters);
        Assert.Equal(AgentsMdSize.MaxCharacters, WorkerConstants.AgentsMdMaxCharacters);
    }

    /// <summary>
    /// One character under the limit reports the count and the limit, with no over-limit wording.
    /// </summary>
    [Fact]
    public void Describe_OneUnderLimit_ReportsWithinLimit()
    {
        var text = AgentsMdSize.Describe(7999);

        Assert.Equal("File is now 7999 characters (limit 8000).", text);
        Assert.DoesNotContain("over the", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exactly at the limit is still allowed — the boundary belongs to the within-limit form.
    /// </summary>
    [Fact]
    public void Describe_ExactlyAtLimit_ReportsWithinLimit()
    {
        var text = AgentsMdSize.Describe(8000);

        Assert.Equal("File is now 8000 characters (limit 8000).", text);
        Assert.DoesNotContain("over the", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// One character over the limit reports exactly how far over the file is.
    /// </summary>
    [Fact]
    public void Describe_OneOverLimit_ReportsOneOver()
    {
        var text = AgentsMdSize.Describe(8001);

        Assert.Equal(
            "File is now 8001 characters — 1 over the 8000-character limit. Bring it back within the limit.",
            text);
    }

    /// <summary>
    /// Every number is invariant-formatted WITHOUT thousands separators, so a large file reports
    /// <c>8001</c> and not <c>8,001</c> regardless of the host's culture.
    /// </summary>
    [Fact]
    public void Describe_OverLimit_UsesNoThousandsSeparators()
    {
        var text = AgentsMdSize.Describe(12345);

        Assert.Equal(
            "File is now 12345 characters — 4345 over the 8000-character limit. Bring it back within the limit.",
            text);
        Assert.DoesNotContain(",", text, StringComparison.Ordinal);
    }
}
