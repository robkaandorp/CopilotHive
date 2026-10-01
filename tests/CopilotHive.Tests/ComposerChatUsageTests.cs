using System.Text;

using CopilotHive.Components.Pages;
using CopilotHive.Goals;
using CopilotHive.Orchestration;

using SharpCoder;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// The token-usage presentation contract of the Composer chat status bar: the PURE
/// <see cref="ComposerChat.BuildUsageStatus"/> helper the Razor markup reads DIRECTLY, called here
/// directly through <c>InternalsVisibleTo</c>. No Blazor rendering, no timing and no Composer: the
/// helper is a function of a <see cref="ComposerUsageSnapshot"/> alone, so every assertion is about
/// the rendered text.
/// <para>
/// The snapshot's four members are OVERLAPPING views of the same model calls
/// (<c>ManualCompactions</c> is also inside <c>Session</c>, <c>Clarifications</c> is not part of it
/// at all), so these tests pin the two rules that follow from that: each figure is rendered from its
/// OWN member, and no two members are ever added together.
/// </para>
/// </summary>
public sealed class ComposerChatUsageTests
{
    // ── Model ids used by the fixtures ───────────────────────────────────────

    private const string AgentModel = "model-a";
    private const string CompactionModel = "model-b";

    // ── Null snapshot: disconnected ──────────────────────────────────────────

    /// <summary>
    /// A disconnected Composer (<see cref="Composer.GetUsage"/> returns <c>null</c>) contributes
    /// NOTHING to the status bar: no text and no tooltip, so the rest of the bar renders exactly as it
    /// did before usage reporting existed.
    /// <para>
    /// DISCRIMINATOR: a helper that fell back to an empty <see cref="ComposerUsageSnapshot"/> would
    /// still emit the unconditional <c>Session:</c> tooltip line, so the empty-tooltip assertion fails.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildUsageStatus_NullSnapshot_ContributesNoTextAndNoTooltip()
    {
        var status = ComposerChat.BuildUsageStatus(null);

        Assert.Equal("", status.Text);
        Assert.Empty(status.TooltipLines);
        Assert.Equal("", status.Tooltip);
    }

    // ── Status-bar text: which parts render ──────────────────────────────────

    /// <summary>
    /// A turn has not completed yet (<c>LastTurn</c> is null while a stream is in flight) but the
    /// session already has usage: only the session part is appended, and no <c>turn</c> figure is
    /// fabricated from the session total.
    /// <para>
    /// DISCRIMINATOR: rendering the turn part from <c>Session</c> (or from a substituted default)
    /// adds a <c>turn …</c> fragment and fails the ordering assertion.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildUsageStatus_LastTurnNullWithSessionCalls_ShowsOnlyTheSessionPart()
    {
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = null,
            Session = SessionWithTwoEntries(),
        };

        var status = ComposerChat.BuildUsageStatus(snapshot);

        Assert.Equal(" · session 12.3K tok", status.Text);
        Assert.DoesNotContain("turn", status.Text, StringComparison.Ordinal);

        // The tooltip has no per-turn line either: the session line is first.
        Assert.StartsWith("Session: ", status.TooltipLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(status.TooltipLines, line => line.StartsWith("Last turn:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Both figures present: the parts appear in the fixed order turn → session, in ONE joined status
    /// string, and each figure comes from its own member (the session total is the session's, not the
    /// turn's).
    /// </summary>
    [Fact]
    public void BuildUsageStatus_TurnAndSession_AppendBothPartsInOrder()
    {
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = TurnCompleted(),
            Session = SessionWithTwoEntries(),
        };

        var status = ComposerChat.BuildUsageStatus(snapshot);

        Assert.Equal(" · turn 950 tok · session 12.3K tok", status.Text);
        Assert.True(
            status.Text.IndexOf("turn", StringComparison.Ordinal)
                < status.Text.IndexOf("session", StringComparison.Ordinal),
            $"the turn part must precede the session part: '{status.Text}'");
    }

    /// <summary>
    /// The status bar omits a session that has made NO call — a freshly connected Composer reports a
    /// zero-call session, and a <c>session 0 tok</c> fragment would claim a measurement that never
    /// happened. A completed turn still shows on its own.
    /// </summary>
    [Fact]
    public void BuildUsageStatus_ZeroCallSession_OmitsTheSessionPart()
    {
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = TurnCompleted(),
            Session = new UsageSummary(),
        };

        var status = ComposerChat.BuildUsageStatus(snapshot);

        Assert.Equal(" · turn 950 tok", status.Text);
        Assert.DoesNotContain("session", status.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A snapshot whose members are all empty (a connected Composer that has made no call yet)
    /// contributes nothing to the bar — but still carries the session tooltip line, which is the
    /// anchor every other figure is read against.
    /// </summary>
    [Fact]
    public void BuildUsageStatus_EmptySnapshot_ContributesNoText()
    {
        var status = ComposerChat.BuildUsageStatus(new ComposerUsageSnapshot());

        Assert.Equal("", status.Text);
        Assert.Equal(
            new[] { "Session: input 0 / 0 cached input / output 0 / 0 reasoning / 0 calls" },
            status.TooltipLines);
    }

    // ── Status-bar text: exact compact values ───────────────────────────────

    /// <summary>
    /// The turn figure is <see cref="UsageFormat.FormatCompactTotal"/>'s compact label applied to the
    /// turn's OWN total, verbatim — including its unit boundaries (plain count, then one-decimal
    /// thousands, then millions).
    /// </summary>
    [Theory]
    [InlineData(0, 0, "0 tok")]
    [InlineData(900, 50, "950 tok")]
    [InlineData(12_000, 300, "12.3K tok")]
    [InlineData(1_234_567, 0, "1.2M tok")]
    public void BuildUsageStatus_TurnTotal_UsesTheCompactTotalLiteral(long input, long output, string expected)
    {
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = new UsageSummary(
                [new UsageEntry(UsageSource.Agent, AgentModel, Reported(input, output, calls: 1))]),
            Session = new UsageSummary(),
        };

        var status = ComposerChat.BuildUsageStatus(snapshot);

        Assert.Equal($" · turn {expected}", status.Text);
    }

    /// <summary>
    /// The session figure is the compact label of the SESSION's total — the sum of its own entries —
    /// so the bar and the tooltip's session line can never disagree.
    /// </summary>
    [Fact]
    public void BuildUsageStatus_SessionTotal_UsesTheCompactTotalLiteral()
    {
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = null,
            Session = SessionWithTwoEntries(),
        };

        // Premise: the fixture really aggregates to the asserted value.
        Assert.Equal(12_300, snapshot.Session.Total.InputTokens + snapshot.Session.Total.OutputTokens);

        var status = ComposerChat.BuildUsageStatus(snapshot);

        Assert.Equal(" · session 12.3K tok", status.Text);
    }

    // ── Tooltip: composition ────────────────────────────────────────────────

    /// <summary>
    /// The tooltip is the lines joined with NEWLINES (never a space-joined blob) and carries the
    /// per-(source, model) entry lines of the session underneath its total, through
    /// <see cref="UsageFormat.FormatEntryLines"/> — one line per bucket, in the summary's own order.
    /// </summary>
    [Fact]
    public void BuildUsageStatus_Tooltip_JoinsLinesWithNewlinesAndCarriesTheEntryLines()
    {
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = TurnCompleted(),
            Session = SessionWithTwoEntries(),
        };

        var status = ComposerChat.BuildUsageStatus(snapshot);

        // Line separation is real: the joined tooltip has exactly as many lines as the list, and the
        // list is what the markup's title attribute is built from.
        Assert.Equal(status.TooltipLines.Count, status.Tooltip.Split('\n').Length);
        Assert.Equal(string.Join("\n", status.TooltipLines), status.Tooltip);
        Assert.True(status.TooltipLines.Count > 1, "the tooltip must hold more than one line");

        // The session's per-(source, model) lines, exactly as the shared formatter renders them.
        var expectedEntries = UsageFormat.FormatEntryLines(snapshot.Session);
        Assert.Equal(2, expectedEntries.Count);
        foreach (var entryLine in expectedEntries)
        {
            Assert.Contains(entryLine, status.TooltipLines);
        }

        // Non-vacuous: the entry lines are the real, distinctive per-model breakdown.
        Assert.Contains($"Agent / {AgentModel}: input 12,000 /", status.Tooltip);
        Assert.Contains($"Compaction / {CompactionModel}: input 120 /", status.Tooltip);

        // …and they sit UNDER the session total, not above it or under the turn line.
        var sessionLineIndex = status.TooltipLines.ToList().FindIndex(
            line => line.StartsWith("Session: ", StringComparison.Ordinal));
        Assert.Equal(1, sessionLineIndex);
        Assert.Equal(expectedEntries[0], status.TooltipLines[sessionLineIndex + 1]);
        Assert.Equal(expectedEntries[1], status.TooltipLines[sessionLineIndex + 2]);
    }

    /// <summary>
    /// The per-turn line comes first in the tooltip and renders the TURN's own total through
    /// <see cref="UsageFormat.FormatUsageLine"/> (the full input/cached/output/reasoning/calls line),
    /// while the session line carries the session's.
    /// </summary>
    [Fact]
    public void BuildUsageStatus_Tooltip_LeadsWithTheTurnLineThenTheSessionLine()
    {
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = TurnCompleted(),
            Session = SessionWithTwoEntries(),
        };

        var status = ComposerChat.BuildUsageStatus(snapshot);

        Assert.Equal($"Last turn: {UsageFormat.FormatUsageLine(snapshot.LastTurn!.Total)}", status.TooltipLines[0]);
        Assert.Equal($"Session: {UsageFormat.FormatUsageLine(snapshot.Session.Total)}", status.TooltipLines[1]);

        // Non-vacuous: the two lines really are different figures from different members.
        Assert.NotEqual(status.TooltipLines[0], status.TooltipLines[1]);
        Assert.StartsWith("Last turn: input 900 / 25 cached input / output 50 / 5 reasoning / 1 calls", status.Tooltip, StringComparison.Ordinal);
        Assert.Contains("\nSession: input 12,120 /", status.Tooltip);
    }

    // ── Tooltip: the optional category rules pass through unchanged ─────────

    /// <summary>
    /// The provider-optional categories (cached input, reasoning) keep exactly the shared formatter's
    /// rendering: a PARTIAL category shows <c>≥N (partial)</c> and a category no call reported shows
    /// <c>unknown</c>. The helper must not re-derive or simplify those values.
    /// <para>
    /// DISCRIMINATOR: an implementation that printed the raw <c>CachedInputTokens</c> would render
    /// <c>7 cached input</c> and drop the <c>≥… (partial)</c> / <c>unknown</c> markers.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildUsageStatus_Tooltip_PartialAndUnknownCategoriesRenderThroughFormatUsageLine()
    {
        var partial = new TokenUsage
        {
            InputTokens = 500,
            OutputTokens = 100,
            Calls = 3,
            // 1 of 3 calls reported cached input → PARTIAL; no call reported reasoning → unknown.
            CachedInputTokens = 7,
            CachedInputReportedCalls = 1,
            ReasoningTokens = 0,
            ReasoningReportedCalls = 0,
        };
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = new UsageSummary([new UsageEntry(UsageSource.Agent, AgentModel, partial)]),
            Session = new UsageSummary([new UsageEntry(UsageSource.Agent, AgentModel, partial)]),
        };

        var status = ComposerChat.BuildUsageStatus(snapshot);

        // Premise: this fixture really exercises both rules.
        Assert.Equal(
            "input 500 / ≥7 (partial) cached input / output 100 / unknown reasoning / 3 calls",
            UsageFormat.FormatUsageLine(partial));

        // Unchanged through the helper: the entry line carries the formatter's exact text…
        Assert.Equal(
            $"Agent / {AgentModel}: input 500 / ≥7 (partial) cached input / output 100 / unknown reasoning / 3 calls",
            Assert.Single(UsageFormat.FormatEntryLines(snapshot.Session)));
        Assert.Contains(
            $"Agent / {AgentModel}: input 500 / ≥7 (partial) cached input / output 100 / unknown reasoning / 3 calls",
            status.TooltipLines);

        // …and nothing on the tooltip rewrites them.
        Assert.DoesNotContain("7 cached input", status.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("0 reasoning", status.Tooltip, StringComparison.Ordinal);
    }

    // ── Tooltip: the two "since start" lines and their gating ───────────────

    /// <summary>
    /// The clarification and manual-compaction lines are OMITTED while their own <c>Calls</c> counter
    /// is zero: a category with no call has no measurement to report, and a <c>0 calls</c> line would
    /// present "never measured" as a real zero.
    /// <para>
    /// DISCRIMINATOR: dropping either <c>Calls &gt; 0</c> guard renders the line with a zero-call
    /// payload and fails the corresponding absence assertion.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildUsageStatus_Tooltip_OmitsClarificationAndManualCompactionLinesWhenNoCalls()
    {
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = TurnCompleted(),
            Session = SessionWithTwoEntries(),
            Clarifications = new UsageSummary(),
            ManualCompactions = new UsageSummary(),
        };

        var status = ComposerChat.BuildUsageStatus(snapshot);

        Assert.DoesNotContain("Clarifications", status.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("Manual compactions", status.Tooltip, StringComparison.Ordinal);

        // The whole tooltip is exactly the turn line + session line + its two entry lines.
        Assert.Equal(4, status.TooltipLines.Count);
    }

    /// <summary>
    /// With calls in both categories, each line appears — AFTER the session's entry lines, in the
    /// fixed order clarifications → manual compactions — carrying its own member's total through
    /// <see cref="UsageFormat.FormatUsageLine"/>, and labelled with its membership semantics.
    /// </summary>
    [Fact]
    public void BuildUsageStatus_Tooltip_AddsClarificationAndManualCompactionLinesWhenCallsExist()
    {
        var clarifications = new UsageSummary(
            [new UsageEntry(UsageSource.SubAgent, "clarify-model", Reported(1_000, 200, calls: 2))]);
        var manualCompactions = new UsageSummary(
            [new UsageEntry(UsageSource.Compaction, CompactionModel, Reported(400, 40, calls: 1))]);
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = TurnCompleted(),
            Session = SessionWithTwoEntries(),
            Clarifications = clarifications,
            ManualCompactions = manualCompactions,
        };

        var status = ComposerChat.BuildUsageStatus(snapshot);

        Assert.Contains(
            $"Clarifications (since start): {UsageFormat.FormatUsageLine(clarifications.Total)}",
            status.TooltipLines);
        Assert.Contains(
            $"Manual compactions (since start, included in session): {UsageFormat.FormatUsageLine(manualCompactions.Total)}",
            status.TooltipLines);

        // Non-vacuous: both payloads carry this fixture's distinctive numbers, and the membership
        // labels state how the two categories relate to the session total.
        Assert.Contains("Clarifications (since start): input 1,000 /", status.Tooltip);
        Assert.Contains("Manual compactions (since start, included in session): input 400 /", status.Tooltip);

        // Fixed order: the session total and its two entry lines, then clarifications, then manual
        // compactions — the last two only AFTER the session breakdown they are read against.
        var lines = status.TooltipLines.ToList();
        Assert.Equal(6, lines.Count);
        var sessionLineIndex = lines.FindIndex(line => line.StartsWith("Session: ", StringComparison.Ordinal));
        Assert.Equal(1, sessionLineIndex);
        Assert.Equal("Agent / model-a: ", lines[sessionLineIndex + 1][.."Agent / model-a: ".Length]);
        Assert.Equal("Compaction / model-b: ", lines[sessionLineIndex + 2][.."Compaction / model-b: ".Length]);
        Assert.StartsWith("Clarifications (since start): ", lines[sessionLineIndex + 3], StringComparison.Ordinal);
        Assert.StartsWith("Manual compactions (since start, included in session): ", lines[sessionLineIndex + 4], StringComparison.Ordinal);
    }

    /// <summary>
    /// The clarifying and manual-compaction members are NEVER added to any other figure: their calls
    /// and tokens stay out of the turn and session numbers, in BOTH the bar and the tooltip. This is
    /// the whole point of the overlapping-members contract (<c>ManualCompactions</c> is ALREADY inside
    /// <c>Session</c>; <c>Clarifications</c> is not in it at all), so the rendered figures must equal
    /// the members read directly.
    /// <para>
    /// DISCRIMINATOR: summing the members into the session line would inflate it by this fixture's
    /// 2 clarification + 1 manual-compaction calls and fail both equality assertions.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildUsageStatus_NeverSumsTheMembersTogether()
    {
        var session = SessionWithTwoEntries();
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = TurnCompleted(),
            Session = session,
            Clarifications = new UsageSummary(
                [new UsageEntry(UsageSource.SubAgent, "clarify-model", Reported(900_000, 90_000, calls: 2))]),
            ManualCompactions = new UsageSummary(
                [new UsageEntry(UsageSource.Compaction, CompactionModel, Reported(700_000, 70_000, calls: 1))]),
        };

        // Premise: folding the two decoys into the session would really change the rendered label, so
        // "the bar shows the session's own label" is a discriminating observation and not a coin flip.
        var folded = new TokenUsage
        {
            InputTokens = session.Total.InputTokens + snapshot.Clarifications.Total.InputTokens + snapshot.ManualCompactions.Total.InputTokens,
            OutputTokens = session.Total.OutputTokens + snapshot.Clarifications.Total.OutputTokens + snapshot.ManualCompactions.Total.OutputTokens,
        };
        Assert.NotEqual(UsageFormat.FormatCompactTotal(session.Total), UsageFormat.FormatCompactTotal(folded));

        var status = ComposerChat.BuildUsageStatus(snapshot);

        // The bar keeps the session's own compact label — not session + clarifications + compactions.
        Assert.Equal(" · turn 950 tok · session 12.3K tok", status.Text);

        // …and the tooltip's session line keeps the session's own full line.
        Assert.Equal($"Session: {UsageFormat.FormatUsageLine(session.Total)}", status.TooltipLines[1]);

        // The decoy figures appear exactly once each, in their OWN lines — never folded anywhere else.
        Assert.Contains("Clarifications (since start): input 900,000 /", status.Tooltip);
        Assert.Contains("Manual compactions (since start, included in session): input 700,000 /", status.Tooltip);
        Assert.DoesNotContain(UsageFormat.FormatCompactTotal(folded), status.Text, StringComparison.Ordinal);
    }

    // ── Purity: the helper never mutates the snapshot ───────────────────────

    /// <summary>
    /// The helper only READS the snapshot: the members' totals, entry counts and per-entry numbers are
    /// identical before and after the call, and a second call renders the same view. A helper that
    /// consumed, cleared or rewrote any member would change the snapshot's own state (and the next
    /// render would drift).
    /// <para>
    /// The mutation probe is verified non-vacuous inside the test: an explicit
    /// <see cref="UsageSummary.Add(SharpCoder.UsageEvent)"/> on a SEPARATE snapshot built by the same
    /// fixture is detected by the very same description, so "unchanged" cannot pass for a probe that
    /// observes nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildUsageStatus_DoesNotMutateTheSnapshot()
    {
        var snapshot = new ComposerUsageSnapshot
        {
            LastTurn = TurnCompleted(),
            Session = SessionWithTwoEntries(),
            Clarifications = new UsageSummary(
                [new UsageEntry(UsageSource.SubAgent, "clarify-model", Reported(1_000, 200, calls: 2))]),
            ManualCompactions = new UsageSummary(
                [new UsageEntry(UsageSource.Compaction, CompactionModel, Reported(400, 40, calls: 1))]),
        };

        var before = Describe(snapshot);

        var first = ComposerChat.BuildUsageStatus(snapshot);
        var second = ComposerChat.BuildUsageStatus(snapshot);

        Assert.Equal(before, Describe(snapshot));

        // Idempotent: nothing about the first call changed what the second renders.
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.TooltipLines, second.TooltipLines);

        // Positive control: the description really does observe a mutation of these members.
        var control = new ComposerUsageSnapshot
        {
            LastTurn = TurnCompleted(),
            Session = SessionWithTwoEntries(),
            Clarifications = new UsageSummary(
                [new UsageEntry(UsageSource.SubAgent, "clarify-model", Reported(1_000, 200, calls: 2))]),
            ManualCompactions = new UsageSummary(
                [new UsageEntry(UsageSource.Compaction, CompactionModel, Reported(400, 40, calls: 1))]),
        };
        Assert.Equal(before, Describe(control));
        control.Session.Add(new UsageEvent(UsageSource.Agent, "mutant-model", Reported(9_999, 999, calls: 1), null));
        Assert.NotEqual(before, Describe(control));
    }

    // ── Wire-up: the status bar renders the helper ───────────────────────────

    /// <summary>
    /// REMOVAL-PROOF wire-up check: the markup really reads the helper's view — the usage span renders
    /// <c>@usageStatus.Text</c> with the tooltip in its <c>title</c> attribute — and the view is
    /// computed DURING RENDER from <c>Composer.GetUsage()</c>, after the ctx part of the bar.
    /// <para>
    /// The project has no bUnit, so the assertion reads the page source's status-bar region: it is what
    /// fails if the span is deleted, if the helper is bypassed by a hardcoded string, or if the status
    /// stops coming from the Composer's own snapshot.
    /// </para>
    /// </summary>
    [Fact]
    public void StatusBarMarkup_RendersTheHelperTextAndTooltipFromTheComposerSnapshot()
    {
        var statusBar = ReadStatusBarRegion();

        // The status-bar text and the tooltip both come from the helper's single view.
        Assert.Contains("BuildUsageStatus(Composer.GetUsage())", statusBar, StringComparison.Ordinal);
        Assert.Contains("@usageStatus.Text", statusBar, StringComparison.Ordinal);
        Assert.Contains("title=\"@usageStatus.Tooltip\"", statusBar, StringComparison.Ordinal);

        // Appended AFTER the ctx part, so the existing figures keep their positions.
        var ctxIndex = statusBar.IndexOf("ContextUsagePercent%", StringComparison.Ordinal);
        var usageIndex = statusBar.IndexOf("@usageStatus.Text", StringComparison.Ordinal);
        Assert.True(ctxIndex >= 0, "the ctx part must still be rendered");
        Assert.True(usageIndex > ctxIndex, "the usage text must follow the ctx part");

        // No NEW event/timer/polling machinery: the view is derived from the render, nothing here
        // schedules a refresh or subscribes to a usage signal.
        Assert.DoesNotContain("Timer", statusBar, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", statusBar, StringComparison.Ordinal);
        Assert.DoesNotContain("OnUsage", statusBar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The usage span is rendered only for a non-empty view: an empty status text (disconnected, or
    /// nothing measured yet) leaves the status bar exactly as it was, with no stray empty span.
    /// </summary>
    [Fact]
    public void StatusBarMarkup_UsageSpanIsGuardedByNonEmptyText()
    {
        var statusBar = ReadStatusBarRegion();

        var guardIndex = statusBar.IndexOf("if (usageStatus.Text.Length > 0)", StringComparison.Ordinal);
        var spanIndex = statusBar.IndexOf("title=\"@usageStatus.Tooltip\"", StringComparison.Ordinal);

        Assert.True(guardIndex >= 0, "the usage span must be guarded by a non-empty-text check");
        Assert.True(spanIndex > guardIndex, "the guard must precede the span it protects");
    }

    /// <summary>
    /// Reads the status-bar region of the real <c>ComposerChat.razor</c> source: everything between the
    /// muted status span's opening tag and the compacting marker that follows it, so assertions target
    /// the bar rather than an unrelated part of the page.
    /// </summary>
    private static string ReadStatusBarRegion()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);

        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", "ComposerChat.razor");
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        var source = File.ReadAllText(razorPath);

        const string startMarker = "<span class=\"text-muted chat-hint\">";
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the chat status bar span was not found in ComposerChat.razor");

        const string endMarker = "@if (Composer.IsCompacting)";
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, "the compacting marker that closes the status bar was not found");

        return source[start..end];
    }

    // ── Fixtures ────────────────────────────────────────────────────────────
    /// <summary>A token bucket for which BOTH optional provider categories are reported.</summary>
    private static TokenUsage Reported(long input, long output, int calls) => new()
    {
        InputTokens = input,
        OutputTokens = output,
        Calls = calls,
        CachedInputTokens = input / 4,
        CachedInputReportedCalls = calls,
        ReasoningTokens = output / 10,
        ReasoningReportedCalls = calls,
    };

    /// <summary>
    /// A completed turn: 950 tokens over 1 call (900 in / 50 out), with both optional categories
    /// reported — the compact label is <c>950 tok</c>.
    /// </summary>
    private static UsageSummary TurnCompleted() => new(
    [
        new UsageEntry(UsageSource.Agent, AgentModel, new TokenUsage
        {
            InputTokens = 900,
            OutputTokens = 50,
            Calls = 1,
            CachedInputTokens = 25,
            CachedInputReportedCalls = 1,
            ReasoningTokens = 5,
            ReasoningReportedCalls = 1,
        }),
    ]);

    /// <summary>
    /// A session with two (source, model) buckets — the agent's and a manual compaction's — summing to
    /// 12,300 tokens over 4 calls, so the compact label is <c>12.3K tok</c> and the tooltip carries two
    /// entry lines.
    /// </summary>
    private static UsageSummary SessionWithTwoEntries() => new(
    [
        new UsageEntry(UsageSource.Agent, AgentModel, new TokenUsage
        {
            InputTokens = 12_000,
            OutputTokens = 180,
            Calls = 3,
            CachedInputTokens = 3_000,
            CachedInputReportedCalls = 3,
            ReasoningTokens = 30,
            ReasoningReportedCalls = 3,
        }),
        new UsageEntry(UsageSource.Compaction, CompactionModel, new TokenUsage
        {
            InputTokens = 120,
            OutputTokens = 0,
            Calls = 1,
            CachedInputTokens = 30,
            CachedInputReportedCalls = 1,
            ReasoningTokens = 0,
            ReasoningReportedCalls = 1,
        }),
    ]);

    /// <summary>
    /// A canonical description of EVERY number the helper can read: each member's aggregate counters
    /// and each entry's (source, model) plus counters. Two snapshots with the same description read
    /// identically, so comparing descriptions before and after a call detects any read-side mutation.
    /// </summary>
    private static string Describe(ComposerUsageSnapshot snapshot)
    {
        var builder = new StringBuilder();
        DescribeMember(builder, "LastTurn", snapshot.LastTurn);
        DescribeMember(builder, "Session", snapshot.Session);
        DescribeMember(builder, "Clarifications", snapshot.Clarifications);
        DescribeMember(builder, "ManualCompactions", snapshot.ManualCompactions);
        return builder.ToString();
    }

    private static void DescribeMember(StringBuilder builder, string name, UsageSummary? summary)
    {
        if (summary is null)
        {
            builder.Append(name).Append(": <null>\n");
            return;
        }

        var total = summary.Total;
        builder.Append(name)
            .Append(": input=").Append(total.InputTokens)
            .Append(" output=").Append(total.OutputTokens)
            .Append(" cached=").Append(total.CachedInputTokens)
            .Append(" cachedReported=").Append(total.CachedInputReportedCalls)
            .Append(" reasoning=").Append(total.ReasoningTokens)
            .Append(" reasoningReported=").Append(total.ReasoningReportedCalls)
            .Append(" calls=").Append(total.Calls)
            .Append(" entries=").Append(summary.Entries.Count)
            .Append('\n');

        foreach (var entry in summary.Entries)
        {
            builder.Append("  entry ")
                .Append(entry.Source).Append('/').Append(entry.Model ?? "<null>")
                .Append(": input=").Append(entry.Usage.InputTokens)
                .Append(" output=").Append(entry.Usage.OutputTokens)
                .Append(" calls=").Append(entry.Usage.Calls)
                .Append('\n');
        }
    }
}
