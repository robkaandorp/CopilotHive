extern alias WorkerAssembly;

using CopilotHive.Workers;
using WorkerAssembly::CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for the <c>BuildRoleSystemPrompt</c> method on <see cref="SharpCoderRunner"/>.
/// Verifies that each role gets the correct hardcoded prompt, the infrastructure rules preamble
/// is always present, and AGENTS.md content is appended correctly under the heuristics separator.
/// </summary>
public sealed class BuildRoleSystemPromptTests
{
    // ── Shared infrastructure rules ───────────────────────────────────────────

    /// <summary>
    /// Every role's prompt must contain the infrastructure rules preamble that forbids
    /// <c>git push</c>, <c>git checkout</c>, <c>git branch</c>, and <c>git switch</c>.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.DocWriter)]
    [InlineData(WorkerRole.Improver)]
    public void BuildRoleSystemPrompt_AllRoles_ContainInfrastructureRules(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("INFRASTRUCTURE RULES", prompt);
        Assert.Contains("NEVER run `git push`", prompt);
        Assert.Contains("NEVER run `git checkout`", prompt);
        Assert.Contains("request_clarification", prompt);
    }

    // ── Role identity ─────────────────────────────────────────────────────────

    /// <summary>
    /// The Coder prompt must contain the role identity and <c>report_code_changes</c> tool reference.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Coder_ContainsRoleIdentityAndToolContract()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, null);

        Assert.Contains("# Coder", prompt);
        Assert.Contains("report_code_changes", prompt);
    }

    /// <summary>
    /// The Tester prompt must contain the role identity and <c>report_test_results</c> tool reference.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Tester_ContainsRoleIdentityAndToolContract()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Tester, null);

        Assert.Contains("# Tester", prompt);
        Assert.Contains("report_test_results", prompt);
    }

    /// <summary>
    /// The Reviewer prompt must contain the role identity, <c>report_review_verdict</c> tool
    /// reference, and the CRITICAL/MAJOR/MINOR severity legend.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Reviewer_ContainsRoleIdentityAndToolContract()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Reviewer, null);

        Assert.Contains("# Reviewer", prompt);
        Assert.Contains("report_review_verdict", prompt);
        Assert.Contains("CRITICAL", prompt);
        Assert.Contains("MAJOR", prompt);
        Assert.Contains("MINOR", prompt);
    }

    /// <summary>
    /// The DocWriter prompt must contain the role identity and <c>report_doc_changes</c> tool reference.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_DocWriter_ContainsRoleIdentityAndToolContract()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.DocWriter, null);

        Assert.Contains("# Doc Writer", prompt);
        Assert.Contains("report_doc_changes", prompt);
    }

    /// <summary>
    /// The Improver prompt must contain the role identity, the 4000-character limit,
    /// and the constraint against removing safety rules.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Improver_ContainsRoleIdentityAndConstraints()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Improver, null);

        Assert.Contains("# Improver", prompt);
        Assert.Contains("4000", prompt);
        Assert.Contains("safety constraints", prompt);
    }

    // ── Learned heuristics appendix ───────────────────────────────────────────

    /// <summary>
    /// When <c>agentsMdContent</c> is non-empty, the returned prompt must contain
    /// the <c># Learned Heuristics</c> separator followed by the supplied content.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_WithAgentsMd_AppendsHeuristicsSeparatorAndContent()
    {
        const string AgentsMd = "Always write unit tests first.";

        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, AgentsMd);

        Assert.Contains("\n\n# Learned Heuristics\n\n", prompt);
        Assert.Contains(AgentsMd, prompt);
    }

    /// <summary>
    /// The heuristics section must appear AFTER the hardcoded prompt content,
    /// not before it.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_WithAgentsMd_HeuristicsAppearsAfterHardcodedContent()
    {
        const string AgentsMd = "Some learned rule.";

        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Tester, AgentsMd);

        var heuristicsIndex = prompt.IndexOf("# Learned Heuristics", StringComparison.Ordinal);
        var infraIndex = prompt.IndexOf("INFRASTRUCTURE RULES", StringComparison.Ordinal);

        Assert.True(infraIndex >= 0, "INFRASTRUCTURE RULES must be present");
        Assert.True(heuristicsIndex > infraIndex,
            "# Learned Heuristics must appear after the hardcoded infrastructure rules");
    }

    // ── Null / empty AGENTS.md handling ──────────────────────────────────────

    /// <summary>
    /// When <c>agentsMdContent</c> is <c>null</c>, no heuristics separator must
    /// appear and the prompt must still be non-empty and valid.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_NullAgentsMd_NoHeuristicsSeparator()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, null);

        Assert.DoesNotContain("# Learned Heuristics", prompt);
        Assert.False(string.IsNullOrWhiteSpace(prompt));
    }

    /// <summary>
    /// When <c>agentsMdContent</c> is an empty string, no heuristics separator
    /// must appear and the prompt must still be non-empty and valid.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_EmptyAgentsMd_NoHeuristicsSeparator()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Reviewer, string.Empty);

        Assert.DoesNotContain("# Learned Heuristics", prompt);
        Assert.False(string.IsNullOrWhiteSpace(prompt));
    }

    /// <summary>
    /// When <c>agentsMdContent</c> is whitespace-only, it is treated as empty
    /// and no heuristics separator must appear.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_WhitespaceAgentsMd_NoHeuristicsSeparator()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.DocWriter, "   \n\t  ");

        Assert.DoesNotContain("# Learned Heuristics", prompt);
    }

    // ── Unknown role guard ────────────────────────────────────────────────────

    /// <summary>
    /// An unhandled <see cref="WorkerRole"/> value must throw <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_UnknownRole_ThrowsInvalidOperationException()
    {
        var unknownRole = (WorkerRole)999;

        Assert.Throws<InvalidOperationException>(
            () => SharpCoderRunner.BuildRoleSystemPrompt(unknownRole, null));
    }
}
