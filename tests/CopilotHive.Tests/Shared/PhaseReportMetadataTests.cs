using CopilotHive.Services;

// NOTE: this file lives under tests/CopilotHive.Tests/Shared/ (it covers a CopilotHive.Shared
// type) but deliberately declares the ROOT test namespace, following the LogSanitizerTests and
// GitCredentialResolverTests convention — declaring `CopilotHive.Tests.Shared` would break the
// existing `Shared.Grpc.*` relative-name resolutions in sibling test files.
namespace CopilotHive.Tests;

/// <summary>
/// The shared metadata contract of <see cref="PhaseReportMetadata"/> — the ONE place the
/// <c>phase_report:{Phase}:{Occurrence}</c> key layout is defined. Both the orchestrator and the
/// worker go through it, so these vectors pin the exact key format and the reader's refusal to
/// guess at anything that does not match it.
/// </summary>
public sealed class PhaseReportMetadataTests
{
    // ── Write: the exact key format ──────────────────────────────────────────

    /// <summary>
    /// The key is EXACTLY <c>phase_report:{Phase}:{Occurrence}</c> — pinned literally, because a
    /// silently changed format would break the worker side without breaking a round-trip.
    /// </summary>
    [Fact]
    public void Write_UsesTheExactKeyFormat()
    {
        var metadata = new Dictionary<string, string>();

        PhaseReportMetadata.Write(metadata, [new PhaseReport("Coding", 2, "coder report")]);

        var entry = Assert.Single(metadata);
        Assert.Equal("phase_report:Coding:2", entry.Key);
        Assert.Equal("coder report", entry.Value);
    }

    /// <summary>Two occurrences of ONE phase produce two distinct keys.</summary>
    [Fact]
    public void Write_TwoOccurrencesOfOnePhase_ProducesTwoKeys()
    {
        var metadata = new Dictionary<string, string>();
        var reports = new[]
        {
            new PhaseReport("Coding", 1, "first coding"),
            new PhaseReport("Coding", 2, "second coding"),
        };

        PhaseReportMetadata.Write(metadata, reports);

        Assert.Equal(2, metadata.Count);
        Assert.Equal("first coding", metadata["phase_report:Coding:1"]);
        Assert.Equal("second coding", metadata["phase_report:Coding:2"]);
    }

    /// <summary>
    /// The value is the report VERBATIM: multi-line, with surrounding whitespace and beyond 4,000
    /// characters, none of it clipped or trimmed.
    /// </summary>
    [Fact]
    public void Write_StoresTheReportVerbatim()
    {
        var report = "  \n" + new string('F', 4_100) + "\n  trailing line with spaces   \nEND-MARKER\n  ";
        var metadata = new Dictionary<string, string>();

        PhaseReportMetadata.Write(metadata, [new PhaseReport("DocWriting", 1, report)]);

        Assert.Equal(report, metadata["phase_report:DocWriting:1"]);
        Assert.StartsWith("  \n", metadata["phase_report:DocWriting:1"], StringComparison.Ordinal);
        Assert.EndsWith("END-MARKER\n  ", metadata["phase_report:DocWriting:1"], StringComparison.Ordinal);
        Assert.True(report.Length > 4_000);
    }

    /// <summary>An empty sequence writes nothing at all.</summary>
    [Fact]
    public void Write_EmptySequence_WritesNothing()
    {
        var metadata = new Dictionary<string, string> { ["unrelated"] = "kept" };

        PhaseReportMetadata.Write(metadata, []);

        Assert.Equal("kept", Assert.Single(metadata).Value);
    }

    // ── Write → Read round-trip ──────────────────────────────────────────────

    [Fact]
    public void WriteThenRead_RoundTripsEveryReportExactly()
    {
        var metadata = new Dictionary<string, string>();
        var reports = new[]
        {
            new PhaseReport("Coding", 1, "coder report — iteration 1"),
            new PhaseReport("DocWriting", 1, "doc report with\nmultiple\nlines and a TRAIL-MARKER"),
            new PhaseReport("Review", 1, new string('R', 5_000)),
        };

        PhaseReportMetadata.Write(metadata, reports);
        var read = PhaseReportMetadata.Read(metadata);

        // Order-insensitive: the contract fixes the KEY of each report, not the enumeration order
        // of the dictionary that carries it.
        Assert.Equal(reports.Length, read.Count);
        Assert.All(reports, report => Assert.Contains(report, read));
    }

    /// <summary>
    /// A round-trip through the TYPED read-only dictionary the worker side actually receives
    /// (<c>IReadOnlyDictionary</c>) returns the same reports.
    /// </summary>
    [Fact]
    public void WriteThenRead_ThroughReadOnlyDictionary_RoundTrips()
    {
        var metadata = new Dictionary<string, string>();
        PhaseReportMetadata.Write(metadata, [new PhaseReport("Testing", 3, "tester report")]);
        IReadOnlyDictionary<string, string> readOnly = metadata;

        var read = PhaseReportMetadata.Read(readOnly);

        Assert.Equal(new PhaseReport("Testing", 3, "tester report"), Assert.Single(read));
    }

    // ── Read: what it ignores ────────────────────────────────────────────────

    /// <summary>
    /// Unrelated metadata (the keys the dispatch also writes: tester_report, iteration_start_sha,
    /// compaction_model, ...) is never mistaken for a phase report.
    /// </summary>
    [Fact]
    public void Read_IgnoresUnrelatedKeys()
    {
        var metadata = new Dictionary<string, string>
        {
            ["tester_report"] = "the tester's report",
            ["iteration_start_sha"] = "abc123",
            ["phase_reportx:Coding:1"] = "wrong prefix",
            ["phase_report"] = "no segments",
            [""] = "empty key",
        };

        Assert.Empty(PhaseReportMetadata.Read(metadata));
    }

    /// <summary>
    /// Malformed occurrence values — non-numeric, empty, or any other shape that is not an
    /// integer — are skipped rather than defaulted to something the writer never meant.
    /// </summary>
    [Theory]
    [InlineData("phase_report:Coding:one")]
    [InlineData("phase_report:Coding:")]
    [InlineData("phase_report:Coding:1.5")]
    [InlineData("phase_report:Coding:0x2")]
    [InlineData("phase_report:Review:1:2")]
    public void Read_MalformedOccurrence_IsIgnored(string key)
    {
        var metadata = new Dictionary<string, string> { [key] = "report body" };

        Assert.Empty(PhaseReportMetadata.Read(metadata));
    }

    /// <summary>
    /// A key with an EMPTY phase token (<c>phase_report::1</c>) names no phase at all, so it is not
    /// a report entry — never read back as a phase-less report.
    /// </summary>
    [Fact]
    public void Read_EmptyPhaseSegment_IsIgnored()
    {
        var metadata = new Dictionary<string, string> { ["phase_report::1"] = "report body" };

        Assert.Empty(PhaseReportMetadata.Read(metadata));
    }

    /// <summary>A malformed entry is skipped WITHOUT hiding the well-formed ones beside it.</summary>
    [Fact]
    public void Read_MalformedKeyAmongValidOnes_KeepsOnlyTheValidReports()
    {
        var metadata = new Dictionary<string, string>
        {
            ["phase_report:Coding:1"] = "coder report",
            ["phase_report:Testing:not-a-number"] = "must be ignored",
            ["phase_report:DocWriting:2"] = "doc report",
        };

        var read = PhaseReportMetadata.Read(metadata);

        Assert.Equal(
            [new PhaseReport("Coding", 1, "coder report"), new PhaseReport("DocWriting", 2, "doc report")],
            read);
    }

    /// <summary>Metadata with no phase-report entries at all reads as an empty list, not null.</summary>
    [Fact]
    public void Read_NoPhaseReportEntries_ReturnsEmptyList()
    {
        Assert.Empty(PhaseReportMetadata.Read(new Dictionary<string, string>()));
    }

    /// <summary>A report whose body is empty is still a report — the entry exists and is read.</summary>
    [Fact]
    public void Read_EmptyReportBody_IsStillRead()
    {
        var metadata = new Dictionary<string, string> { ["phase_report:Improve:1"] = "" };

        Assert.Equal(new PhaseReport("Improve", 1, ""), Assert.Single(PhaseReportMetadata.Read(metadata)));
    }
}
