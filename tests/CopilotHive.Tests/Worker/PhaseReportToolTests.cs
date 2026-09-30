using System.Reflection;
using System.Runtime.CompilerServices;

using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE USER-FACING HALF of the phase-report handoff: the REAL <c>get_phase_report</c>
/// <see cref="AIFunction"/> a worker's model can call, built by the real
/// <see cref="SharpCoderRunner"/>'s private <c>BuildCustomTools</c>, reading the reports the real
/// <see cref="SharpCoderRunner.SetPhaseReports"/> stored.
/// <para>
/// Every vector invokes the ACTUAL tool through <see cref="AIFunction.InvokeAsync"/> — never a
/// hand-rolled replica of its logic — so a changed signature, a dropped registration or a changed
/// message fails here. The tool is built fresh per invocation, exactly as one turn would, and no
/// live client, tool bridge or LLM is involved.
/// </para>
/// <para>
/// Deterministic only: no sleeps, no delays, no polling.
/// </para>
/// </summary>
public sealed class PhaseReportToolTests
{
    private const string ToolName = "get_phase_report";

    // ── Returning a report ───────────────────────────────────────────────────

    /// <summary>With no occurrence given, the LATEST occurrence of the phase is returned.</summary>
    [Fact]
    public async Task GetPhaseReport_OccurrenceOmitted_ReturnsTheLatestOccurrence()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(
        [
            new PhaseReport("Coding", 1, "FIRST-CODING-REPORT"),
            new PhaseReport("Coding", 2, "SECOND-CODING-REPORT"),
        ]);

        var result = await InvokeAsync(runner, phase: "Coding");

        Assert.Equal("SECOND-CODING-REPORT", result);
    }

    /// <summary>
    /// LATEST == GREATEST OCCURRENCE, NOT THE LAST ENUMERATED MATCH. A stored set whose occurrence 2
    /// comes BEFORE occurrence 1 (the reverse of a timeline) must still answer with occurrence 2:
    /// list position is not a timeline guarantee, because <c>PhaseReportMetadata.Read</c> preserves
    /// the dictionary's enumeration and protobuf's <c>MapField</c> documents its iteration order as
    /// undefined.
    /// </summary>
    /// <remarks>
    /// THIS IS THE MUTANT KILL for a <c>LastOrDefault</c>-based selector: that version answers with
    /// occurrence 1 here. The stored order is proven below, so the assertion cannot pass by accident
    /// of the list already being sorted.
    /// </remarks>
    [Fact]
    public async Task GetPhaseReport_OccurrenceOmitted_ReturnsTheGreatestOccurrenceRegardlessOfStoredOrder()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(
        [
            new PhaseReport("Coding", 2, "SECOND-CODING-REPORT"),
            new PhaseReport("Coding", 1, "FIRST-CODING-REPORT"),
        ]);
        // PREMISE: the runner really holds them in the reversed order under test.
        Assert.Equal(2, PeekPhaseReports(runner)[0].Occurrence);
        Assert.Equal(1, PeekPhaseReports(runner)[1].Occurrence);

        Assert.Equal("SECOND-CODING-REPORT", await InvokeAsync(runner, phase: "Coding"));
        // The explicit path is unaffected by the stored order either.
        Assert.Equal("FIRST-CODING-REPORT", await InvokeAsync(runner, phase: "Coding", occurrence: 1));
        Assert.Equal("SECOND-CODING-REPORT", await InvokeAsync(runner, phase: "Coding", occurrence: 2));
    }

    /// <summary>
    /// The same reversal driven through the REAL transport: metadata written with the keys in
    /// REVERSE insertion order, read back through <see cref="PhaseReportMetadata"/>, and answered by
    /// the real tool. This is the shape production actually sees, where the enumeration order is the
    /// transport's, not the writer's.
    /// </summary>
    /// <remarks>
    /// The read-back order is asserted as the premise, so the vector genuinely exercises a reversed
    /// stored sequence rather than a conveniently sorted one.
    /// </remarks>
    [Fact]
    public async Task GetPhaseReport_OccurrenceOmitted_ReturnsTheGreatestOccurrenceFromReversedMetadataKeys()
    {
        // Keys inserted occurrence 2 first, occurrence 1 second.
        var metadata = new Dictionary<string, string>();
        PhaseReportMetadata.Write(metadata,
        [
            new PhaseReport("DocWriting", 2, "SECOND-DOC-REPORT"),
            new PhaseReport("DocWriting", 1, "FIRST-DOC-REPORT"),
        ]);
        var read = PhaseReportMetadata.Read(metadata);
        // PREMISE: reading preserves the (reversed) enumeration order.
        Assert.Equal([2, 1], read.Select(r => r.Occurrence));

        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(read);

        Assert.Equal("SECOND-DOC-REPORT", await InvokeAsync(runner, phase: "DocWriting"));
    }

    /// <summary>With an explicit occurrence, exactly that occurrence is returned.</summary>
    [Fact]
    public async Task GetPhaseReport_ExplicitOccurrence_ReturnsExactlyThatOccurrence()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(
        [
            new PhaseReport("Coding", 1, "FIRST-CODING-REPORT"),
            new PhaseReport("Coding", 3, "SECOND-CODING-REPORT"),
        ]);

        Assert.Equal("FIRST-CODING-REPORT", await InvokeAsync(runner, phase: "Coding", occurrence: 1));
        Assert.Equal("SECOND-CODING-REPORT", await InvokeAsync(runner, phase: "Coding", occurrence: 3));
    }

    /// <summary>
    /// The phase name is matched CASE-INSENSITIVELY, and the report still comes back verbatim —
    /// the caller's own capitalization never leaks into the answer.
    /// </summary>
    [Theory]
    [InlineData("Coding")]
    [InlineData("coding")]
    [InlineData("CODING")]
    [InlineData("cOdInG")]
    [InlineData("DocWriting")]
    [InlineData("docwriting")]
    [InlineData("docWRITING")]
    public async Task GetPhaseReport_PhaseNameIsCaseInsensitive(string phase)
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(
        [
            new PhaseReport("Coding", 1, "CODER-REPORT-BODY"),
            new PhaseReport("DocWriting", 1, "DOC-WRITER-REPORT-BODY"),
        ]);

        var expected = phase.Equals("Coding", StringComparison.OrdinalIgnoreCase)
            ? "CODER-REPORT-BODY"
            : "DOC-WRITER-REPORT-BODY";

        Assert.Equal(expected, await InvokeAsync(runner, phase: phase));
    }

    /// <summary>
    /// The report is returned VERBATIM — no header, no trimming, no cap. The body starts and ends
    /// with whitespace and is longer than 4,000 characters, with unique markers at both ends.
    /// </summary>
    [Fact]
    public async Task GetPhaseReport_ReturnsTheStoredReportVerbatim()
    {
        var report = "  \nHEAD-MARKER\n" + new string('R', 4_100) + "\nTAIL-MARKER\n  ";
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports([new PhaseReport("Review", 1, report)]);

        var result = await InvokeAsync(runner, phase: "Review");

        Assert.Equal(report, result);
        Assert.StartsWith("  \nHEAD-MARKER\n", result, StringComparison.Ordinal);
        Assert.EndsWith("\nTAIL-MARKER\n  ", result, StringComparison.Ordinal);
        Assert.True(result.Length > 4_000);
    }

    /// <summary>
    /// Only the requested phase's report is returned: the presence of other phases' reports never
    /// changes the answer.
    /// </summary>
    [Fact]
    public async Task GetPhaseReport_OtherPhasesPresent_ReturnsOnlyTheRequestedPhase()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(
        [
            new PhaseReport("Coding", 1, "CODER-REPORT"),
            new PhaseReport("DocWriting", 1, "DOC-REPORT"),
            new PhaseReport("Testing", 1, "TESTER-REPORT"),
        ]);

        var result = await InvokeAsync(runner, phase: "DocWriting");

        Assert.Equal("DOC-REPORT", result);
        Assert.DoesNotContain("CODER-REPORT", result, StringComparison.Ordinal);
        Assert.DoesNotContain("TESTER-REPORT", result, StringComparison.Ordinal);
    }

    // ── No match: the two exact messages ─────────────────────────────────────

    /// <summary>
    /// An unknown phase with reports present names the available reports in the DETERMINISTIC
    /// listing order, plus the canonical phase name when the phase IS known but has no report.
    /// </summary>
    [Fact]
    public async Task GetPhaseReport_UnknownPhase_ListsAvailableReports()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(
        [
            new PhaseReport("Coding", 1, "CODER-REPORT"),
            new PhaseReport("DocWriting", 2, "DOC-REPORT"),
        ]);

        var result = await InvokeAsync(runner, phase: "Review");

        Assert.Equal(
            "No Review report is available for this iteration. Available: Coding (occurrence 1), DocWriting (occurrence 2).",
            result);
    }

    /// <summary>
    /// THE DETERMINISTIC LISTING. Stored reports arrive in metadata order, which the transport does
    /// not guarantee, so the listing orders a COPY by the canonical phase sequence (Coding, Testing,
    /// Review, DocWriting, Improve) and then by occurrence ASCENDING within each phase — never by the
    /// order the keys happened to be inserted in.
    /// </summary>
    /// <remarks>
    /// BOTH ORDERING DIMENSIONS ARE PINNED BY ONE FIXTURE: the inserted order is deliberately the
    /// reverse of both (DocWriting last-canonically first, occurrences 3 then 1), so a listing that
    /// merely sorted one dimension, or none, produces a different string. THIS IS THE MUTANT KILL for
    /// a stored-order listing.
    /// </remarks>
    [Fact]
    public async Task GetPhaseReport_UnknownPhase_ListsInCanonicalPhaseThenOccurrenceOrder()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        // Inserted OUT of the listing order on both dimensions.
        runner.SetPhaseReports(
        [
            new PhaseReport("DocWriting", 2, "DOC-2"),
            new PhaseReport("Testing", 3, "TEST-3"),
            new PhaseReport("DocWriting", 1, "DOC-1"),
            new PhaseReport("Coding", 2, "CODE-2"),
            new PhaseReport("Improve", 1, "IMPROVE-1"),
            new PhaseReport("Testing", 1, "TEST-1"),
            new PhaseReport("Coding", 1, "CODE-1"),
            new PhaseReport("Review", 1, "REVIEW-1"),
        ]);
        // PREMISE: the stored order really is the inserted order, so the expected string below
        // cannot match by accident.
        Assert.Equal(
            ["DocWriting", "Testing", "DocWriting", "Coding", "Improve", "Testing", "Coding", "Review"],
            PeekPhaseReports(runner).Select(r => r.Phase));
        // PREMISE: nothing is mutated by the listing — the stored set is unchanged afterwards.
        var storedBefore = PeekPhaseReports(runner);

        var result = await InvokeAsync(runner, phase: "not-a-phase");

        Assert.Equal(
            "No not-a-phase report is available for this iteration. Available: "
            + "Coding (occurrence 1), Coding (occurrence 2), Testing (occurrence 1), Testing (occurrence 3), "
            + "Review (occurrence 1), DocWriting (occurrence 1), DocWriting (occurrence 2), Improve (occurrence 1).",
            result);
        Assert.Equal(storedBefore, PeekPhaseReports(runner));
    }

    /// <summary>
    /// THE DETERMINISTIC LISTING, driven through the METADATA KEYS: reports written into a metadata
    /// dictionary with the keys inserted OUT of canonical order (Improve and DocWriting before
    /// Coding), then read back through <see cref="PhaseReportMetadata"/> and answered by the real
    /// tool. The same stored set must always produce the same listing, whatever order the keys were
    /// written in.
    /// </summary>
    /// <remarks>
    /// The read-back order is asserted as the premise, so the vector genuinely exercises a
    /// non-canonical stored sequence rather than a conveniently ordered one.
    /// </remarks>
    [Fact]
    public async Task GetPhaseReport_UnknownPhase_ListsCanonicallyForNonCanonicalMetadataKeyOrder()
    {
        // Keys inserted out of canonical order: Improve, DocWriting, Coding, Testing.
        var metadata = new Dictionary<string, string>();
        PhaseReportMetadata.Write(metadata,
        [
            new PhaseReport("Improve", 1, "IMPROVE-REPORT"),
            new PhaseReport("DocWriting", 1, "DOC-REPORT"),
            new PhaseReport("Coding", 1, "CODER-REPORT"),
            new PhaseReport("Testing", 1, "TESTER-REPORT"),
        ]);
        var read = PhaseReportMetadata.Read(metadata);
        // PREMISE: reading preserves the non-canonical (insertion) order.
        Assert.Equal(["Improve", "DocWriting", "Coding", "Testing"], read.Select(r => r.Phase));

        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(read);

        var result = await InvokeAsync(runner, phase: "Review");

        Assert.Equal(
            "No Review report is available for this iteration. Available: "
            + "Coding (occurrence 1), Testing (occurrence 1), DocWriting (occurrence 1), Improve (occurrence 1).",
            result);
    }

    /// <summary>
    /// The listing renders each name in canonical casing — a stored name's own spelling (lower-case,
    /// mixed-case) is never echoed back, whatever it was.
    /// </summary>
    [Fact]
    public async Task GetPhaseReport_UnknownPhase_ListsWithCanonicalCasing()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(
        [
            new PhaseReport("DocWriting", 1, "DOC-REPORT"),
            new PhaseReport("coding", 1, "CODER-REPORT"),
        ]);

        var result = await InvokeAsync(runner, phase: "MERGING-was-not-a-phase");

        Assert.Equal(
            "No MERGING-was-not-a-phase report is available for this iteration. Available: Coding (occurrence 1), DocWriting (occurrence 1).",
            result);
    }

    /// <summary>
    /// A KNOWN phase name with no matching occurrence still uses the canonical name in the message,
    /// so a casing variant of a phase with no report reads correctly.
    /// </summary>
    [Fact]
    public async Task GetPhaseReport_KnownPhaseWithoutThatOccurrence_UsesTheCanonicalName()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports([new PhaseReport("Coding", 1, "CODER-REPORT")]);

        var result = await InvokeAsync(runner, phase: "dOcWrItInG");

        Assert.Equal(
            "No DocWriting report is available for this iteration. Available: Coding (occurrence 1).",
            result);
    }

    /// <summary>A requested occurrence that does not exist is reported like any other miss.</summary>
    [Fact]
    public async Task GetPhaseReport_OccurrenceNotPresent_ReportsAvailable()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports([new PhaseReport("Coding", 1, "FIRST-CODING-REPORT")]);

        var result = await InvokeAsync(runner, phase: "Coding", occurrence: 7);

        Assert.Equal(
            "No Coding report is available for this iteration. Available: Coding (occurrence 1).",
            result);
    }

    /// <summary>
    /// With no reports at all, the tool says exactly that — never an empty "Available:" list.
    /// </summary>
    [Theory]
    [InlineData("Coding")]
    [InlineData("NothingLikeAPhase")]
    public async Task GetPhaseReport_NoReportsAtAll_ReturnsTheNoReportsMessage(string phase)
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        Assert.Empty(PeekPhaseReports(runner));

        var result = await InvokeAsync(runner, phase: phase);

        Assert.Equal(SharpCoderRunner.NoPhaseReportsAvailableMessage, result);
        Assert.Equal("No completed phase reports are available for this iteration yet.", result);
    }

    // ── Lifecycle: no stale reports ──────────────────────────────────────────

    /// <summary>
    /// A RE-USED runner: after an assignment that carried reports, a second assignment with NONE
    /// sees the no-reports message — the previous task's reports are gone, not merely superseded.
    /// </summary>
    /// <remarks>
    /// KILLS TWO MUTANTS: (a) an implementation that ignores the empty replacement (the first
    /// task's report would still be returned); (b) one that remembers the previous non-empty list
    /// "for convenience" (same observable failure).
    /// </remarks>
    [Fact]
    public async Task GetPhaseReport_SecondAssignmentWithoutReports_SeesNoReports()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;

        // Assignment 1 — reports present, the tool really serves them.
        runner.SetPhaseReports([new PhaseReport("Coding", 1, "FIRST-TASK-REPORT")]);
        Assert.Equal("FIRST-TASK-REPORT", await InvokeAsync(runner, phase: "Coding"));

        // Assignment 2 — no reports for this task.
        runner.SetPhaseReports([]);

        Assert.Equal(
            SharpCoderRunner.NoPhaseReportsAvailableMessage,
            await InvokeAsync(runner, phase: "Coding"));
        Assert.Empty(PeekPhaseReports(runner));
    }

    /// <summary>
    /// Starting a NEW connection drops the reports: after <c>ConnectAsync</c> the tool reports the
    /// no-reports message, exactly like the stale tester report it is cleared beside.
    /// </summary>
    [Fact]
    public async Task GetPhaseReport_AfterConnectAsync_ReportsNothingAvailable()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports([new PhaseReport("Coding", 1, "PRE-CONNECT-REPORT")]);
        Assert.Equal("PRE-CONNECT-REPORT", await InvokeAsync(runner, phase: "Coding"));

        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Empty(PeekPhaseReports(runner));
        Assert.Equal(
            SharpCoderRunner.NoPhaseReportsAvailableMessage,
            await InvokeAsync(runner, phase: "Coding"));
    }

    // ── The tool's own contract ──────────────────────────────────────────────

    /// <summary>
    /// The registered tool advertises exactly the two documented parameters, so a model can call it
    /// with and without an occurrence.
    /// </summary>
    [Fact]
    public void GetPhaseReport_ToolAdvertisesPhaseAndOccurrenceParameters()
    {
        var runner = new SharpCoderRunner();
        var tool = Assert.Single(BuildTools(runner), t => t.Name == ToolName);

        var descriptor = tool.GetType().GetProperty("FunctionDescriptor")?.GetValue(tool);
        Assert.NotNull(descriptor);
        var expectedNames = descriptor!.GetType().GetProperty("ExpectedArgumentNames")?.GetValue(descriptor)
            as HashSet<string>;
        Assert.NotNull(expectedNames);

        Assert.Contains("phase", expectedNames);
        Assert.Contains("occurrence", expectedNames);
    }

    /// <summary>
    /// The tool is READ-ONLY: invoking it never mutates the stored reports — repeated calls with
    /// different arguments all answer from the same unchanged set.
    /// </summary>
    [Fact]
    public async Task GetPhaseReport_Invocations_DoNotMutateTheStoredReports()
    {
        PhaseReport[] stored =
        [
            new PhaseReport("Coding", 1, "CODER-REPORT"),
            new PhaseReport("Testing", 2, "TESTER-REPORT"),
        ];
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports(stored);

        await InvokeAsync(runner, phase: "Coding");
        await InvokeAsync(runner, phase: "DocWriting");
        await InvokeAsync(runner, phase: "Improve", occurrence: 3);

        Assert.Equal(stored, PeekPhaseReports(runner));
    }

    /// <summary>
    /// The tool LOGS its invocation like the other tools — the existing diagnostic convention, with
    /// the occurrence shown only when the model supplied one.
    /// </summary>
    [Fact]
    public async Task GetPhaseReport_LogsTheToolCall()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;
        runner.SetPhaseReports([new PhaseReport("Coding", 1, "CODER-REPORT")]);

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);

            await InvokeAsync(runner, phase: "Coding");
            await InvokeAsync(runner, phase: "Coding", occurrence: 2);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = captured.ToString();
        Assert.Contains("Tool call: get_phase_report(Coding)", output);
        Assert.Contains("Tool call: get_phase_report(Coding, occurrence=2)", output);
    }

    /// <summary>
    /// The tool keeps its own contract: its description names the CURRENT iteration and points the
    /// model at an earlier phase's report, so a model that reads the tool list knows when to use it.
    /// </summary>
    [Fact]
    public void GetPhaseReport_ToolDescriptionDocumentsItsPurpose()
    {
        var runner = new SharpCoderRunner();
        var tool = Assert.Single(BuildTools(runner), t => t.Name == ToolName);

        Assert.Equal(
            "Retrieve the full report of a phase that already completed in the CURRENT iteration "
            + "(e.g. the coder's or doc-writer's report) — use it to verify evidence the goal asks for "
            + "in an earlier phase's report.",
            tool.Description);
    }

    /// <summary>
    /// CONSTRUCTION-TIME BINDING: a tool set is built once per turn, and the reports it answers from
    /// are the ones stored WHEN IT WAS BUILT. A runner is repointed at the next assignment between
    /// turns, so a tool that read the live field instead would silently answer with the NEXT
    /// assignment's reports — the retention hazard the other tools close the same way.
    /// </summary>
    /// <remarks>
    /// KILLS THE MUTANT that resolves the reports from the live field rather than the turn's captured
    /// value: that version answers with the SECOND assignment's report.
    /// </remarks>
    [Fact]
    public async Task GetPhaseReport_ToolBuiltForOneAssignment_KeepsAnsweringFromItsOwnReports()
    {
        var runner = new SharpCoderRunner();
        await using var _ = runner;

        // Turn A's reports, and the tool set built for turn A.
        runner.SetPhaseReports([new PhaseReport("Coding", 1, "ASSIGNMENT-A-REPORT")]);
        var toolBuiltForA = Assert.Single(BuildTools(runner), t => t.Name == ToolName);
        var functionBuiltForA = Assert.IsAssignableFrom<AIFunction>(toolBuiltForA);

        // The NEXT assignment repoints the runner — turn B's reports.
        runner.SetPhaseReports([new PhaseReport("Coding", 1, "ASSIGNMENT-B-REPORT")]);

        var result = await functionBuiltForA.InvokeAsync(
            new AIFunctionArguments { ["phase"] = "Coding" }, TestContext.Current.CancellationToken);

        Assert.Equal("ASSIGNMENT-A-REPORT", result?.ToString());
        // The runner's live reports really are turn B's, so the assertion above is discriminating.
        Assert.Equal([new PhaseReport("Coding", 1, "ASSIGNMENT-B-REPORT")], PeekPhaseReports(runner));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes the REAL <c>get_phase_report</c> tool exactly as the model would, through the real
    /// runner's own <c>BuildCustomTools</c>. An <see cref="InvalidOperationException"/> names the
    /// tool when it is missing, so a dropped registration fails loudly instead of silently.
    /// </summary>
    private static async Task<string> InvokeAsync(SharpCoderRunner runner, string phase, int? occurrence = null)
    {
        var tool = Assert.Single(BuildTools(runner), t => t.Name == ToolName);
        var function = Assert.IsAssignableFrom<AIFunction>(tool);

        var arguments = new AIFunctionArguments { ["phase"] = phase };
        if (occurrence is { } value)
            arguments["occurrence"] = value;

        var result = await function.InvokeAsync(arguments, TestContext.Current.CancellationToken);
        return result?.ToString() ?? string.Empty;
    }

    /// <summary>The tools the real runner builds for the CURRENT role, via its private builder.</summary>
    private static IList<AITool> BuildTools(SharpCoderRunner runner)
    {
        var method = typeof(SharpCoderRunner).GetMethod(
            "BuildCustomTools", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "SharpCoderRunner.BuildCustomTools not found — the tool-building method was renamed or removed.");
        return (IList<AITool>)method.Invoke(runner, [TestContext.Current.CancellationToken])!;
    }

    /// <summary>
    /// Reads the real runner's private phase-report field directly. A pure observation: it cannot
    /// clear or otherwise change the value it is inspecting.
    /// </summary>
    private static List<PhaseReport> PeekPhaseReports(SharpCoderRunner runner)
    {
        var field = typeof(SharpCoderRunner)
            .GetField("_phaseReports", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "SharpCoderRunner._phaseReports not found — the phase-report field was renamed or removed.");
        return [.. (IReadOnlyList<PhaseReport>)field.GetValue(runner)!];
    }
}

// ── THE FULL-CHAIN HARNESS: a real runner whose real turn calls the tool ──────

/// <summary>
/// Wraps a REAL <see cref="SharpCoderRunner"/> whose REAL prompt turn is driven by
/// <see cref="PhaseReportToolCallChatClient"/>: the turn CALLS <c>get_phase_report</c> through the
/// agent loop, and the tool result the model received is recorded in
/// <see cref="ObservedGetPhaseReportResults"/>.
/// <para>
/// This is the observation point the end-to-end chain asserts on: the list is appended only from
/// INSIDE a prompt turn, so an executor that failed before prompting, a runner that never built the
/// tool set, or a tool that returned something else all leave it empty or wrong.
/// </para>
/// </summary>
internal sealed class PhaseReportChainRunner : IAgentRunner
{
    private readonly SharpCoderRunner _inner;
    private readonly PhaseReportToolCallChatClient _client = new();

    /// <summary>
    /// A REAL runner whose lazy-client seam is pointed at the tool-calling stub. The config-repo
    /// path is the caller's (the shared helper supplies a real directory), so a role that needs it
    /// finds one.
    /// </summary>
    public PhaseReportChainRunner(string configRepoDir = "/config-repo")
    {
        _inner = new SharpCoderRunner(configRepoDir);
        _inner.ClientCreationSeam = _ => _client;
    }

    /// <summary>
    /// Every <c>get_phase_report</c> result observed DURING a real prompt turn, in order. An empty
    /// list proves no turn reached the tool.
    /// </summary>
    public List<string> ObservedGetPhaseReportResults => _client.ObservedToolResults;

    /// <summary>The phase name the stub model asks for on its tool-calling turn.</summary>
    public string RequestedPhase
    {
        get => _client.RequestedPhase;
        set => _client.RequestedPhase = value;
    }

    /// <summary>The occurrence the stub model asks for, or <c>null</c> to omit the argument.</summary>
    public int? RequestedOccurrence
    {
        get => _client.RequestedOccurrence;
        set => _client.RequestedOccurrence = value;
    }

    /// <summary>Arms exactly one <c>get_phase_report</c> call for the coming prompt turn.</summary>
    public void ArmToolCall() => _client.ArmToolCall();

    public void SetPhaseReports(IReadOnlyList<PhaseReport>? reports) => _inner.SetPhaseReports(reports);
    public void SetRole(WorkerRole role) => _inner.SetRole(role);
    public void SetTesterReport(string? report) => _inner.SetTesterReport(report);

    public TestResultReport? LastTestReport => _inner.LastTestReport;
    public WorkerReport? LastWorkerReport => _inner.LastWorkerReport;

    public void ClearTestReport() => _inner.ClearTestReport();
    public void ClearWorkerReport() => _inner.ClearWorkerReport();
    public void SetToolBridge(IToolCallBridge? bridge) => _inner.SetToolBridge(bridge);
    public void SetCurrentTaskId(string? taskId) => _inner.SetCurrentTaskId(taskId);
    public void SetCurrentGoalId(string? goalId) => _inner.SetCurrentGoalId(goalId);
    public void SetCustomAgent(WorkerRole role, string agentsMdContent) =>
        _inner.SetCustomAgent(role, agentsMdContent);
    public void SetSession(object? session) => _inner.SetSession(session);
    public object? GetSession() => _inner.GetSession();
    public int GetContextUsagePercent() => _inner.GetContextUsagePercent();
    public void SetMaxContextTokens(int maxTokens) => _inner.SetMaxContextTokens(maxTokens);
    public void SetCompactionModel(string? model) => _inner.SetCompactionModel(model);
    public void SetCompactionMaxTokens(int? maxTokens) => _inner.SetCompactionMaxTokens(maxTokens);
    public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) => _inner.SetSubAgentModels(models);
    public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) =>
        _inner.SetConfigProvisioner(provisioner);
    public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
    public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default) =>
        _inner.ResetSessionAsync(model, reasoningEffort, ct);

    /// <summary>
    /// The REAL prompt turn: <see cref="SharpCoderRunner.SendPromptAsync"/> runs the real agent loop
    /// (through the tool-calling stub client), so <c>get_phase_report</c> is invoked by the ACTUAL
    /// machinery rather than by the test.
    /// </summary>
    public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct) =>
        _inner.SendPromptAsync(prompt, workDir, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// A deterministic stub <c>IChatClient</c>: each ARMED assignment makes exactly one
/// <c>get_phase_report(RequestedPhase, RequestedOccurrence)</c> call on its first turn and completes
/// with plain text afterwards. Whenever the conversation carries the result of the call it issued,
/// the result is recorded — i.e. the recorded value is exactly what the model saw.
/// <para>
/// ARMING IS EXPLICIT and per assignment on purpose. An executor run can issue several prompts
/// (a tester's metrics follow-up, an auto-commit follow-up) that replay the session, so "the first
/// turn of the process" would be the wrong trigger: the model asks ONCE per assignment, which is
/// what <see cref="ArmToolCall"/> models.
/// </para>
/// </summary>
internal sealed class PhaseReportToolCallChatClient : IChatClient
{
    /// <summary>The call-id prefix the stub uses, so its results can be recognized among other tool calls.</summary>
    internal const string ToolCallIdPrefix = "phase-report-call-";

    private int _toolCallsIssued;

    /// <summary>The next turn must ask for a phase report.</summary>
    private bool _toolCallArmed;

    /// <summary>The call id this client is currently awaiting a result for, if any.</summary>
    private string? _awaitingResultFor;

    /// <summary>
    /// Every <c>get_phase_report</c> result the model received, in order — one entry per ARMED
    /// assignment whose tool call was answered. An empty list proves no turn reached the tool.
    /// </summary>
    public List<string> ObservedToolResults { get; } = [];

    /// <summary>The phase name the stub asks for.</summary>
    public string RequestedPhase { get; set; } = "Coding";

    /// <summary>The occurrence the stub asks for, or <c>null</c> to omit the argument.</summary>
    public int? RequestedOccurrence { get; set; }

    /// <summary>Arms exactly one <c>get_phase_report</c> call for the coming assignment.</summary>
    public void ArmToolCall() => _toolCallArmed = true;

    public ChatClientMetadata Metadata => new("phase-report-stub", null, "phase-report-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
        {
            FinishReason = ChatFinishReason.Stop,
        });

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Record the result of THIS client's own pending tool call, if the conversation now carries
        // it. A replayed earlier turn's result is not re-recorded: the awaiting id is cleared above.
        if (_awaitingResultFor is { } awaitedId)
        {
            foreach (var message in messages.ToList())
            {
                var result = message.Contents
                    .OfType<FunctionResultContent>()
                    .FirstOrDefault(r => r.CallId == awaitedId);
                if (result is not null)
                {
                    ObservedToolResults.Add(result.Result?.ToString() ?? string.Empty);
                    _awaitingResultFor = null;
                    break;
                }
            }
        }

        await Task.Yield();

        if (_toolCallArmed)
        {
            _toolCallArmed = false;

            var arguments = new Dictionary<string, object?> { ["phase"] = RequestedPhase };
            if (RequestedOccurrence is { } occurrence)
                arguments["occurrence"] = occurrence;

            var toolCallId = ToolCallIdPrefix + Interlocked.Increment(ref _toolCallsIssued);
            _awaitingResultFor = toolCallId;

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(toolCallId, "get_phase_report", arguments)])
            {
                FinishReason = ChatFinishReason.ToolCalls,
            };
            yield break;
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
        yield return new ChatResponseUpdate
        {
            FinishReason = ChatFinishReason.Stop,
            Role = ChatRole.Assistant,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
