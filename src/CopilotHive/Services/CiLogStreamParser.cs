using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CopilotHive.Services;

/// <summary>
/// Redacts secrets from CI log text <em>before</em> any of it becomes model- or user-visible
/// output (issue titles, issue descriptions, dedup appends, fallback snippets, or the parsed
/// per-test error/stack fields).
/// </summary>
/// <remarks>
/// This type owns the single copy of the CI secret patterns: <see cref="CiMonitorService.SanitizeLogContent"/>
/// and <see cref="CiLogStreamParser"/> both delegate here, so the whole-string and streaming paths
/// can never drift apart. Every pattern's trigger and value live inside one line (the
/// <c>Password=</c>, <c>Bearer</c>, and environment-variable values are bounded by the end of the
/// line), which is what makes it safe for the streaming parser to redact one complete log line at
/// a time: a secret split across two stream chunks is re-joined into its line before redaction.
/// </remarks>
internal static class CiLogRedactor
{
    /// <summary>Text written in place of every redacted secret value.</summary>
    internal const string RedactionMarker = "***REDACTED***";

    /// <summary>Redacts <c>ghp_</c> GitHub personal-access tokens (36-character secrets).</summary>
    private static readonly Regex GhpTokenRegex = new(
        @"ghp_[A-Za-z0-9]{36}",
        RegexOptions.Compiled);

    /// <summary>Redacts <c>gho_</c> GitHub OAuth access tokens (36-character secrets).</summary>
    private static readonly Regex GhoTokenRegex = new(
        @"gho_[A-Za-z0-9]{36}",
        RegexOptions.Compiled);

    /// <summary>Redacts <c>ghs_</c> GitHub user-to-server tokens (36-character secrets).</summary>
    private static readonly Regex GhsTokenRegex = new(
        @"ghs_[A-Za-z0-9]{36}",
        RegexOptions.Compiled);

    /// <summary>Redacts HTTP <c>Bearer</c> authorization tokens (any base64-ish value).</summary>
    private static readonly Regex BearerTokenRegex = new(
        @"Bearer [A-Za-z0-9._\-]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Redacts secret-looking environment variables (names containing TOKEN, KEY, SECRET, or
    /// PASSWORD) while preserving the key name. Multiline (<c>RegexOptions.Multiline</c>) and
    /// case-sensitive so ordinary words such as <c>token</c> in prose are never touched.
    /// </summary>
    /// <remarks>
    /// The key must start the line, but a raw GitHub Actions log line starts with an ISO-8601
    /// timestamp (<c>2026-08-22T09:47:38.1197374Z MY_API_TOKEN=…</c>), and redaction runs BEFORE
    /// the timestamp is stripped for parsing. The optional leading timestamp group is therefore part
    /// of the anchor, so a timestamped line is redacted exactly like a bare one. The timestamp is
    /// kept verbatim (group 1) and only the value is replaced.
    /// </remarks>
    private static readonly Regex SecretEnvVarRegex = new(
        @"^((?:\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z[ \t]+)?)([A-Z_]*(?:TOKEN|KEY|SECRET|PASSWORD)[A-Z_]*)=.+$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Redacts ADO-style <c>Password=...</c> connection-string fragments.</summary>
    private static readonly Regex PasswordAssignmentRegex = new(
        @"Password=[^;]+",
        RegexOptions.Compiled);

    /// <summary>
    /// The token triggers whose value has a fixed length, and therefore can be left as an
    /// unredactable fragment when a bounded read cuts the line they live on. See
    /// <see cref="TrimUnredactableTail"/>.
    /// </summary>
    private static readonly string[] FixedLengthTokenTriggers = ["ghp_", "gho_", "ghs_"];

    /// <summary>
    /// Redacts every supported secret category from <paramref name="text"/>: GitHub tokens
    /// (<c>ghp_</c>/<c>gho_</c>/<c>ghs_</c>), <c>Bearer</c> authorization headers, secret-looking
    /// environment variables, and <c>Password=</c> connection-string fragments. Never truncates
    /// the input — every character of the input survives, with only secret values replaced.
    /// </summary>
    /// <param name="text">The raw text to redact.</param>
    /// <returns>The redacted text.</returns>
    internal static string Redact(string text)
    {
        var redacted = GhpTokenRegex.Replace(text, RedactionMarker);
        redacted = GhoTokenRegex.Replace(redacted, RedactionMarker);
        redacted = GhsTokenRegex.Replace(redacted, RedactionMarker);
        redacted = BearerTokenRegex.Replace(redacted, "Bearer " + RedactionMarker);
        redacted = SecretEnvVarRegex.Replace(redacted, "$1$2=" + RedactionMarker);
        redacted = PasswordAssignmentRegex.Replace(redacted, "Password=" + RedactionMarker);
        return redacted;
    }

    /// <summary>Length of the leading run of token-body characters, capped at <paramref name="limit"/>.</summary>
    /// <param name="text">The text to inspect.</param>
    /// <param name="limit">The longest run to report: the 40 characters a complete GitHub token spans.</param>
    internal static int LeadingTokenRunLength(string text, int limit)
    {
        var run = 0;
        while (run < text.Length && run < limit
            && (char.IsAsciiLetterOrDigit(text[run]) || text[run] == '_'))
            run++;
        return run;
    }

    /// <summary>
    /// Redacts one <em>complete</em> log line for user- or model-visible output: runs
    /// <see cref="Redact"/> and then <see cref="TrimUnredactableTail"/>.
    /// </summary>
    /// <remarks>
    /// The trim is unconditional because a line can end early for two different reasons, and both
    /// leave the same unredactable fragment:
    /// <list type="bullet">
    /// <item>the line was cut by <see cref="CiLogStreamParser.MaxLogLineChars"/>;</item>
    /// <item>the line is the LAST line of a log that simply stops mid-token — an unterminated final
    /// line, or a log whose body ends inside a token. Nothing about a newline-terminated line
    /// prevents this: the raw log itself can end anywhere.</item>
    /// </list>
    /// Running the trim on every line therefore closes both boundaries with one rule. It can only
    /// ever redact MORE than <see cref="Redact"/> alone (a bare <c>ghp_</c> mentioned in prose
    /// becomes <see cref="RedactionMarker"/> through to the end of that line), so it fails closed:
    /// over-redaction is acceptable, a surviving token fragment is not.
    /// </remarks>
    /// <param name="completeLine">One complete log line, without its newline.</param>
    /// <returns>The redacted line, safe for user- or model-visible output.</returns>
    internal static string RedactCompleteLine(string completeLine) =>
        TrimUnredactableTail(Redact(completeLine));

    /// <summary>
    /// Removes the unredactable fragment a bounded read can leave behind at the END of the text it
    /// was given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value-bounded categories (<c>Bearer</c>, <c>Password=</c>, secret environment
    /// variables) always redact to the end of the retained text, so a cut value is still fully
    /// redacted. The fixed-length <c>ghp_</c>/<c>gho_</c>/<c>ghs_</c> patterns only match when all
    /// 36 trailing characters are present, so a token whose text ends early survives as a
    /// fragment. Because every <em>complete</em> token has already been replaced by
    /// <see cref="RedactionMarker"/> at this point, any remaining trigger is by construction an
    /// incomplete token: everything from it to the end of the text is replaced.
    /// </para>
    /// <para>
    /// Callers must invoke this at EVERY boundary where the text can end early, not only at the
    /// per-line cap: a line cut by the cap, and equally the last line of a log that simply stops
    /// mid-token (an unterminated final line, or a bounded retention window that ends inside a
    /// token). Both are the same defect — a truncated token — so both take the same trim.
    /// </para>
    /// </remarks>
    /// <param name="redactedText">Already-redacted text whose tail may be a partial token.</param>
    /// <returns>The text with any partial token replaced by <see cref="RedactionMarker"/>.</returns>
    internal static string TrimUnredactableTail(string redactedText)
    {
        var cut = -1;
        foreach (var trigger in FixedLengthTokenTriggers)
        {
            var index = redactedText.LastIndexOf(trigger, StringComparison.Ordinal);
            if (index > cut)
                cut = index;
        }

        return cut < 0
            ? redactedText
            : string.Concat(redactedText.AsSpan(0, cut), RedactionMarker);
    }
}

/// <summary>
/// Bounded retention of parsed CI test failures, shared by every job log scanned for one log
/// fetch so the bounds apply to the aggregate and not per job.
/// </summary>
/// <remarks>
/// Retention is a strict prefix of the parsed occurrences: the first failure that does not fit
/// the remaining budget closes retention, so the omitted set is always a contiguous suffix and
/// the overflow count (<c>parsed - retained</c>) stays exact.
/// </remarks>
internal sealed class CiFailureRetention
{
    private readonly int _maxFailures;
    private readonly int _maxChars;
    private long _usedChars;
    private bool _exhausted;

    /// <summary>Creates a retention budget.</summary>
    /// <param name="maxFailures">Maximum number of retained failure occurrences.</param>
    /// <param name="maxChars">Maximum aggregate decoded characters of retained failure fields.</param>
    internal CiFailureRetention(int maxFailures, int maxChars)
    {
        _maxFailures = maxFailures;
        _maxChars = maxChars;
    }

    /// <summary>
    /// The production budget: <see cref="CiLogStreamParser.MaxRetainedFailureCount"/> failures and
    /// <see cref="CiLogStreamParser.MaxRetainedFailureChars"/> characters per log fetch.
    /// </summary>
    internal static CiFailureRetention CreateProduction() =>
        new(CiLogStreamParser.MaxRetainedFailureCount, CiLogStreamParser.MaxRetainedFailureChars);

    /// <summary>An unbounded budget, used only for logs already held in memory by callers.</summary>
    internal static CiFailureRetention CreateUnbounded() => new(int.MaxValue, int.MaxValue);

    /// <summary>Failures retained so far, in log order.</summary>
    internal List<(string TestName, string Error, string StackTrace)> Failures { get; } = [];

    /// <summary>
    /// Retains one parsed failure when the remaining budget allows it.
    /// </summary>
    /// <param name="testName">The parsed test name.</param>
    /// <param name="error">The parsed error text.</param>
    /// <param name="stackTrace">The parsed stack trace.</param>
    /// <returns><see langword="true"/> when the failure was retained.</returns>
    internal bool TryRetain(string testName, string error, string stackTrace)
    {
        if (_exhausted)
            return false;

        if (Failures.Count >= _maxFailures)
        {
            _exhausted = true;
            return false;
        }

        // Widened so the unbounded budget (int.MaxValue characters) cannot overflow the running
        // total for a very large in-memory log.
        var cost = (long)testName.Length + error.Length + stackTrace.Length;
        if (_usedChars + cost > _maxChars)
        {
            _exhausted = true;
            return false;
        }

        Failures.Add((testName, error, stackTrace));
        _usedChars += cost;
        return true;
    }

    /// <summary>A restore point of the retention state, taken before one job's log is scanned.</summary>
    /// <param name="Count">Number of retained failures at the checkpoint.</param>
    /// <param name="UsedChars">Aggregate retained characters at the checkpoint.</param>
    /// <param name="Exhausted">Whether the budget was already closed at the checkpoint.</param>
    internal readonly record struct Checkpoint(int Count, long UsedChars, bool Exhausted);

    /// <summary>Captures the current state so a failed job's contributions can be undone.</summary>
    internal Checkpoint Mark() => new(Failures.Count, _usedChars, _exhausted);

    /// <summary>
    /// Discards everything retained since <paramref name="checkpoint"/>. A job whose log download
    /// fails part-way has already offered failures to this SHARED run-wide budget; keeping them would
    /// let a broken download consume the budget and displace later, successfully downloaded jobs
    /// while the occurrence accounting (which only counts successful jobs) no longer matched the
    /// retained set. Rolling back restores both the retained list and the budget exactly.
    /// </summary>
    /// <param name="checkpoint">The state captured by <see cref="Mark"/>.</param>
    internal void Rollback(Checkpoint checkpoint)
    {
        if (Failures.Count > checkpoint.Count)
            Failures.RemoveRange(checkpoint.Count, Failures.Count - checkpoint.Count);

        _usedChars = checkpoint.UsedChars;
        _exhausted = checkpoint.Exhausted;
    }
}

/// <summary>
/// Streaming CI job-log parser: reads a job log of ANY size in fixed-size chunks, extracts
/// VSTest/xUnit and Microsoft.Testing.Platform (MTP) failure blocks incrementally across chunk
/// boundaries, and keeps both its working memory and its retained output bounded.
/// </summary>
/// <remarks>
/// <para>
/// The parser never buffers the whole log and never stops reading at a byte cap: only the
/// in-progress line, the in-progress failure block, and a bounded head/tail excerpt of the
/// redacted text are held at any moment. Failure extraction is incremental — a failure header
/// opens a block, the block's own lines are accumulated up to
/// <see cref="MaxFailureBlockChars"/>, and the block is finalized by the next failure header of
/// either format (VSTest blocks) or by the next failure header or MTP result/footer line (MTP
/// blocks), exactly as the whole-log parser does.
/// </para>
/// <para>
/// Secret redaction runs on each <em>complete</em> line before it is parsed, counted, or retained,
/// so the carried tail of a chunk (the incomplete line) always passes through the redactor as part
/// of its line and a token split across two chunks cannot leak. When a line is longer than
/// <see cref="MaxLogLineChars"/> the retained prefix is redacted first and truncated afterwards,
/// and <see cref="CiLogRedactor.TrimUnredactableTail"/> removes any fixed-length token fragment
/// the truncation boundary would otherwise expose.
/// </para>
/// </remarks>
internal sealed class CiLogStreamParser
{
    /// <summary>
    /// Maximum number of raw bytes pulled from the log stream in a single read. Units: raw bytes.
    /// Bounds the parser's byte buffer; reads are independent of the total log size.
    /// </summary>
    internal const int StreamChunkBytes = 16 * 1024;

    /// <summary>
    /// Maximum number of decoded characters retained for ONE log line. Units: decoded UTF-16
    /// characters. A longer line (e.g. a minified stack blob) is capped with an explicit
    /// truncation marker reporting how many decoded characters were dropped, so a single huge
    /// line can never exhaust memory.
    /// </summary>
    internal const int MaxLogLineChars = 8 * 1024;

    /// <summary>
    /// Maximum number of decoded characters retained for ONE in-progress failure block (the lines
    /// between a failure header and its terminator). Units: decoded UTF-16 characters. A block
    /// that grows past this bound keeps being tracked (so its terminator still closes it) while
    /// further lines are counted and dropped, and the extracted fields carry an explicit truncation
    /// marker. A block so large that its <c>Error Message:</c> section falls past this bound holds no
    /// extractable failure and is reported as unparseable rather than retained.
    /// </summary>
    internal const int MaxFailureBlockChars = 16 * 1024;

    /// <summary>
    /// Maximum number of parsed test-failure OCCURRENCES retained for one log fetch (aggregated
    /// across every failed job of the run). Units: count of failures. Later occurrences are
    /// omitted and reported through the log fetch result's overflow summary.
    /// </summary>
    internal const int MaxRetainedFailureCount = 200;

    /// <summary>
    /// Maximum aggregate decoded characters of retained failure fields (test name + error + stack
    /// trace, summed over retained failures) for one log fetch. Units: decoded UTF-16 characters.
    /// </summary>
    internal const int MaxRetainedFailureChars = 200 * 1024;

    /// <summary>
    /// Maximum number of decoded characters kept from the START of a job log for the no-failure
    /// fallback excerpt. Units: decoded UTF-16 characters.
    /// </summary>
    internal const int FallbackHeadChars = 2_000;

    /// <summary>
    /// Maximum number of decoded characters kept from the END of a job log for the no-failure
    /// fallback excerpt. Units: decoded UTF-16 characters.
    /// </summary>
    internal const int FallbackTailChars = 2_000;

    /// <summary>
    /// UTF-8 decoder with a replacement fallback: an incomplete multi-byte sequence at the end of
    /// a chunk is held by the decoder and flushed as U+FFFD only at the end of the stream, so a
    /// character split across two chunks decodes correctly and a genuinely truncated final
    /// sequence decodes to the replacement character instead of throwing.
    /// </summary>
    internal static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Matches an xUnit failure header line: <c>Failed {name} [duration]</c>. The name is
    /// captured non-greedily up to the trailing duration bracket, so theory names containing
    /// spaces still parse. Multiline so headers are anchored to their own line, and leading
    /// whitespace is tolerated because console output indents failure lines.
    /// </summary>
    private static readonly Regex XUnitFailedTestRegex = new(
        @"^\s*Failed\s+(.+?)\s+\[\d+[^\]]*\]\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Matches a Microsoft.Testing.Platform (MTP) failure header line:
    /// <c>failed {identity} ({duration})</c>. MTP prints the word <c>failed</c> in lower case and
    /// the duration in parentheses rather than xUnit's upper-case <c>Failed {name} [duration]</c>
    /// shape, so the VSTest header regex never matches it. The identity is captured non-greedily
    /// up to the trailing duration parentheses, so theory names containing spaces, parentheses,
    /// and quotes (e.g. <c>…​(label: "case with spaces")</c>) still parse. MTP's
    /// HumanReadableDurationFormatter emits space-separated multi-component durations
    /// (<c>0ms</c>, <c>234ms</c>, <c>1s 234ms</c>, <c>4m 54s 690ms</c>), so each component is
    /// digit-led and further components may follow it separated by whitespace; the identity stays
    /// anchored because the duration must run to the end of the line.
    /// </summary>
    private static readonly Regex MtpFailedTestRegex = new(
        @"^\s*failed\s+(.+?)\s+\(\d+[^\s)]*(?:\s+\d+[^\s)]*)*\)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Matches the indented <c>from {dll} ({tfm}|{arch})</c> line MTP prints directly under a
    /// failure header, identifying the assembly the test ran in.
    /// </summary>
    private static readonly Regex MtpFromLineRegex = new(
        @"^\s*from\s+.+?\([^)]*\)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches a bare stack-frame line (<c>at {method} …</c>) inside an MTP failure block. MTP
    /// prints no <c>Stack Trace:</c> label, so the trailing run of such lines is the trace. The
    /// leading whitespace is optional because <see cref="StripActionsTimestamp"/> consumes the
    /// indentation that follows a GitHub Actions timestamp prefix.
    /// </summary>
    private static readonly Regex MtpStackTraceLineRegex = new(
        @"^\s*at\s+\S",
        RegexOptions.Compiled);

    /// <summary>Matches the <c>Error Message:</c> section header of an xUnit failure block.</summary>
    private static readonly Regex XUnitErrorMessageRegex = new(
        @"^\s*Error Message:\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Matches the <c>Stack Trace:</c> section header of an xUnit failure block.</summary>
    private static readonly Regex XUnitStackTraceRegex = new(
        @"^\s*Stack Trace:\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Matches the <c>--- End of stack trace ---</c> marker that terminates a stack-trace
    /// section in .NET/xUnit output.
    /// </summary>
    private static readonly Regex StackTraceEndMarkerRegex = new(
        @"^\s*---\s*End of stack trace",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches the ISO-8601 timestamp prefix GitHub Actions prepends to every raw log line
    /// (e.g. <c>2026-08-22T09:47:38.1197374Z </c>). Stripped before failure parsing so headers
    /// and section markers anchor to the start of the line.
    /// </summary>
    private static readonly Regex GitHubActionsTimestampRegex = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches the line that terminates a Microsoft.Testing.Platform failure block: the next
    /// per-test result line (<c>failed|passed|skipped|…</c> followed by a dotted identity, in
    /// either case so a following VSTest-format block also terminates it) or a run-level footer
    /// such as <c>Test run summary:</c>, <c>Exit code:</c>, or <c>Standard output:</c>. MTP blocks
    /// are not blank-line separated, so without this bound the last failure in a log would swallow
    /// the following result and footer lines into its error text.
    /// </summary>
    private static readonly Regex MtpBlockTerminatorRegex = new(
        @"^\s*(?:(?i:failed|passed|skipped|errored|error|canceled|cancelled|timed\s+out)\s+[A-Za-z_][\w.]*\.[A-Za-z_]\w*|Test run summary:|Test run completed|Exit code:|In process file artifacts|Standard output:|Standard error:)",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches MTP's per-assembly result footer, e.g.
    /// <c>{path}.dll (net10.0|x64) failed with 3 error(s) (4m 54s 690ms)</c> or
    /// <c>{path}.dll (net10.0|x64) passed (4m 56s 611ms)</c>. This line always closes the final
    /// failure block, and it matches neither <see cref="MtpBlockTerminatorRegex"/> (the result word
    /// is preceded by the assembly name) nor the failure headers, so it is recognised separately.
    /// The <c>from {dll} ({tfm}|{arch})</c> location line has no result word after the parentheses
    /// and therefore never matches.
    /// </summary>
    private static readonly Regex MtpAssemblyResultRegex = new(
        @"^\s*\S+\.dll\s+\([^)]*\)\s+(?i:passed|failed|skipped|errored|completed)\b",
        RegexOptions.Compiled);

    private readonly CiFailureRetention _retention;
    private readonly int _maxLineChars;
    private readonly int _maxBlockChars;
    private readonly bool _redactLines;

    /// <summary>Retained prefix of the line currently being assembled.</summary>
    private readonly StringBuilder _line = new();

    /// <summary>Decoded characters currently held in <see cref="_line"/>.</summary>
    private int _lineChars;

    /// <summary>
    /// Decoded characters of the whole in-progress line, including the dropped ones. Wide on purpose:
    /// a newline-free stream can supply more than <see cref="int.MaxValue"/> decoded characters, and a
    /// narrow counter would wrap negative, skip the final-line flush, and mis-state the omission size.
    /// </summary>
    private long _lineTotalChars;

    /// <summary>
    /// Bounded ring over the LAST <see cref="FallbackTailChars"/> decoded characters the line carried
    /// past <see cref="_maxLineChars"/>. It exists only so the no-failure fallback excerpt can show the
    /// true end of an oversized line; it never grows beyond <see cref="FallbackTailChars"/>.
    /// </summary>
    private readonly StringBuilder _lineTail = new();

    /// <summary>
    /// Whether the in-progress line has carried a value-bounded secret trigger (<c>Bearer </c> or
    /// <c>Password=</c>) anywhere, including inside the part dropped by the line cap. When set, the
    /// line's retained tail cannot be vouched for (its start may fall inside a secret value) and is
    /// not shown in the fallback excerpt.
    /// </summary>
    private bool _lineTainted;

    /// <summary>The last few characters of the previous fragment, so a trigger split across fragments is still seen.</summary>
    private string _triggerCarry = string.Empty;

    /// <summary>
    /// Position of <see cref="_envScan"/> in the grammar of a supported secret environment-variable assignment
    /// (<see cref="EnvScanState"/>). It is advanced over the WHOLE line as it streams — independent of where
    /// the line cap or a stream-chunk boundary falls — so the decision is the same wherever the cut is.
    /// </summary>
    private EnvScanState _envScan;

    /// <summary>Characters of the fixed timestamp template (<see cref="EnvTimestampTemplate"/>) matched so far.</summary>
    private int _envTemplateIndex;

    /// <summary>Whether TOKEN/KEY/SECRET/PASSWORD has appeared inside the active key run.</summary>
    private bool _envKeywordSeen;

    /// <summary>Last <see cref="EnvKeywordCarryChars"/> characters of the key run, so a keyword split across fragments is seen.</summary>
    private string _envCarry = string.Empty;

    /// <summary>
    /// Absolute position (decoded characters from the line start) just past the <c>=</c> of a supported
    /// assignment, or -1 while none has been seen. The line is only a secret assignment when at least one
    /// value character follows it (<c>.+</c> in the sanitizer's pattern), so the decision is taken in
    /// <see cref="FinishLine"/> once the line length is known.
    /// </summary>
    private long _envAssignmentEnd = -1;

    /// <summary>Lines of the failure block currently open, in log order.</summary>
    private readonly List<string> _blockLines = [];

    private FailureBlockKind _blockKind;
    private string _blockTestName = string.Empty;
    private int _blockChars;

    /// <summary>Decoded characters (newline-inclusive) of block lines dropped by the block cap.</summary>
    private long _blockOmittedChars;

    /// <summary>
    /// Explicit truncation state of the open block: set at the FIRST line the block cap rejects, after
    /// which every further line is omitted too. The retained lines are therefore always a strict prefix
    /// of the block — a shorter line can never be admitted after a longer one was rejected.
    /// </summary>
    private bool _blockTruncated;

    /// <summary>Redacted head of the log, kept for the no-failure fallback excerpt.</summary>
    private readonly StringBuilder _head = new();

    /// <summary>Redacted tail of the log, kept for the no-failure fallback excerpt.</summary>
    private readonly StringBuilder _tail = new();

    /// <summary>
    /// Every character fed into the fallback excerpt stream (i.e. after redaction), so the head/tail
    /// gap arithmetic measures the text actually offered to the excerpt.
    /// </summary>
    private long _excerptChars;

    /// <summary>Set once a skipped region follows the head text: later text is not contiguous with the head.</summary>
    private bool _headClosed;

    private int _parsedFailureCount;

    /// <summary>Creates a parser.</summary>
    /// <param name="retention">The shared bounded retention budget for parsed failures.</param>
    /// <param name="maxLineChars">Maximum decoded characters retained per log line.</param>
    /// <param name="maxBlockChars">Maximum decoded characters retained per failure block.</param>
    /// <param name="redactLines">
    /// Whether each complete line is redacted before it is parsed or retained. The streaming job-log
    /// path always redacts; the whole-log convenience path parses text the caller already owns.
    /// </param>
    internal CiLogStreamParser(CiFailureRetention retention, int maxLineChars, int maxBlockChars, bool redactLines)
    {
        _retention = retention;
        _maxLineChars = maxLineChars;
        _maxBlockChars = maxBlockChars;
        _redactLines = redactLines;
    }

    /// <summary>
    /// Creates the production streaming parser: bounded line/block retention and per-line secret
    /// redaction.
    /// </summary>
    /// <param name="retention">The shared bounded retention budget for parsed failures.</param>
    internal static CiLogStreamParser CreateProduction(CiFailureRetention retention) =>
        new(retention, MaxLogLineChars, MaxFailureBlockChars, redactLines: true);

    /// <summary>
    /// Creates a parser for text the caller already holds in memory: unbounded line/block retention
    /// (the input is already bounded by being in memory) and no redaction (the caller owns the text).
    /// </summary>
    /// <param name="retention">The retention budget for parsed failures.</param>
    internal static CiLogStreamParser CreateWholeLog(CiFailureRetention retention) =>
        new(retention, int.MaxValue, int.MaxValue, redactLines: false);

    /// <summary>Number of parsed test-failure occurrences seen, retained or omitted.</summary>
    internal int ParsedFailureCount => _parsedFailureCount;

    /// <summary>
    /// Bounded sanitized head+tail excerpt of the whole log, or <c>null</c> when the log carried no
    /// characters at all. Only used when no failure parses.
    /// </summary>
    internal string? FallbackDiagnostics { get; private set; }

    /// <summary>
    /// Streams a job log to completion in <see cref="StreamChunkBytes"/> chunks, extracting failures
    /// and the bounded fallback excerpt on the way.
    /// </summary>
    /// <param name="stream">The log body stream; read to its end, whatever its length.</param>
    /// <param name="retention">The shared bounded retention budget for parsed failures.</param>
    /// <param name="ct">Cancellation token; cancellation propagates to the caller.</param>
    /// <returns>The completed parser, carrying the parse counters and the fallback excerpt.</returns>
    internal static async Task<CiLogStreamParser> ScanAsync(
        Stream stream, CiFailureRetention retention, CancellationToken ct)
    {
        var parser = CreateProduction(retention);
        var decoder = LenientUtf8.GetDecoder();

        // One byte buffer and one character buffer for the whole scan: the working memory does not
        // grow with the log size.
        var bytes = new byte[StreamChunkBytes];
        var chars = new char[StreamChunkBytes + 16];

        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            var produced = decoder.GetChars(bytes.AsSpan(0, read), chars.AsSpan(), flush: false);
            parser.Feed(chars.AsSpan(0, produced));
        }

        // Flush the decoder: an incomplete multi-byte sequence at the very end of the log becomes a
        // single replacement character (never a throw).
        var flushed = decoder.GetChars(ReadOnlySpan<byte>.Empty, chars.AsSpan(), flush: true);
        if (flushed > 0)
            parser.Feed(chars.AsSpan(0, flushed));

        parser.Complete();
        return parser;
    }

    /// <summary>
    /// Feeds decoded text into the parser. Call <see cref="Complete"/> when no more text follows.
    /// </summary>
    /// <param name="text">Decoded log text; may end mid-line.</param>
    internal void Feed(string text) => Feed(text.AsSpan());

    /// <summary>
    /// Feeds decoded text into the parser. Call <see cref="Complete"/> when no more text follows.
    /// </summary>
    /// <param name="text">Decoded log text; may end mid-line.</param>
    internal void Feed(ReadOnlySpan<char> text)
    {
        while (!text.IsEmpty)
        {
            var newline = text.IndexOf('\n');
            if (newline < 0)
            {
                AppendLineFragment(text);
                return;
            }

            AppendLineFragment(text[..newline]);
            FinishLine(terminated: true);
            text = text[(newline + 1)..];
        }
    }

    /// <summary>
    /// Flushes the final (newline-less) line, closes the last open block, and builds the fallback
    /// excerpt. Idempotent.
    /// </summary>
    internal void Complete()
    {
        if (_lineTotalChars > 0)
            FinishLine(terminated: false);

        FinalizeBlock();
        FallbackDiagnostics = BuildFallbackExcerpt();
    }

    /// <summary>
    /// Appends a fragment of the in-progress line. Characters past the line cap are dropped from the
    /// line used for failure extraction, but the last <see cref="FallbackTailChars"/> of them are kept
    /// in a bounded ring for the fallback excerpt.
    /// </summary>
    private void AppendLineFragment(ReadOnlySpan<char> fragment)
    {
        _lineTotalChars += fragment.Length;

        if (_redactLines && !_lineTainted)
            ScanForValueTriggers(fragment);

        if (_redactLines)
            ScanSecretEnvAssignment(fragment, _lineTotalChars - fragment.Length);

        var room = _maxLineChars - _lineChars;
        var take = room <= 0 ? 0 : Math.Min(room, fragment.Length);
        if (take > 0)
        {
            _line.Append(fragment[..take]);
            _lineChars += take;
        }

        if (take < fragment.Length)
            AppendLineTail(fragment[take..]);
    }

    /// <summary>Keeps only the last <see cref="FallbackTailChars"/> characters dropped by the line cap.</summary>
    private void AppendLineTail(ReadOnlySpan<char> dropped)
    {
        if (dropped.Length >= FallbackTailChars)
        {
            _lineTail.Clear();
            _lineTail.Append(dropped[^FallbackTailChars..]);
            return;
        }

        _lineTail.Append(dropped);
        if (_lineTail.Length > FallbackTailChars)
            _lineTail.Remove(0, _lineTail.Length - FallbackTailChars);
    }

    /// <summary>The longest value-bounded trigger (<c>Password=</c>) is 9 characters; 8 carried characters suffice.</summary>
    private const int TriggerCarryChars = 8;

    /// <summary>
    /// Flags the line when a value-bounded secret trigger (<c>Bearer </c> / <c>Password=</c>) passes
    /// through it, including across a fragment boundary. Cheap: one vectorised search per fragment.
    /// </summary>
    private void ScanForValueTriggers(ReadOnlySpan<char> fragment)
    {
        if (ContainsValueTrigger(fragment))
        {
            _lineTainted = true;
            return;
        }

        // A trigger can straddle two fragments: look at the carried tail joined to this fragment's head.
        if (_triggerCarry.Length > 0)
        {
            var head = fragment[..Math.Min(fragment.Length, TriggerCarryChars)];
            Span<char> joined = stackalloc char[_triggerCarry.Length + head.Length];
            _triggerCarry.AsSpan().CopyTo(joined);
            head.CopyTo(joined[_triggerCarry.Length..]);
            if (ContainsValueTrigger(joined))
            {
                _lineTainted = true;
                return;
            }
        }

        _triggerCarry = fragment.Length >= TriggerCarryChars
            ? new string(fragment[^TriggerCarryChars..])
            : string.Concat(_triggerCarry, new string(fragment)) is { Length: > TriggerCarryChars } joinedText
                ? joinedText[^TriggerCarryChars..]
                : string.Concat(_triggerCarry, new string(fragment));
    }

    /// <summary>The longest secret-env keyword is <c>PASSWORD</c> (8): 7 carried characters complete any split keyword.</summary>
    private const int EnvKeywordCarryChars = 7;

    private static readonly string[] EnvKeywords = ["TOKEN", "KEY", "SECRET", "PASSWORD"];

    /// <summary>The characters a secret-env key can consist of (the pattern's <c>[A-Z_]</c>).</summary>
    private static readonly System.Buffers.SearchValues<char> EnvKeyChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZ_");

    /// <summary>The separator characters the sanitizer accepts after a timestamp: its <c>[ \t]+</c>.</summary>
    private static readonly System.Buffers.SearchValues<char> EnvSeparatorChars =
        System.Buffers.SearchValues.Create(" \t");

    /// <summary>
    /// The fixed part of the Actions timestamp the sanitizer accepts, <c>yyyy-MM-ddTHH:mm:ss</c>, where
    /// <c>D</c> is a decimal digit (the pattern's <c>\d</c>) and every other character is literal. The
    /// optional fraction (<c>.\d+</c>) and the <c>Z</c> follow it.
    /// </summary>
    private const string EnvTimestampTemplate = "DDDD-DD-DDTDD:DD:DD";

    /// <summary>Where the incremental secret-env grammar scan currently stands.</summary>
    private enum EnvScanState : byte
    {
        /// <summary>Nothing consumed: the first character decides between a timestamp and a bare key.</summary>
        Start,
        /// <summary>Inside the fixed timestamp template.</summary>
        Template,
        /// <summary>The template is complete: <c>.</c> (fraction) or <c>Z</c> must follow.</summary>
        AfterSeconds,
        /// <summary>A <c>.</c> was read: at least one fraction digit must follow.</summary>
        FractionFirst,
        /// <summary>Inside the fraction digits: more digits or <c>Z</c>.</summary>
        FractionDigits,
        /// <summary><c>Z</c> was read: at least one space or tab must follow.</summary>
        SeparatorFirst,
        /// <summary>Inside the separator: more spaces/tabs, then the key.</summary>
        SeparatorMore,
        /// <summary>Inside the <c>[A-Z_]</c> key run, waiting for the <c>=</c> that ends it.</summary>
        Key,
        /// <summary>A supported assignment prefix (<c>[timestamp]KEY=</c>) was recognised.</summary>
        Done,
        /// <summary>The line cannot be a supported secret assignment.</summary>
        Failed,
    }

    private static bool ContainsEnvKeyword(ReadOnlySpan<char> text)
    {
        foreach (var keyword in EnvKeywords)
        {
            if (text.IndexOf(keyword, StringComparison.Ordinal) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Advances, with bounded state, the recogniser of the FULL-LINE shape
    /// <see cref="CiLogRedactor.Redact"/> replaces as a secret environment-variable assignment:
    /// <c>^[Actions timestamp + [ \t]+]KEY=value</c>, where KEY is an unbroken <c>[A-Z_]</c> run containing
    /// TOKEN, KEY, SECRET or PASSWORD and the value is non-empty. Its result decides whether the fallback tail
    /// of an oversized line may be shown: the tail starts inside the dropped part and a secret value can
    /// begin anywhere before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan mirrors the sanitizer exactly and is fed EVERY fragment of the line (not only the part past the
    /// cap), so timestamp, separator, key and <c>=</c> are all tracked across the cap and across stream-chunk
    /// boundaries alike: the only state is a few enum/boolean fields, a position in a 19-character template, and
    /// a 7-character keyword carry. Digits are Unicode decimal digits, as the pattern's <c>\d</c> is.
    /// </para>
    /// <para>
    /// A shape the sanitizer does NOT accept never taints the line, so the tail of such a line is still shown:
    /// leading whitespace before an untimestamped key, a timestamp with no separator, any separator other than
    /// space/tab, a key without a keyword (or broken by a space or lowercase letter), and an assignment whose
    /// value is empty.
    /// </para>
    /// </remarks>
    /// <param name="fragment">The next characters of the line, before the line cap is applied.</param>
    /// <param name="fragmentStart">Absolute position of <paramref name="fragment"/> within the line.</param>
    private void ScanSecretEnvAssignment(ReadOnlySpan<char> fragment, long fragmentStart)
    {
        var i = 0;
        while (i < fragment.Length)
        {
            switch (_envScan)
            {
                case EnvScanState.Start:
                    if (IsEnvKeyChar(fragment[i]))
                    {
                        _envScan = EnvScanState.Key;
                    }
                    else if (char.IsDigit(fragment[i]))
                    {
                        _envScan = EnvScanState.Template;
                        _envTemplateIndex = 0;
                    }
                    else
                    {
                        _envScan = EnvScanState.Failed;
                    }

                    break;

                case EnvScanState.Template:
                {
                    var expected = EnvTimestampTemplate[_envTemplateIndex];
                    var c = fragment[i];
                    if (expected == 'D' ? !char.IsDigit(c) : c != expected)
                    {
                        _envScan = EnvScanState.Failed;
                        break;
                    }

                    i++;
                    if (++_envTemplateIndex == EnvTimestampTemplate.Length)
                        _envScan = EnvScanState.AfterSeconds;
                    break;
                }

                case EnvScanState.AfterSeconds:
                    _envScan = fragment[i] == '.' ? EnvScanState.FractionFirst
                        : fragment[i] == 'Z' ? EnvScanState.SeparatorFirst
                        : EnvScanState.Failed;
                    i++;
                    break;

                case EnvScanState.FractionFirst:
                    _envScan = char.IsDigit(fragment[i]) ? EnvScanState.FractionDigits : EnvScanState.Failed;
                    i++;
                    break;

                case EnvScanState.FractionDigits:
                    if (char.IsDigit(fragment[i]))
                        _envScan = EnvScanState.FractionDigits;
                    else
                        _envScan = fragment[i] == 'Z' ? EnvScanState.SeparatorFirst : EnvScanState.Failed;
                    i++;
                    break;

                case EnvScanState.SeparatorFirst:
                    _envScan = fragment[i] is ' ' or '\t' ? EnvScanState.SeparatorMore : EnvScanState.Failed;
                    i++;
                    break;

                case EnvScanState.SeparatorMore:
                {
                    var run = fragment[i..].IndexOfAnyExcept(EnvSeparatorChars);
                    if (run < 0)
                    {
                        i = fragment.Length; // the separator continues into the next fragment
                        break;
                    }

                    i += run;
                    _envScan = IsEnvKeyChar(fragment[i]) ? EnvScanState.Key : EnvScanState.Failed;
                    break;
                }

                case EnvScanState.Key:
                {
                    var rest = fragment[i..];
                    var run = rest.IndexOfAnyExcept(EnvKeyChars);
                    var keyPart = run < 0 ? rest : rest[..run];

                    if (!_envKeywordSeen)
                    {
                        var joined = string.Concat(_envCarry, new string(keyPart));
                        _envKeywordSeen = ContainsEnvKeyword(joined);
                        _envCarry = joined.Length > EnvKeywordCarryChars ? joined[^EnvKeywordCarryChars..] : joined;
                    }

                    if (run < 0)
                    {
                        i = fragment.Length; // the key run continues into the next fragment
                        break;
                    }

                    i += run;
                    if (fragment[i] == '=' && _envKeywordSeen)
                    {
                        _envAssignmentEnd = fragmentStart + i + 1;
                        _envScan = EnvScanState.Done;
                    }
                    else
                    {
                        _envScan = EnvScanState.Failed;
                    }

                    break;
                }

                default: // Done or Failed: nothing more can change the decision
                    return;
            }
        }
    }

    private static bool IsEnvKeyChar(char c) => c is (>= 'A' and <= 'Z') or '_';

    private static bool ContainsValueTrigger(ReadOnlySpan<char> text) =>
        text.IndexOf("Bearer ", StringComparison.Ordinal) >= 0
        || text.IndexOf("Password=", StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Completes one log line: redacts it, applies the truncation marker when it exceeded
    /// <see cref="_maxLineChars"/>, collects the bounded head/tail excerpt, and dispatches it to the
    /// failure state machine.
    /// </summary>
    private void FinishLine(bool terminated)
    {
        var lineTotalChars = _lineTotalChars;
        var raw = _line.ToString();
        var lineTail = _lineTail.ToString();
        // A supported assignment needs a non-empty value after its '=': the line is a secret assignment only
        // if it continued past the delimiter.
        var tainted = _lineTainted || (_envAssignmentEnd >= 0 && lineTotalChars > _envAssignmentEnd);
        _line.Clear();
        _lineTail.Clear();
        _lineChars = 0;
        _lineTotalChars = 0;
        _lineTainted = false;
        _triggerCarry = string.Empty;
        _envScan = EnvScanState.Start;
        _envTemplateIndex = 0;
        _envKeywordSeen = false;
        _envCarry = string.Empty;
        _envAssignmentEnd = -1;

        // Redact BEFORE truncating: the whole retained prefix is visible to the redactor, so a
        // token that straddles the truncation boundary is replaced instead of being cut.
        var text = _redactLines ? CiLogRedactor.RedactCompleteLine(raw) : raw;

        var omittedChars = lineTotalChars - raw.Length;
        if (omittedChars > 0)
        {
            text += TruncationMarker(omittedChars);
            AppendOversizedLineToExcerpt(raw, lineTail, omittedChars, tainted);
        }
        else
        {
            AppendFallbackDiagnostics(text);
        }

        // The excerpt is fed from the line's own head and from the bounded tail ring, NOT from the
        // capped failure-extraction line: the fallback windows are bounded only by FallbackHeadChars /
        // FallbackTailChars, so the end of an oversized line reaches the excerpt even though the line
        // cap dropped it from failure extraction.
        if (terminated)
            AppendFallbackDiagnostics("\n");

        DispatchLine(text);
    }

    /// <summary>
    /// Feeds a line the line cap truncated into the fallback excerpt: its redacted capped prefix, then an
    /// explicit skip of the characters in between, then — when it can be shown safely — the redacted last
    /// <see cref="FallbackTailChars"/> characters of the line.
    /// </summary>
    /// <param name="raw">The retained (capped) raw prefix of the line.</param>
    /// <param name="lineTail">The bounded ring of the line's last dropped characters.</param>
    /// <param name="droppedChars">How many decoded characters the line cap dropped.</param>
    /// <param name="tainted">Whether a value-bounded secret trigger passed through the line.</param>
    private void AppendOversizedLineToExcerpt(string raw, string lineTail, long droppedChars, bool tainted)
    {
        // The ring holds the WHOLE dropped part: the line is complete, redact it as one line.
        if (droppedChars <= lineTail.Length)
        {
            var whole = raw + lineTail;
            AppendFallbackDiagnostics(_redactLines ? CiLogRedactor.RedactCompleteLine(whole) : whole);
            return;
        }

        AppendFallbackDiagnostics(_redactLines ? CiLogRedactor.RedactCompleteLine(raw) : raw);

        // The tail starts in the middle of the line, possibly inside a secret. Without the context of
        // what precedes it, show it only when that cannot leak:
        //  * a value-bounded secret (Bearer / Password= / a secret environment variable) may have run
        //    into it from the unseen middle, so such a line shows no tail at all. `tainted` is set while
        //    streaming, including for an environment-variable assignment whose timestamp, separator, keyword
        //    or '=' was cut by, or lies entirely beyond, the line cap (see ScanSecretEnvAssignment);
        //  * a fixed-length GitHub token (40 characters, 36 after its prefix) may have been cut at its
        //    start, so the leading token-character run is dropped.
        if (_redactLines && tainted)
        {
            AppendFallbackSkip(droppedChars);
            return;
        }

        if (!_redactLines)
        {
            AppendFallbackSkip(droppedChars - lineTail.Length);
            AppendFallbackDiagnostics(lineTail);
            return;
        }

        // Redact the WHOLE ring first and trim afterwards. Trimming the raw tail could cut through a
        // COMPLETE token that begins inside the leading run, leaving its secret suffix without a trigger
        // for the redactor. Redacting first replaces every complete token in the ring with the redaction
        // marker, and the leading run then stops at the marker's '*', so the trim can only ever remove
        // characters that precede any complete token — i.e. the fragment of a token cut at the tail's
        // start, whose trigger is in the dropped part. The skipped region is accounted as everything the
        // ring did not show, in the same redacted-excerpt character units as the rest of the excerpt.
        var redactedTail = CiLogRedactor.RedactCompleteLine(lineTail);
        var trimmed = CiLogRedactor.LeadingTokenRunLength(redactedTail, 40);
        AppendFallbackSkip(droppedChars - lineTail.Length + trimmed);
        AppendFallbackDiagnostics(redactedTail[trimmed..]);
    }

    /// <summary>Advances the failure state machine by one redacted log line.</summary>
    private void DispatchLine(string redactedLine)
    {
        var line = StripActionsTimestamp(redactedLine);

        // MTP is checked first, exactly as the whole-log parser does.
        if (TryMatchMtpFailureHeader(line, out var mtpName))
        {
            FinalizeBlock();
            StartBlock(FailureBlockKind.Mtp, mtpName);
            return;
        }

        if (TryMatchFailureHeader(line, out var vstestName))
        {
            FinalizeBlock();
            StartBlock(FailureBlockKind.Vstest, vstestName);
            return;
        }

        if (_blockKind == FailureBlockKind.None)
            return;

        // MTP blocks are bounded by their next result line or run footer; VSTest blocks run until
        // the next failure header or the end of the log.
        if (_blockKind == FailureBlockKind.Mtp
            && (MtpBlockTerminatorRegex.IsMatch(line) || MtpAssemblyResultRegex.IsMatch(line)))
        {
            FinalizeBlock();
            return;
        }

        AppendBlockLine(line);
    }

    private void StartBlock(FailureBlockKind kind, string testName)
    {
        _blockKind = kind;
        _blockTestName = testName;
        _blockLines.Clear();
        _blockChars = 0;
        _blockOmittedChars = 0;
        _blockTruncated = false;
    }

    private void AppendBlockLine(string line)
    {
        // Newline-inclusive cost: the omitted-character figure reported by the truncation marker
        // counts the line separators of the dropped text too.
        var cost = line.Length + 1;

        // Once ANY line has been rejected the block is truncated for good: every later line is omitted,
        // however short. Admitting a short line (a blank terminator, say) after rejecting longer ones
        // would retain a non-contiguous block and hide the truncation.
        if (_blockTruncated)
        {
            _blockOmittedChars += cost;
            return;
        }

        var used = (long)_blockChars + cost;
        if (used > _maxBlockChars)
        {
            _blockTruncated = true;
            _blockOmittedChars += cost;
            return;
        }

        _blockLines.Add(line);
        _blockChars = (int)used;
    }

    /// <summary>Extracts and retains the open block, if it is a parseable failure.</summary>
    private void FinalizeBlock()
    {
        if (_blockKind == FailureBlockKind.None)
            return;

        var kind = _blockKind;
        var testName = _blockTestName;
        var omittedChars = _blockOmittedChars;

        _blockKind = FailureBlockKind.None;
        _blockTestName = string.Empty;
        _blockOmittedChars = 0;
        _blockTruncated = false;
        _blockChars = 0;

        if (kind == FailureBlockKind.Mtp)
            TryExtractMtpFailure(testName, omittedChars);
        else
            TryExtractVstestFailure(testName, omittedChars);

        _blockLines.Clear();
    }

    /// <summary>
    /// Extracts one VSTest/xUnit failure block: the <c>Error Message:</c> section runs until the
    /// <c>Stack Trace:</c> header (or the end of the block), and the stack trace runs until an
    /// end-of-stack-trace marker, a blank line, or the end of the block.
    /// </summary>
    private bool TryExtractVstestFailure(string testName, long omittedChars)
    {
        var lines = _blockLines;
        var errorHeaderIndex = -1;
        var stackHeaderIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (errorHeaderIndex < 0 && XUnitErrorMessageRegex.IsMatch(lines[i]))
            {
                errorHeaderIndex = i;
                continue;
            }

            if (stackHeaderIndex < 0 && XUnitStackTraceRegex.IsMatch(lines[i]))
            {
                stackHeaderIndex = i;
                break; // the stack trace is the last section of the failure block
            }
        }

        if (errorHeaderIndex < 0)
            return false; // header without a message section is not a parseable failure

        var errorEnd = stackHeaderIndex >= 0 ? stackHeaderIndex : lines.Count;
        var error = SliceLines(lines, errorHeaderIndex + 1, errorEnd);

        var stackTrace = string.Empty;
        if (stackHeaderIndex >= 0)
        {
            var stackEnd = lines.Count;
            for (var i = stackHeaderIndex + 1; i < lines.Count; i++)
            {
                if (StackTraceEndMarkerRegex.IsMatch(lines[i]) || string.IsNullOrWhiteSpace(lines[i]))
                {
                    stackEnd = i;
                    break;
                }
            }

            stackTrace = SliceLines(lines, stackHeaderIndex + 1, stackEnd);

            // The omitted lines all FOLLOW the retained ones (the accumulator keeps a strict prefix), so
            // they cut the stack trace short exactly when no terminator closed it among the retained
            // lines. A stack trace that a blank line or end-of-stack-trace marker closed inside the
            // retained prefix is complete as parsed; the omitted lines are what followed it.
            Emit(testName, error, stackTrace, stackEnd < lines.Count ? 0 : omittedChars);
            return true;
        }

        Emit(testName, error, stackTrace, omittedChars);
        return true;
    }

    /// <summary>
    /// Extracts one MTP failure block: the trailing run of bare <c>at …</c> frames is the stack
    /// trace and everything before it (after the optional <c>from {dll} (…)</c> line) is the error.
    /// </summary>
    private bool TryExtractMtpFailure(string testName, long omittedChars)
    {
        var lines = _blockLines;

        // Trailing blank/whitespace-only lines must not stop the backwards stack-frame scan.
        var bodyEnd = lines.Count;
        while (bodyEnd > 0 && string.IsNullOrWhiteSpace(lines[bodyEnd - 1]))
            bodyEnd--;

        // Skip MTP's indented "from <dll> (<tfm>|<arch>)" location line when present.
        var bodyStart = 0;
        if (bodyStart < bodyEnd && MtpFromLineRegex.IsMatch(lines[bodyStart]))
            bodyStart++;

        var stackStart = bodyEnd;
        for (var i = bodyEnd - 1; i >= bodyStart; i--)
        {
            if (MtpStackTraceLineRegex.IsMatch(lines[i]))
                stackStart = i;
            else
                break;
        }

        var error = SliceLines(lines, bodyStart, stackStart);
        var stackTrace = SliceLines(lines, stackStart, bodyEnd);

        if (error.Length == 0 && stackTrace.Length == 0)
            return false; // header followed by nothing parseable

        // An MTP block has no terminator of its own among its lines (the next result line or footer
        // closes the block and is not part of it), so every omitted line belongs to the body: the
        // block is truncated whenever any line was omitted, regardless of how many retained trailing
        // blank lines the body trimming discards.
        Emit(testName, error, stackTrace, omittedChars);
        return true;
    }

    /// <summary>Joins and trims a line range; an empty range yields an empty string.</summary>
    private static string SliceLines(List<string> lines, int start, int end) =>
        end > start
            ? string.Join("\n", lines.GetRange(start, end - start)).Trim()
            : string.Empty;

    /// <summary>Records one parsed failure occurrence and offers it to the bounded retention.</summary>
    private void Emit(string testName, string error, string stackTrace, long omittedBlockChars)
    {
        _parsedFailureCount++;

        if (omittedBlockChars > 0)
        {
            var marker = TruncationMarker(omittedBlockChars);
            if (stackTrace.Length > 0)
                stackTrace += marker;
            else
                error += marker;
        }

        _retention.TryRetain(testName, error, stackTrace);
    }

    /// <summary>
    /// Records that <paramref name="skippedChars"/> decoded characters of the stream are NOT shown (an
    /// oversized line's middle). The head window is closed — what follows is not contiguous with it —
    /// and the tail window restarts, so the gap arithmetic stays exact in decoded characters.
    /// </summary>
    private void AppendFallbackSkip(long skippedChars)
    {
        if (skippedChars <= 0)
            return;

        _excerptChars += skippedChars;
        _headClosed = true;
        _tail.Clear();
    }

    /// <summary>Collects the bounded head/tail excerpt of redacted log text.</summary>
    private void AppendFallbackDiagnostics(string text)
    {
        if (text.Length == 0)
            return;

        _excerptChars += text.Length;

        if (!_headClosed && _head.Length < FallbackHeadChars)
        {
            var room = FallbackHeadChars - _head.Length;
            _head.Append(text.Length <= room ? text : text[..room]);
        }

        _tail.Append(text);
        if (_tail.Length > FallbackTailChars)
            _tail.Remove(0, _tail.Length - FallbackTailChars);
    }

    /// <summary>
    /// Builds the head+tail fallback excerpt with explicit truncation markers, or <c>null</c> when
    /// the log carried no characters at all. Sizes count DECODED characters of the redacted text
    /// (raw byte counts are never synthesized).
    /// </summary>
    private string? BuildFallbackExcerpt()
    {
        if (_excerptChars == 0)
            return null;

        var head = _head.ToString();

        // The whole log fits in the head window: no truncation, no marker.
        if (_excerptChars <= head.Length)
            return head;

        var tail = _tail.ToString();

        // The tail window starts inside or before the head window. When it starts inside it, the two
        // windows already overlap and together cover the whole log: emit the head plus only the part
        // of the tail the head does not already carry.
        var tailStart = _excerptChars - tail.Length;
        if (tailStart <= head.Length)
            return string.Concat(head, tail[(int)(head.Length - tailStart)..]);

        var omittedChars = tailStart - head.Length;
        return string.Concat(head, "\n", GapTruncationMarker(omittedChars), "\n", tail);
    }

    /// <summary>Renders the truncation marker for a capped line, counting DECODED characters.</summary>
    private static string TruncationMarker(long omittedChars) =>
        FormattableString.Invariant($" …[truncated: {omittedChars:N0} characters omitted]…");

    /// <summary>Renders the truncation marker for the gap between the fallback head and tail.</summary>
    private static string GapTruncationMarker(long omittedChars) =>
        FormattableString.Invariant($"…[truncated: {omittedChars:N0} characters omitted between the head and the tail of the log]…");

    /// <summary>Removes a GitHub Actions ISO-8601 timestamp prefix from a single log line.</summary>
    private static string StripActionsTimestamp(string line) =>
        GitHubActionsTimestampRegex.Replace(line, string.Empty);

    /// <summary>
    /// Matches an xUnit failure header line and validates that the captured test name is a
    /// dotted <c>Class.Method</c> identifier. This rejects prose lines such as
    /// <c>Failed to clone repository [attempt 1]</c> and <c>Failed: 3</c> that would otherwise
    /// satisfy the header shape.
    /// </summary>
    private static bool TryMatchFailureHeader(string line, out string testName)
    {
        testName = string.Empty;

        var header = XUnitFailedTestRegex.Match(line);
        if (!header.Success)
            return false;

        var candidate = header.Groups[1].Value.Trim();
        if (!candidate.Contains('.', StringComparison.Ordinal))
            return false;

        testName = candidate;
        return true;
    }

    /// <summary>
    /// Matches a Microsoft.Testing.Platform failure header and validates that the captured
    /// identity is a dotted <c>Class.Method</c> identifier (optionally followed by a theory
    /// argument list). The same dotted-name guard as the VSTest header rejects prose such as
    /// <c>failed to restore packages (3s)</c>.
    /// </summary>
    private static bool TryMatchMtpFailureHeader(string line, out string testName)
    {
        testName = string.Empty;

        var header = MtpFailedTestRegex.Match(line);
        if (!header.Success)
            return false;

        var candidate = header.Groups[1].Value.Trim();
        if (!candidate.Contains('.', StringComparison.Ordinal))
            return false;

        testName = candidate;
        return true;
    }

    /// <summary>Which failure shape the open block belongs to.</summary>
    private enum FailureBlockKind
    {
        /// <summary>No block is open.</summary>
        None,
        /// <summary>A VSTest/xUnit block (<c>Failed … [duration]</c>).</summary>
        Vstest,
        /// <summary>A Microsoft.Testing.Platform block (<c>failed … (duration)</c>).</summary>
        Mtp,
    }
}
