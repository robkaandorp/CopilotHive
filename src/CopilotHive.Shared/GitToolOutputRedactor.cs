using System.Text;

namespace CopilotHive.Services;

/// <summary>
/// The single credential-redaction transform for text that is about to become MODEL-VISIBLE Git
/// tool output: raw git stdout/stderr, an echoed git argument list, a validation rejection that
/// quotes caller input, and a classified exception summary.
/// </summary>
/// <remarks>
/// <para>
/// <b>One boundary, not many call sites.</b> The Composer's git tools hand whatever git printed
/// straight to the model — a remote URL git echoed back, a credential helper's stderr, a token a
/// hostile remote embedded in an error message, or the arguments the tool itself passed. Every one
/// of those strings is redacted through this ONE transform so that a future output surface cannot
/// silently forget to redact. Callers must apply it BEFORE truncation, so the truncated text and
/// its reported total line count both describe the already-redacted output.
/// </para>
/// <para>
/// <b>Two span sources, one replacement.</b> Both sources only COLLECT the spans they recognise
/// in the same unmodified text; the union of all spans is then replaced in a single pass. Neither
/// source ever sees the other's output, so an inventory value that happens to coincide with a
/// structural recognition marker (the header name <c>Authorization</c>, the assignment name
/// <c>GITHUB_TOKEN</c>, the prefix <c>ssh://u:</c>) cannot erase the marker and hide the
/// credential that follows it. Overlapping spans merge into one placeholder; spans that merely
/// touch keep their own.
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>Known-secret inventory.</b> Values the Composer itself holds are matched verbatim,
///     together with their one-level percent-encoded and standard-base64 forms. Only values of at
///     least <see cref="MinimumInventoryValueLength"/> characters participate: a one- or
///     two-character "secret" would match ordinary text everywhere and destroy the output for no
///     security benefit. Shorter structural credentials are still removed by the structural source.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Structural patterns</b>, which need no inventory at all: HTTP/HTTPS userinfo and token
///     query parameters (delegated to <see cref="GitUrlRedactor"/> so its exact grammar is neither
///     duplicated nor silently broadened), SSH and scp-style passwords, folded
///     <c>Authorization</c> headers, and token/credential assignments.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Bounded encodings.</b> Encoded matching is deliberately BOUNDED: exactly ONE level of UTF-8
/// percent escapes (hex digits case-insensitive, never recursively decoded) and STANDARD base64
/// only, matching both the padded and the unpadded form of the same encoding. Nothing is
/// re-encoded after decoding, so a doubly-encoded value is not chased.
/// </para>
/// <para>
/// <b>Best effort, never a guarantee.</b> This is a known-secret plus structural-pattern boundary.
/// A credential in a shape no pattern describes and that is not in the inventory survives — which
/// is why it is applied unconditionally rather than only when a specific credential is "selected".
/// </para>
/// <para>
/// <b>Line structure is preserved.</b> No pass inserts or removes a line break (a folded header's
/// indentation and newlines survive its redaction), so callers may truncate the redacted text and
/// report its own line count.
/// </para>
/// </remarks>
public static class GitToolOutputRedactor
{
    /// <summary>The placeholder substituted for every redacted credential.</summary>
    public const string Placeholder = "[redacted]";

    /// <summary>
    /// The minimum inventory value length that is eligible for exact replacement. Shorter values
    /// are NOT exact-replaced — they are far too likely to occur in ordinary text.
    /// </summary>
    public const int MinimumInventoryValueLength = 8;

    /// <summary>
    /// The credential-bearing process environment variables whose CURRENT values belong to the
    /// inventory. Callers read them FRESH per redaction operation through
    /// <see cref="ReadEnvironmentSecrets"/>: a value may change between calls, so a cached copy
    /// could both under- and over-redact.
    /// </summary>
    public static IReadOnlyList<string> InventoryEnvironmentVariableNames { get; } =
        ["GH_TOKEN", "GITHUB_TOKEN", "GITHUB_CONFIG_REPO_TOKEN", "GITHUB_ORCHESTRATOR_TOKEN"];

    /// <summary>The lower-case SSH scheme prefix whose userinfo password is redacted.</summary>
    private const string SshScheme = "ssh://";

    /// <summary>The HTTP header whose credential value is redacted, including its folded form.</summary>
    private const string AuthorizationHeaderName = "Authorization";

    /// <summary>The proxy variant of <see cref="AuthorizationHeaderName"/>, redacted the same way.</summary>
    private const string ProxyAuthorizationHeaderName = "Proxy-Authorization";

    /// <summary>The <c>Basic</c> authorization scheme name.</summary>
    private const string BasicScheme = "Basic";

    /// <summary>The <c>Bearer</c> authorization scheme name.</summary>
    private const string BearerScheme = "Bearer";

    /// <summary>
    /// The assignment names whose value is a credential. Ordered LONGEST FIRST so the most
    /// specific name in the list wins at a given position (<c>GITHUB_CONFIG_REPO_TOKEN</c> before
    /// the bare <c>token</c> inside it). Each name is matched as a whole name and only when it is
    /// followed by an assignment operator, so prose such as "the token is expired" is untouched.
    /// The four environment variable names are listed explicitly; the remaining entries cover
    /// generic git-config style <c>name = value</c> output.
    /// </summary>
    private static readonly string[] AssignmentNames =
    [
        "GITHUB_CONFIG_REPO_TOKEN",
        "GITHUB_ORCHESTRATOR_TOKEN",
        "GITHUB_TOKEN",
        "GH_TOKEN",
        "access_token",
        "refresh_token",
        "client_secret",
        "api_token",
        "authtoken",
        "credentials",
        "credential",
        "api_key",
        "apikey",
        "password",
        "passwd",
        "token",
        "secret",
    ];

    /// <summary>
    /// Reads the CURRENT value of every <see cref="InventoryEnvironmentVariableNames"/> variable.
    /// </summary>
    /// <returns>
    /// One entry per variable, in <see cref="InventoryEnvironmentVariableNames"/> order, holding
    /// that variable's current value or <c>null</c> when it is unset or empty. Callers concatenate
    /// this with any value the operation itself holds and hand the whole list to
    /// <see cref="Redact"/>, which applies the length bound.
    /// </returns>
    public static IReadOnlyList<string?> ReadEnvironmentSecrets()
    {
        var values = new string?[InventoryEnvironmentVariableNames.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var value = Environment.GetEnvironmentVariable(InventoryEnvironmentVariableNames[i]);
            values[i] = string.IsNullOrEmpty(value) ? null : value;
        }

        return values;
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every recognized credential replaced by
    /// <see cref="Placeholder"/>.
    /// </summary>
    /// <param name="text">
    /// The text about to become model-visible output. May be null or empty.
    /// </param>
    /// <param name="knownSecrets">
    /// The inventory values the caller knows about (the operation's own keys and the fresh
    /// environment values). Entries that are null, empty or shorter than
    /// <see cref="MinimumInventoryValueLength"/> are ignored. The collection is only enumerated —
    /// it is never stored, so a later change to a process environment variable cannot be masked by
    /// a cached copy.
    /// </param>
    /// <returns>
    /// The redacted text, or the input unchanged when it carries no recognized credential.
    /// </returns>
    public static string Redact(string? text, IEnumerable<string?>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        // ONE span-union pass over ONE text, so neither layer's output can blind the other.
        //
        // The inventory layer used to run first and hand the structural layer text in which a
        // matched value had already become "[redacted]". When an inventory value coincided with a
        // structural MARKER (the header name "Authorization", the assignment name "GITHUB_TOKEN",
        // the "ssh://u:" prefix) the marker was destroyed and the credential that followed it
        // escaped entirely. Running the layers the other way round is no fix: a structural pass
        // would then redact a fraction of an inventory value and the whole-value match would be
        // lost. Instead every layer only COLLECTS spans against the same unmodified text; the
        // union is replaced once.
        //
        // The composed GitUrlRedactor runs first because it is the one pass that rewrites text
        // rather than reporting spans (it deletes userinfo and token parameters and is not
        // modified here). It must still precede the assignment pass: that pass would otherwise
        // claim `token=abc&ref=topic` inside a URL query and swallow the parameters the URL
        // grammar keeps. It only DELETES credential text, so it cannot manufacture a marker.
        //
        // That deletion is destructive for the INVENTORY, though: a known value that CONTAINS a
        // credentialed URL (`https://u:pw@host/r…`) no longer occurs in the rewritten text, and the
        // rest of it would survive unmatched. So the inventory is matched against the ORIGINAL text
        // as well and those spans are mapped onto the rewritten text (see CollectOriginalTextSpans).
        var current = GitUrlRedactor.Redact(text) ?? string.Empty;

        var spans = new List<(int Start, int End)>();
        var forms = BuildInventoryForms(knownSecrets);
        if (forms.Count > 0)
        {
            spans.AddRange(CollectInventorySpans(current, forms));
            if (!string.Equals(current, text, StringComparison.Ordinal))
                CollectOriginalTextSpans(text, current, forms, spans);
        }

        CollectScpStylePasswordSpans(current, spans);
        CollectSshUrlPasswordSpans(current, spans);
        CollectAuthorizationHeaderSpans(current, spans);
        CollectSecretAssignmentSpans(current, spans);

        if (spans.Count == 0)
            return current;

        // ReplaceSpans merges in a single ascending sweep; touching spans stay distinct.
        spans.Sort(static (left, right) =>
            left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.End.CompareTo(right.End));
        return ReplaceSpans(current, spans);
    }

    // ── Span source 1: the known-secret inventory ─────────────────────────────

    /// <summary>
    /// Builds the matchable forms of every eligible inventory value (raw, one-level percent
    /// escapes, standard padded / unpadded base64). The spans they match are collected by
    /// <see cref="CollectInventorySpans"/> against the original text and merged with the
    /// structural spans, so a secret that CONTAINS another (or an encoded variant that merely
    /// STARTS with another value) is removed whole whatever order the values arrive in.
    /// </summary>
    /// <returns>The forms, or an empty list when there is no eligible value.</returns>
    private static List<InventoryForms> BuildInventoryForms(IEnumerable<string?>? knownSecrets)
    {
        var forms = new List<InventoryForms>();
        if (knownSecrets is null)
            return forms;

        var eligible = new List<string>();
        foreach (var candidate in knownSecrets)
        {
            if (candidate is not { Length: >= MinimumInventoryValueLength })
                continue;
            if (!eligible.Contains(candidate, StringComparer.Ordinal))
                eligible.Add(candidate);
        }

        // ORDER-INDEPENDENT by construction. Every form of every value is matched against the
        // ORIGINAL text and the matched spans are merged, so no pass ever sees another pass's
        // output. Any sequence of per-value replacement passes lets one value's replacement
        // destroy a prefix of a DIFFERENT value's longer (encoded) variant and strand its tail,
        // whatever order it runs in: sorting by raw length, running base64 first, or re-scanning
        // only moves WHICH pair of values collides.
        forms.AddRange(eligible.Select(static secret => new InventoryForms(secret)));
        return forms;
    }

    /// <summary>
    /// The matchable forms of one inventory value: its raw text, its one-level percent-encoded
    /// readings, and its standard padded / unpadded base64.
    /// </summary>
    private sealed class InventoryForms(string secret)
    {
        /// <summary>The raw value (also the form the percent-aware matcher starts from).</summary>
        public string Secret { get; } = secret;

        /// <summary>Whether the percent-aware matcher applies (single-byte characters only).</summary>
        public bool PercentAware { get; } = !secret.Any(static c => c > 0x7F);

        /// <summary>The padded and, when different, unpadded standard-base64 spellings.</summary>
        public IReadOnlyList<string> Base64 { get; } = StandardBase64Forms(secret);

        private CharMembership? _rawChars;
        private CharMembership?[]? _base64Chars;

        /// <summary>Exactly the characters of the raw value (O(length), built once, on first use).</summary>
        public CharMembership RawChars => _rawChars ??= CharMembership.Of(Secret);

        /// <summary>Exactly the characters of the base64 spelling at <paramref name="index"/>.</summary>
        public CharMembership Base64Chars(int index)
        {
            _base64Chars ??= new CharMembership?[Base64.Count];
            return _base64Chars[index] ??= CharMembership.Of(Base64[index]);
        }
    }

    /// <summary>The form of an inventory value that produced a match, and the extent it matched.</summary>
    private readonly record struct InventoryMatch(int Start, int End, InventoryForms Form, int Base64Index, bool Escaped);

    /// <summary>
    /// An EXACT set of characters. "Does this value contain character c?" is a property of the value
    /// itself, decidable in O(length) — never something to approximate under a scan budget.
    /// </summary>
    private sealed class CharMembership
    {
        private readonly bool[] _ascii = new bool[128];
        private HashSet<char>? _other;

        public static CharMembership Of(string text)
        {
            var set = new CharMembership();
            foreach (var c in text)
                set.Add(c);
            return set;
        }

        /// <summary>Exactly the characters of <c>text[start..end)</c> — the concrete text a match covers.</summary>
        public static CharMembership OfSpan(string text, int start, int end)
        {
            var set = new CharMembership();
            for (var i = start; i < end; i++)
                set.Add(text[i]);
            return set;
        }

        public bool Contains(char c) => c < 128 ? _ascii[c] : _other?.Contains(c) == true;

        private void Add(char c)
        {
            if (c < 128)
                _ascii[c] = true;
            else
                (_other ??= []).Add(c);
        }
    }

    /// <summary>
    /// Finds, at EVERY position, the extent of every form of every inventory value that matches
    /// there, in ascending start order.
    /// </summary>
    /// <remarks>
    /// All candidates at a position are kept rather than only the longest: the union taken by
    /// <see cref="ReplaceSpans"/> makes the LONGEST single-layer variant win at each position
    /// regardless of which inventory entry it belongs to. A shorter raw match of one value inside
    /// another value's encoded variant merges into it instead of pre-empting it.
    /// </remarks>
    private static List<(int Start, int End)> CollectInventorySpans(string text, List<InventoryForms> forms) =>
        CollectInventoryMatches(text, forms).Select(static match => (match.Start, match.End)).ToList();

    /// <summary>
    /// The matches behind <see cref="CollectInventorySpans"/>, each with the form that produced it
    /// (so its exact character set can be consulted later), in ascending start order.
    /// </summary>
    private static List<InventoryMatch> CollectInventoryMatches(string text, List<InventoryForms> forms)
    {
        var matches = new List<InventoryMatch>();
        for (var index = 0; index < text.Length; index++)
        {
            foreach (var form in forms)
            {
                if (text.AsSpan(index).StartsWith(form.Secret, StringComparison.Ordinal))
                    matches.Add(new InventoryMatch(index, index + form.Secret.Length, form, -1, false));

                for (var b = 0; b < form.Base64.Count; b++)
                {
                    var encoded = form.Base64[b];
                    if (text.AsSpan(index).StartsWith(encoded, StringComparison.Ordinal))
                        matches.Add(new InventoryMatch(index, index + encoded.Length, form, b, false));
                }

                // Fast path: only a literal first character or a leading escape can start a match.
                if (form.PercentAware
                    && (text[index] == form.Secret[0] || text[index] == '%')
                    && TryMatchPercentEncoded(text, index, form.Secret, out var percentEnd))
                {
                    // A match with no escape in it is the raw value itself and holds exactly its characters.
                    var literal = percentEnd - index == form.Secret.Length
                        && text.AsSpan(index, percentEnd - index).SequenceEqual(form.Secret);
                    matches.Add(new InventoryMatch(index, percentEnd, form, -1, !literal));
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Matches the inventory against the ORIGINAL text and adds the matches, mapped onto the
    /// URL-rewritten text, to <paramref name="spans"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GitUrlRedactor"/> only DELETES characters (userinfo, token parameters, fragments,
    /// dangling delimiters), so the rewritten text is a subsequence of the original and every kept
    /// character has a position in it. A match that lies wholly in deleted text maps to nothing — the
    /// rewrite has already removed it, exactly as before. A match that reaches KEPT characters (a
    /// known value that contains the whole URL, or only its host and path) maps to those characters,
    /// so the union with the other spans removes the value whole instead of leaving a tail.
    /// </para>
    /// <para>
    /// Where mapping cannot decide which of several equal characters the rewrite kept, it fails
    /// TOWARD coverage of the known value — never toward a dropped byte of it — but only where a
    /// matching character really lies inside the known value's extent, so ordinary text elsewhere is
    /// not redacted by ambiguity alone; see <see cref="UrlRewriteAlignment"/>.
    /// </para>
    /// </remarks>
    private static void CollectOriginalTextSpans(
        string original, string rewritten, List<InventoryForms> forms, List<(int Start, int End)> spans)
    {
        var originalMatches = CollectInventoryMatches(original, forms);
        if (originalMatches.Count == 0)
            return;

        // Unreachable for a deletion-only rewrite; if it ever failed the spans collected on the
        // rewritten text above remain the (pre-existing) behaviour.
        if (UrlRewriteAlignment.Create(original, rewritten) is not { } alignment)
            return;

        foreach (var match in originalMatches)
        {
            var chars = match.Escaped ? PercentMatchChars(original, match)
                : match.Base64Index >= 0 ? match.Form.Base64Chars(match.Base64Index)
                : match.Form.RawChars;

            if (alignment.Map(match.Start, match.End, chars) is { } mapped)
                spans.Add(mapped);
        }
    }

    /// <summary>Most reading positions followed while enumerating the readings of one percent match.</summary>
    private const int MaxReadingSteps = 4_000_000;

    /// <summary>
    /// Exactly the characters of the CONCRETE text a percent-form match can cover — not those of every
    /// spelling the value could hypothetically have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A percent spelling of a value written with a literal <c>%</c> is ambiguous: <c>%25</c> may be one
    /// escaped <c>%</c> or a literal <c>%</c> followed by text. Every genuine reading starts at the
    /// same index, so each reading's span is a PREFIX of the longest one, and the union of their
    /// characters is exactly the characters of the longest genuine reading. Membership is therefore
    /// "is the character in <c>original[start..longestEnd)</c>", exact over the readings that can
    /// actually occur. An earlier version used the raw value's characters plus the whole hex alphabet,
    /// which answered "could ANY spelling contain it?" and so licensed coverage of an ordinary host
    /// character (<c>a</c>–<c>f</c>, <c>0</c>–<c>9</c>, …) the real spelling does not contain.
    /// </para>
    /// </remarks>
    private static CharMembership PercentMatchChars(string original, InventoryMatch match) =>
        CharMembership.OfSpan(original, match.Start, LongestPercentReadingEnd(original, match.Start, match.Form.Secret, match.End));

    /// <summary>
    /// The end of the longest genuine one-level percent reading of <paramref name="secret"/> that
    /// starts at <paramref name="start"/>.
    /// </summary>
    /// <param name="text">The text the match lies in.</param>
    /// <param name="start">Where the match starts.</param>
    /// <param name="secret">The inventory value whose readings are enumerated.</param>
    /// <param name="matchedEnd">The end the matcher chose; the answer is never below it.</param>
    /// <remarks>
    /// The readings are followed as a set of reachable positions, one step per character of the value.
    /// A position can only branch at a <c>%</c> of the value that meets <c>%25</c> in the text, so the
    /// set stays tiny for any realistic value. If it ever does not (a pathological value made of
    /// <c>%</c> against a text of <c>%25</c>), the answer is the longest span any reading could have —
    /// the sanctioned conservative direction for a cost-limited enumeration, never used otherwise.
    /// </remarks>
    private static int LongestPercentReadingEnd(string text, int start, string secret, int matchedEnd)
    {
        var current = new List<int> { start };
        var next = new List<int>();
        var steps = 0;

        foreach (var expected in secret)
        {
            next.Clear();
            foreach (var position in current)
            {
                if (++steps > MaxReadingSteps)
                    return Math.Min(text.Length, start + (3 * secret.Length));

                if (position < text.Length && text[position] == expected)
                    next.Add(position + 1);

                if (position + 2 < text.Length
                    && text[position] == '%'
                    && TryParseHexDigit(text[position + 1], out var high)
                    && TryParseHexDigit(text[position + 2], out var low)
                    && (byte)((high << 4) | low) == (byte)expected)
                {
                    next.Add(position + 3);
                }
            }

            if (next.Count > 1)
            {
                next.Sort();
                var write = 1;
                for (var read = 1; read < next.Count; read++)
                {
                    if (next[read] != next[write - 1])
                        next[write++] = next[read];
                }

                next.RemoveRange(write, next.Count - write);
            }

            (current, next) = (next, current);
            if (current.Count == 0)
                return matchedEnd;
        }

        return Math.Max(matchedEnd, current.Max());
    }

    /// <summary>
    /// Places each kept character of <see cref="GitUrlRedactor"/>'s rewritten text at its position in
    /// the original text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain diff is AMBIGUOUS when a deleted character equals an adjacent kept one: userinfo
    /// <c>hunter:pw@</c> in front of host <c>host.invalid</c> deletes either <c>hunter:pw@</c> or
    /// <c>unter:pw@h</c> and prints the same text. Guessing wrong would redact the host's first
    /// character whenever a known value sits in userinfo — the most common real case. So the earliest
    /// and latest greedy alignments bracket every possible placement, and each ambiguous alphanumeric
    /// character is RESOLVED by probing: replace that one character by a fresh sentinel and re-run
    /// the frozen redactor on the URL's own region. If the output is unchanged the character was
    /// deleted (a kept character would have shown the sentinel).
    /// </para>
    /// <para>
    /// Whatever stays ambiguous (a delimiter such as <c>&amp;</c>, a grammar-critical letter, a region
    /// no sentinel can be found for, or a probe budget overrun) FAILS CLOSED: a kept character joins a
    /// known-value span when ANY character that could be it lies inside the span. The earlier rule —
    /// join only when EVERY placement lies inside — dropped the first byte of a known host whenever
    /// the equal character in the deleted userinfo sat outside the known extent, which leaks part of
    /// a KNOWN secret. An engineering fallback is a cost, not a security contract: over-redacting one
    /// ambiguous character is the sanctioned failure, under-redacting a known value is not.
    /// </para>
    /// <para>
    /// The widening is driven by the known-value span, not by ambiguity: a candidate has to be an
    /// actual equal character INSIDE the span. A known value that covers only deleted text (a
    /// password) holds no such character, so the retained host beside it is left alone.
    /// </para>
    /// </remarks>
    private sealed class UrlRewriteAlignment
    {
        /// <summary>Most ambiguous characters probed per text; beyond it the character stays ambiguous and <see cref="Map"/> fails closed.</summary>
        private const int MaxProbes = 4096;

        /// <summary>Most positions examined while looking for ambiguous characters.</summary>
        private const int MaxScan = 200_000;

        private const string SentinelPool = "QXZJKWVYqxzjkwvy0123456789ABCDEFGHILMNOPRSTUabcdefghilmnoprstu";

        /// <summary>Most positions examined while deciding fail-closed membership in one mapping.</summary>
        private const int MaxMapScan = 1_000_000;

        /// <summary>Longest region a two-character sentinel is searched for (its bigram set is built in memory).</summary>
        private const int MaxSentinelRegion = 100_000;

        private readonly string _original;
        private readonly string _rewritten;
        private readonly int[] _earliest;
        private readonly int[] _latest;

        private UrlRewriteAlignment(string original, string rewritten, int[] earliest, int[] latest)
        {
            _original = original;
            _rewritten = rewritten;
            _earliest = earliest;
            _latest = latest;
        }

        /// <summary>Aligns <paramref name="rewritten"/> onto <paramref name="original"/>, or <c>null</c> when it is not a subsequence.</summary>
        public static UrlRewriteAlignment? Create(string original, string rewritten)
        {
            var deleted = new bool[original.Length];
            if (!TryAlign(original, rewritten, deleted, out var earliest, out var latest))
                return null;

            if (ProbeAmbiguousDeletions(original, rewritten, earliest, latest, deleted)
                && TryAlign(original, rewritten, deleted, out var exactEarliest, out var exactLatest))
            {
                earliest = exactEarliest;
                latest = exactLatest;
            }

            return new UrlRewriteAlignment(original, rewritten, earliest, latest);
        }

        /// <summary>
        /// Maps the original-text span [<paramref name="start"/>, <paramref name="end"/>) onto the
        /// rewritten text: every kept character that COULD be inside it, fail-closed.
        /// </summary>
        /// <remarks>
        /// A kept character is inside when it is placed there with certainty, or when it is ambiguous
        /// and an equal character that could be it lies inside the span (see
        /// <see cref="CouldBeInside"/>), where "equal character" is decided exactly from
        /// <paramref name="chars"/>, the character set of the form that matched. Only the characters at
        /// the two ends of the span can be ambiguous, so the result is the hull between the first and
        /// the last included character.
        /// </remarks>
        /// <returns>The rewritten-text span, or <c>null</c> when no kept character qualifies.</returns>
        public (int Start, int End)? Map(int start, int end, CharMembership chars)
        {
            // Every character whose possible placements intersect the span lies in [low, high].
            var low = FirstAtLeast(_latest, start);
            var high = FirstAtLeast(_earliest, end) - 1;
            if (low > high)
                return null;

            var budget = MaxMapScan;
            var first = -1;
            for (var j = low; j <= high; j++)
            {
                if (CouldBeInside(j, start, end, chars, ref budget))
                {
                    first = j;
                    break;
                }
            }

            if (first < 0)
                return null;

            var last = first;
            for (var j = high; j > first; j--)
            {
                if (CouldBeInside(j, start, end, chars, ref budget))
                {
                    last = j;
                    break;
                }
            }

            return (first, last + 1);
        }

        /// <summary>
        /// Whether kept character <paramref name="j"/> is certainly, or possibly, inside
        /// [<paramref name="start"/>, <paramref name="end"/>).
        /// </summary>
        /// <remarks>
        /// Certain: every placement is inside. Impossible: the matched value does not contain the
        /// character at all — decided EXACTLY from <paramref name="chars"/>, never by a budget.
        /// Possible: it is ambiguous and an EQUAL character lies inside the span within its placement
        /// range. Only that PLACEMENT scan is budgeted; when the budget is spent the answer is
        /// "inside" — the fail-closed direction, sanctioned because placement is what cannot be decided.
        /// </remarks>
        private bool CouldBeInside(int j, int start, int end, CharMembership chars, ref int budget)
        {
            var earliest = _earliest[j];
            var latest = _latest[j];
            if (earliest >= start && latest < end)
                return true;

            // MEMBERSHIP is decidable and exact: a character that the matched value does not contain
            // cannot be inside its extent under ANY placement, however large the value is. Only the
            // question of WHERE an equal character lies is budgeted below; answering this one wrongly
            // on a budget would redact ordinary text no known value can cover.
            if (!chars.Contains(_rewritten[j]))
                return false;

            var from = Math.Max(earliest, start);
            var to = Math.Min(latest, end - 1);
            for (var p = from; p <= to; p++)
            {
                if (--budget < 0)
                    return true;
                if (_original[p] == _rewritten[j])
                    return true;
            }

            return false;
        }

        /// <summary>Index of the first element that is >= <paramref name="value"/> in a strictly ascending array.</summary>
        private static int FirstAtLeast(int[] ascending, int value)
        {
            var low = 0;
            var high = ascending.Length;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (ascending[middle] < value)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        /// <summary>
        /// Greedy earliest and latest alignments of <paramref name="rewritten"/> onto
        /// <paramref name="original"/>, skipping positions already known to be deleted.
        /// </summary>
        private static bool TryAlign(string original, string rewritten, bool[] deleted, out int[] earliest, out int[] latest)
        {
            earliest = new int[rewritten.Length];
            latest = new int[rewritten.Length];

            var position = 0;
            for (var j = 0; j < rewritten.Length; j++)
            {
                while (position < original.Length && (deleted[position] || original[position] != rewritten[j]))
                    position++;
                if (position >= original.Length)
                    return false;
                earliest[j] = position++;
            }

            position = original.Length - 1;
            for (var j = rewritten.Length - 1; j >= 0; j--)
            {
                while (position >= 0 && (deleted[position] || original[position] != rewritten[j]))
                    position--;
                if (position < 0)
                    return false;
                latest[j] = position--;
            }

            return true;
        }

        /// <summary>
        /// Marks in <paramref name="deleted"/> every ambiguous alphanumeric position whose mutation
        /// leaves the rewritten output unchanged.
        /// </summary>
        /// <returns><c>true</c> when at least one position was proven deleted.</returns>
        private static bool ProbeAmbiguousDeletions(
            string original, string rewritten, int[] earliest, int[] latest, bool[] deleted)
        {
            var candidates = new SortedSet<int>();
            var scanned = 0;
            for (var j = 0; j < rewritten.Length; j++)
            {
                if (earliest[j] == latest[j])
                    continue;

                for (var p = earliest[j]; p <= latest[j]; p++)
                {
                    if (++scanned > MaxScan)
                        return false;
                    if (original[p] == rewritten[j] && char.IsAsciiLetterOrDigit(original[p]))
                        candidates.Add(p);
                }
            }

            if (candidates.Count == 0 || candidates.Count > MaxProbes)
                return false;

            // The frozen redactor treats the text as independent regions that each START at an
            // http(s):// scheme, so a probe only needs the region around the candidate. If that
            // independence does not reproduce the full rewrite, probing is not trusted at all.
            var starts = SchemeStarts(original);
            if (!RegionsReproduceTheRewrite(original, rewritten, starts))
                return false;

            var any = false;
            var regionRewrites = new Dictionary<int, string>();
            var regionSentinels = new Dictionary<int, string?>();
            foreach (var position in candidates)
            {
                var (regionStart, regionEnd) = RegionAround(starts, original.Length, position);
                var region = original.Substring(regionStart, regionEnd - regionStart);
                if (!regionRewrites.TryGetValue(regionStart, out var regionRewritten))
                    regionRewrites[regionStart] = regionRewritten = GitUrlRedactor.Redact(region) ?? string.Empty;
                if (!regionSentinels.TryGetValue(regionStart, out var sentinel))
                    regionSentinels[regionStart] = sentinel = FreshSentinel(region);

                // No sentinel exists for this region: the character stays ambiguous and Map fails closed.
                if (sentinel is null)
                    continue;

                var index = position - regionStart;
                var probe = string.Concat(region.AsSpan(0, index), sentinel, region.AsSpan(index + 1));
                if (string.Equals(GitUrlRedactor.Redact(probe), regionRewritten, StringComparison.Ordinal))
                {
                    deleted[position] = true;
                    any = true;
                }
            }

            return any;
        }

        /// <summary>The start index of every <c>http://</c> / <c>https://</c> occurrence (case-insensitive).</summary>
        private static List<int> SchemeStarts(string text)
        {
            var starts = new List<int>();
            for (var i = 0; i < text.Length; i++)
            {
                var rest = text.AsSpan(i);
                if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    starts.Add(i);
                }
            }

            return starts;
        }

        /// <summary>The region that begins at the scheme at or before <paramref name="position"/> and ends at the next one.</summary>
        private static (int Start, int End) RegionAround(List<int> starts, int length, int position)
        {
            var index = starts.BinarySearch(position);
            if (index < 0)
                index = ~index - 1;

            var regionStart = index >= 0 ? starts[index] : 0;
            var regionEnd = index + 1 < starts.Count ? starts[index + 1] : length;
            if (index < 0)
                regionEnd = starts.Count > 0 ? starts[0] : length;

            return (regionStart, regionEnd);
        }

        /// <summary>Whether rewriting each scheme-delimited region on its own yields exactly <paramref name="rewritten"/>.</summary>
        private static bool RegionsReproduceTheRewrite(string original, string rewritten, List<int> starts)
        {
            var builder = new StringBuilder(rewritten.Length);
            var previous = 0;
            foreach (var start in starts.Append(original.Length))
            {
                if (start > previous)
                    builder.Append(GitUrlRedactor.Redact(original.Substring(previous, start - previous)));
                previous = start;
            }

            return string.Equals(builder.ToString(), rewritten, StringComparison.Ordinal);
        }

        /// <summary>
        /// A string of alphanumeric characters that does not occur in <paramref name="region"/>, or
        /// <c>null</c> when none can be found.
        /// </summary>
        /// <remarks>
        /// A single character is tried first. A region that contains EVERY alphanumeric (a path that
        /// spells the alphabet) has none, so the source is made inexhaustible with two-character
        /// sentinels: a region of n characters holds at most n - 1 distinct pairs, so a pair is absent
        /// from any region shorter than the number of pairs. The sentinel only has to survive the
        /// frozen redactor if the probed character was KEPT, and it is built from characters the
        /// URL grammar accepts, so it never changes where a candidate ends.
        /// </remarks>
        private static string? FreshSentinel(string region)
        {
            foreach (var candidate in SentinelPool)
            {
                if (region.IndexOf(candidate) < 0)
                    return candidate.ToString();
            }

            if (region.Length > MaxSentinelRegion)
                return null;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < region.Length; i++)
                seen.Add(region.Substring(i, 2));

            foreach (var first in SentinelPool)
            {
                foreach (var second in SentinelPool)
                {
                    var pair = string.Concat(first.ToString(), second.ToString());
                    if (!seen.Contains(pair))
                        return pair;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Replaces each union of overlapping spans with ONE <see cref="Placeholder"/>. Spans that merely
    /// touch (one ends where the next starts) stay separate, so two adjacent secrets keep two
    /// placeholders.
    /// </summary>
    private static string ReplaceSpans(string text, List<(int Start, int End)> spans)
    {
        // Spans arrive in ascending Start order, so a single sweep merges them.
        var builder = new StringBuilder(text.Length);
        var copied = 0;
        var mergedStart = spans[0].Start;
        var mergedEnd = spans[0].End;

        for (var i = 1; i <= spans.Count; i++)
        {
            if (i < spans.Count && spans[i].Start < mergedEnd)
            {
                mergedEnd = Math.Max(mergedEnd, spans[i].End);
                continue;
            }

            builder.Append(text, copied, mergedStart - copied);
            builder.Append(Placeholder);
            copied = mergedEnd;

            if (i < spans.Count)
            {
                mergedStart = spans[i].Start;
                mergedEnd = spans[i].End;
            }
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    /// <summary>
    /// The STANDARD base64 forms of <paramref name="secret"/>: the padded encoding and, when it
    /// carries padding, the same encoding with the trailing <c>=</c> removed. No other alphabet or
    /// padding style is derived.
    /// </summary>
    private static IReadOnlyList<string> StandardBase64Forms(string secret)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        var unpadded = encoded.TrimEnd('=');
        return string.Equals(unpadded, encoded, StringComparison.Ordinal)
            ? [encoded]
            : [encoded, unpadded];
    }

    /// <summary>
    /// Attempts to match <paramref name="secret"/> at <paramref name="start"/>, allowing every
    /// character to be either literal or a single one-level percent escape of that character.
    /// Used by <see cref="CollectInventorySpans"/> for values made of single-byte characters only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching walks the value once, consuming each expected character as a literal or as exactly
    /// ONE escape, so no recursive decoding can occur: <c>%255F</c> decodes to <c>%5F</c> — the
    /// byte of <c>%</c>, not of the escaped character — and therefore never matches the character
    /// whose escape it is a fragment of. The only point where the reading is ambiguous is a
    /// <c>%</c> in the value itself (the text may hold a literal <c>%</c> or its escape
    /// <c>%25</c>); there the matcher BACKTRACKS, preferring the longer escape reading and falling
    /// back to the literal one when the remainder cannot match.
    /// </para>
    /// <para>
    /// The scan compares one expected BYTE against one text character, which is exact only for
    /// single-byte (ASCII) characters. A value with any non-ASCII character is left to the raw and
    /// base64 matches rather than matched approximately.
    /// </para>
    /// </remarks>
    private static bool TryMatchPercentEncoded(string text, int start, string secret, out int end)
    {
        // Only a '%' in the secret itself is ambiguous: in the text it may appear as a literal '%'
        // or as the one-level escape "%25". Every other character has exactly one possible form
        // (a literal can never begin an escape, and an escape never equals a non-'%' literal), so
        // the matcher only ever needs to BACKTRACK at a '%'. At that point the escape reading is
        // tried FIRST (it is the longer one) with the literal reading as the fallback.
        return TryMatchPercentEncodedFrom(text, start, secret, 0, out end);
    }

    /// <summary>
    /// Matches <paramref name="secret"/> from character <paramref name="index"/> onward at
    /// <paramref name="position"/>. Every character has exactly one reading except a <c>%</c> that
    /// is present both as a literal and as the start of <c>%25</c>: there the one-level escape is
    /// preferred (it is the longer reading) and the literal is the fallback when the remainder of
    /// the value cannot match after the escape.
    /// </summary>
    private static bool TryMatchPercentEncodedFrom(string text, int position, string secret, int index, out int end)
    {
        for (; index < secret.Length; index++)
        {
            var expected = secret[index];
            var literal = position < text.Length && text[position] == expected;
            var escaped = position + 2 < text.Length
                && text[position] == '%'
                && TryParseHexDigit(text[position + 1], out var high)
                && TryParseHexDigit(text[position + 2], out var low)
                && (byte)((high << 4) | low) == (byte)expected;

            if (literal && escaped)
            {
                // Only reachable for expected == '%' with "%25" in the text. Prefer the ESCAPE: it
                // is the longer reading, so a value that ends in '%' is removed WHOLE instead of
                // leaving the "25" of its own escape behind. Fall back to the literal reading if the
                // remainder cannot match after the escape.
                if (TryMatchPercentEncodedFrom(text, position + 3, secret, index + 1, out end))
                    return true;

                position++;
                continue;
            }

            if (literal)
            {
                position++;
                continue;
            }

            if (escaped)
            {
                position += 3;
                continue;
            }

            end = position;
            return false;
        }

        end = position;
        return true;
    }

    // ── Span source 2: structural patterns ────────────────────────────────────

    /// <summary>
    /// Collects the span of the PASSWORD of every scp-style remote (<c>user:password@host:path</c>),
    /// keeping the user name and the host:path form intact.
    /// </summary>
    /// <remarks>
    /// A form without a password — <c>git@host:path</c> or <c>user@host:path</c> — never matches,
    /// because the userinfo run before the <c>@</c> must contain a separator with a non-empty user
    /// on its left and a non-empty password on its right. The host must itself be followed by
    /// <c>:</c>, which is what distinguishes the scp form from a URL authority.
    /// </remarks>
    private static void CollectScpStylePasswordSpans(string text, List<(int Start, int End)> spans)
    {
        var lastEnd = 0;
        var at = text.IndexOf('@');

        while (at >= 0)
        {
            // A candidate that starts inside the previous one would claim the same text twice.
            if (TryFindScpPassword(text, at, out var passwordStart, out var passwordEnd)
                && passwordStart >= lastEnd)
            {
                spans.Add((passwordStart, passwordEnd));
                lastEnd = passwordEnd;
            }

            at = text.IndexOf('@', at + 1);
        }
    }

    /// <summary>
    /// Locates the password of an scp-style credential whose <c>@</c> is at
    /// <paramref name="at"/>, returning it as the half-open range
    /// [<paramref name="passwordStart"/>, <paramref name="passwordEnd"/>).
    /// </summary>
    private static bool TryFindScpPassword(string text, int at, out int passwordStart, out int passwordEnd)
    {
        passwordStart = 0;
        passwordEnd = 0;

        // The host runs up to the ':' separator and must be non-empty and followed by a path.
        var hostStart = at + 1;
        var hostEnd = hostStart;
        while (hostEnd < text.Length)
        {
            if (IsHostCharacter(text[hostEnd]))
                hostEnd++;
            else if (StartsWithPlaceholder(text, hostEnd))
                hostEnd += Placeholder.Length;
            else
                break;
        }

        if (hostEnd == hostStart || hostEnd >= text.Length || text[hostEnd] != ':')
            return false;
        if (hostEnd + 1 >= text.Length || char.IsWhiteSpace(text[hostEnd + 1]))
            return false;

        // The userinfo run immediately before the '@' — `user[:password]`, so the separator is
        // INSIDE the run and the password is what follows it.
        var userEnd = at;
        var userStart = userEnd;
        while (userStart > 0)
        {
            if (IsScpUserInfoCharacter(text[userStart - 1]))
                userStart--;
            else if (EndsWithPlaceholder(text, userStart))
                userStart -= Placeholder.Length;
            else
                break;
        }
        if (userStart == userEnd)
            return false;

        // Split at the FIRST ':' of the run. A run without one is a password-less userinfo
        // (`git@host:path`, `user@host:path`) and is deliberately left untouched.
        var separator = text.IndexOf(':', userStart, userEnd - userStart);
        if (separator < userStart || separator + 1 >= userEnd)
            return false;

        passwordStart = separator + 1;
        passwordEnd = userEnd;
        return true;
    }

    /// <summary>
    /// Collects the span of the PASSWORD userinfo of every <c>ssh://</c> URL
    /// (<c>ssh://user:password@host[:port]/path</c>), keeping the user name.
    /// </summary>
    /// <remarks>
    /// A URL with no userinfo (<c>ssh://host/path</c>, so a port's <c>:</c> is not a credential) or
    /// with a password-less userinfo (<c>ssh://git@host/path</c>) is returned unchanged.
    /// </remarks>
    private static void CollectSshUrlPasswordSpans(string text, List<(int Start, int End)> spans)
    {
        var lastEnd = 0;
        var index = 0;

        while (index < text.Length)
        {
            if (MatchSshScheme(text, index) == 0)
            {
                index++;
                continue;
            }

            var authorityStart = index + SshScheme.Length;
            var authorityEnd = authorityStart;
            while (authorityEnd < text.Length)
            {
                if (IsAuthorityCharacter(text[authorityEnd]))
                    authorityEnd++;
                else if (StartsWithPlaceholder(text, authorityEnd))
                    authorityEnd += Placeholder.Length;
                else
                    break;
            }

            if (authorityEnd > authorityStart)
            {
                var lastAt = text.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
                if (lastAt > authorityStart)
                {
                    var separator = text.IndexOf(':', authorityStart, lastAt - authorityStart);
                    if (separator >= authorityStart && separator + 1 < lastAt
                        && separator + 1 >= lastEnd)
                    {
                        spans.Add((separator + 1, lastAt));
                        lastEnd = lastAt;
                    }
                }
            }

            index = Math.Max(authorityEnd, index + SshScheme.Length);
        }
    }

    /// <summary>
    /// Collects the spans of the credential value of every <c>Authorization</c> (and, at its own
    /// name boundary, <c>Proxy-Authorization</c>) header whose scheme is <c>Basic</c> or
    /// <c>Bearer</c>.
    /// </summary>
    /// <remarks>
    /// Git and credential helpers print headers verbatim, and a long value is often FOLDED across
    /// continuation lines that begin with whitespace (RFC 5322 folding). Every folded segment is
    /// redacted in place, so the line breaks and indentation — and therefore the total line count a
    /// caller reports after truncation — are preserved exactly.
    /// </remarks>
    private static void CollectAuthorizationHeaderSpans(string text, List<(int Start, int End)> spans)
    {
        var lastEnd = 0;
        var index = 0;

        while (index < text.Length)
        {
            var match = text.IndexOf(AuthorizationHeaderName, index, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
                break;

            index = match + 1;

            // The header name is matched as a whole word: a letter or digit glued to it means this
            // is not the header ("XAuthorization"), while '-' is a separator (Proxy-Authorization).
            if (match > 0 && (char.IsLetterOrDigit(text[match - 1]) || text[match - 1] == '_'))
                continue;

            if (FindAuthorizationValueSegments(text, match + AuthorizationHeaderName.Length) is not { Count: > 0 } segments)
                continue;

            // A candidate starting inside a value already claimed would claim the text twice.
            if (segments[0].Start < lastEnd)
                continue;

            spans.AddRange(segments);
            lastEnd = segments[^1].End;
            index = lastEnd;
        }
    }

    /// <summary>
    /// Returns the ranges of the credential value of a <c>Basic</c>/<c>Bearer</c> authorization
    /// header whose name ended at <paramref name="index"/> — the first line's segment followed by
    /// every folded continuation segment — or <c>null</c> when the text at that position is not
    /// such a header.
    /// </summary>
    private static List<(int Start, int End)>? FindAuthorizationValueSegments(string text, int index)
    {
        var cursor = index;
        while (cursor < text.Length && IsHorizontalSpace(text[cursor]))
            cursor++;

        if (cursor >= text.Length || text[cursor] != ':')
            return null;
        cursor++;

        while (cursor < text.Length && IsHorizontalSpace(text[cursor]))
            cursor++;

        var schemeLength = MatchAuthorizationScheme(text, cursor);
        if (schemeLength == 0)
            return null;

        cursor += schemeLength;
        if (cursor >= text.Length || !IsHorizontalSpace(text[cursor]))
            return null;

        while (cursor < text.Length && IsHorizontalSpace(text[cursor]))
            cursor++;

        // Only a run of credential-token characters is treated as the value. The boundary is
        // FAIL-CLOSED: anything after the scheme is a credential by construction, so even a short
        // token run is redacted (the surrounding prose, outside the run, survives untouched).
        var valueEnd = ScanCredentialToken(text, cursor);
        if (valueEnd == cursor)
            return null;

        var segments = new List<(int Start, int End)> { (cursor, valueEnd) };

        var lineEnd = valueEnd;
        while (true)
        {
            // Trailing horizontal whitespace after a segment must not hide the fold that follows;
            // it is skipped for RECOGNITION only — it is never part of a redacted range, so the
            // surrounding text and every line break stay byte-for-byte as they were.
            var next = lineEnd;
            while (next < text.Length && IsHorizontalSpace(text[next]))
                next++;
            if (next < text.Length && text[next] == '\r')
                next++;
            if (next >= text.Length || text[next] != '\n')
                break;
            next++;

            var indentedStart = next;
            while (next < text.Length && IsHorizontalSpace(text[next]))
                next++;
            if (next == indentedStart)
                break;

            // A continuation line that is itself an Authorization header is a NEW header, not a
            // value segment of this one: header recognition takes priority, so it is left for the
            // caller's scan to find (and to redact with its own scheme) instead of being consumed
            // here as a "credential" word.
            if (IsAuthorizationHeaderStart(text, next))
                break;

            var continuationEnd = ScanCredentialToken(text, next);
            if (continuationEnd == next)
                break;

            segments.Add((next, continuationEnd));
            lineEnd = continuationEnd;
        }

        return segments;
    }

    /// <summary>
    /// Whether an <c>Authorization</c> or <c>Proxy-Authorization</c> header name followed by
    /// <c>:</c> (horizontal whitespace allowed before it) starts at <paramref name="index"/>.
    /// </summary>
    private static bool IsAuthorizationHeaderStart(string text, int index)
    {
        var span = text.AsSpan(index);
        var nameLength = 0;
        if (span.StartsWith(ProxyAuthorizationHeaderName, StringComparison.OrdinalIgnoreCase))
            nameLength = ProxyAuthorizationHeaderName.Length;
        else if (span.StartsWith(AuthorizationHeaderName, StringComparison.OrdinalIgnoreCase))
            nameLength = AuthorizationHeaderName.Length;
        else
            return false;

        var cursor = index + nameLength;
        while (cursor < text.Length && IsHorizontalSpace(text[cursor]))
            cursor++;

        return cursor < text.Length && text[cursor] == ':';
    }

    /// <summary>
    /// Collects the span of the value of every credential-named assignment: the four inventory environment
    /// variables and generic git-config style <c>token = …</c> / <c>credential: …</c> output.
    /// </summary>
    /// <remarks>
    /// A name only matches as a WHOLE name (so <c>tokens</c> and <c>mypassword</c> do not), only
    /// when it is followed — across horizontal whitespace only, never a line break — by <c>=</c> or
    /// <c>:</c>, and only when a value follows. A quoted value is redacted inside its quotes; an
    /// unterminated quote is redacted to the end of the line rather than left exposed.
    /// <para>
    /// The boundary is FAIL-CLOSED by design: a <c>password:</c> in ordinary prose is redacted too.
    /// Over-redacting a line of a diff is a cosmetic loss, while under-redacting a credential is
    /// permanent exposure, so the ambiguous case resolves toward redaction.
    /// </para>
    /// </remarks>
    private static void CollectSecretAssignmentSpans(string text, List<(int Start, int End)> spans)
    {
        var lastEnd = 0;
        var index = 0;

        while (index < text.Length)
        {
            if (!TryMatchAssignmentName(text, index, out var nameLength))
            {
                index++;
                continue;
            }

            if (FindAssignedValue(text, index + nameLength) is not { } value)
            {
                index += nameLength;
                continue;
            }

            // The "value" is itself a credential assignment (`credential: password: SECRET`):
            // consuming it as this assignment's value would redact only the inner KEYWORD and leave
            // the real secret behind. Leave the outer name alone and let the scan reach the inner
            // assignment, which redacts the actual value. Chained nesting resolves the same way.
            if (IsNestedAssignment(text, value))
            {
                index += nameLength;
                continue;
            }

            // A value starting inside an earlier claim would claim the text twice.
            if (value.Start < lastEnd)
            {
                index = Math.Max(value.End, index + nameLength);
                continue;
            }

            spans.Add(value);
            lastEnd = value.End;
            index = value.End;
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/> consists of NOTHING but another credential name (with at
    /// most one trailing <c>=</c> / <c>:</c>) that itself has an assigned value after it — i.e. the
    /// outer assignment's "value" is really the start of a nested assignment.
    /// </summary>
    /// <remarks>
    /// Deliberately exact: <c>password = token=SECRET</c> has the value <c>token=SECRET</c>, which
    /// is more than a bare name, so it stays a single redaction of the whole value.
    /// </remarks>
    private static bool IsNestedAssignment(string text, (int Start, int End) value)
    {
        if (!TryMatchAssignmentName(text, value.Start, out var innerNameLength))
            return false;

        var afterName = value.Start + innerNameLength;
        if (afterName < value.End && text[afterName] is '=' or ':')
            afterName++;

        return afterName == value.End && FindAssignedValue(text, value.Start + innerNameLength) is not null;
    }

    /// <summary>
    /// Whether one of the <see cref="AssignmentNames"/> starts at <paramref name="index"/> as a
    /// whole name.
    /// </summary>
    private static bool TryMatchAssignmentName(string text, int index, out int nameLength)
    {
        var before = index > 0 ? text[index - 1] : ' ';

        // A letter or digit immediately before the name makes it part of a longer word.
        if (char.IsLetterOrDigit(before))
        {
            nameLength = 0;
            return false;
        }

        foreach (var name in AssignmentNames)
        {
            if (!text.AsSpan(index).StartsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;

            // The name must END at a name boundary too: "tokens" is not "token".
            var after = index + name.Length < text.Length ? text[index + name.Length] : ' ';
            if (char.IsLetterOrDigit(after) || after == '_')
                continue;

            nameLength = name.Length;
            return true;
        }

        nameLength = 0;
        return false;
    }

    /// <summary>
    /// Locates the value assigned at <paramref name="index"/> — that is, after an optional run of
    /// horizontal whitespace, an <c>=</c> or <c>:</c>, and more optional horizontal whitespace.
    /// </summary>
    /// <returns>The value's half-open range, or <c>null</c> when no assignment (or no value) follows.</returns>
    private static (int Start, int End)? FindAssignedValue(string text, int index)
    {
        var cursor = index;
        while (cursor < text.Length && IsHorizontalSpace(text[cursor]))
            cursor++;

        if (cursor >= text.Length || (text[cursor] != '=' && text[cursor] != ':'))
            return null;

        cursor++;
        while (cursor < text.Length && IsHorizontalSpace(text[cursor]))
            cursor++;

        if (cursor >= text.Length || char.IsWhiteSpace(text[cursor]))
            return null;

        if (text[cursor] is not ('"' or '\''))
        {
            var plainEnd = cursor;
            while (plainEnd < text.Length && !char.IsWhiteSpace(text[plainEnd]))
                plainEnd++;
            return (cursor, plainEnd);
        }

        var quote = text[cursor];
        var lineEndRelative = text.AsSpan(cursor + 1).IndexOfAny('\r', '\n');
        var lineEnd = lineEndRelative < 0 ? text.Length : cursor + 1 + lineEndRelative;

        var closing = text.IndexOf(quote, cursor + 1);
        if (closing > cursor + 1 && closing < lineEnd)
            return (cursor + 1, closing);

        // Unterminated quote on this line: redact what is there instead of leaving it exposed.
        return cursor + 1 < lineEnd ? (cursor + 1, lineEnd) : null;
    }

    // ── Shared character and scheme helpers ───────────────────────────────────

    /// <summary>
    /// Whether an already-substituted <see cref="Placeholder"/> starts at <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// The text this transform receives may ALREADY contain <c>[redacted]</c> — output that an
    /// earlier boundary (the repository manager's own redaction) has substituted, or a value the
    /// operator literally logged. Because <c>[</c> and <c>]</c> are not authority, host or
    /// userinfo characters, a scan that stopped there would end an SSH/scp credential run early
    /// and strand the structural password. Treating the placeholder as part of the run keeps such
    /// text from blinding the scan (the same principle <see cref="ScanCredentialToken"/> applies to
    /// headers). The inventory source no longer produces placeholders ahead of the structural
    /// scans — both collect spans from the same unmodified text — so this is about pre-redacted
    /// INPUT only.
    /// </remarks>
    private static bool StartsWithPlaceholder(string text, int index) =>
        text.AsSpan(index).StartsWith(Placeholder, StringComparison.Ordinal);

    /// <summary>
    /// Whether an already-substituted <see cref="Placeholder"/> ends exactly at
    /// <paramref name="end"/> (exclusive) — the backwards counterpart of
    /// <see cref="StartsWithPlaceholder"/> for the scp userinfo scan.
    /// </summary>
    private static bool EndsWithPlaceholder(string text, int end) =>
        end >= Placeholder.Length
        && text.AsSpan(end - Placeholder.Length, Placeholder.Length).SequenceEqual(Placeholder);

    /// <summary>
    /// Returns the length of the <c>ssh://</c> scheme at <paramref name="index"/>, or 0 when no
    /// such scheme starts there. The comparison is case-insensitive.
    /// </summary>
    private static int MatchSshScheme(string text, int index) =>
        text.AsSpan(index).StartsWith(SshScheme, StringComparison.OrdinalIgnoreCase) ? SshScheme.Length : 0;

    /// <summary>
    /// Returns the length of the <c>Basic</c> / <c>Bearer</c> authorization scheme at
    /// <paramref name="index"/>, or 0 when neither starts there. Comparisons are case-insensitive.
    /// </summary>
    private static int MatchAuthorizationScheme(string text, int index)
    {
        var span = text.AsSpan(index);
        if (span.StartsWith(BasicScheme, StringComparison.OrdinalIgnoreCase))
            return BasicScheme.Length;
        return span.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase) ? BearerScheme.Length : 0;
    }

    /// <summary>Whether the character is a space or a horizontal tab.</summary>
    private static bool IsHorizontalSpace(char c) => c is ' ' or '\t';

    /// <summary>
    /// Whether the character may appear inside a URL/userinfo authority (RFC 3986 userinfo plus
    /// reg-name characters). Whitespace, <c>/</c>, <c>?</c>, <c>#</c> and quotes are excluded, which
    /// is what bounds the authority scan.
    /// </summary>
    private static bool IsAuthorityCharacter(char c) =>
        IsAlphaNumeric(c)
        || c is '-' or '.' or '_' or '~' or '%' or '!' or '$' or '&' or '\'' or '(' or ')'
              or '*' or '+' or ',' or ';' or '=' or ':' or '@';

    /// <summary>
    /// Whether the character may appear in the userinfo run scanned backwards from an <c>@</c>.
    /// Deliberately narrow: an unrecognized userinfo character only ends the scan (the URL passes
    /// then cover well-formed URLs), it never widens what is redacted.
    /// </summary>
    private static bool IsUserInfoCharacter(char c) =>
        IsAlphaNumeric(c) || c is '-' or '.' or '_' or '~' or '%' or '+' or '!' or '$' or '&' or '\'' or '=';

    /// <summary>
    /// Whether the character may appear in an scp-style userinfo run — the same set as
    /// <see cref="IsUserInfoCharacter"/> PLUS the <c>:</c> that separates the user name from the
    /// password, since recognising <c>user:password</c> as ONE run is the whole point of that pass.
    /// </summary>
    private static bool IsScpUserInfoCharacter(char c) => IsUserInfoCharacter(c) || c == ':';

    /// <summary>Whether the character may appear in the host of an scp-style remote.</summary>
    private static bool IsHostCharacter(char c) => IsAlphaNumeric(c) || c is '-' or '.' or '_';

    /// <summary>
    /// Whether the character may appear in an authorization header's credential value: the base64
    /// alphabet with its padding, plus the token characters a bearer token or percent escape uses.
    /// </summary>
    private static bool IsCredentialTokenCharacter(char c) =>
        IsAlphaNumeric(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '=' or '%';

    /// <summary>
    /// Scans a run of <see cref="IsCredentialTokenCharacter"/> characters, treating an already
    /// substituted <see cref="Placeholder"/> as part of the run.
    /// </summary>
    /// <remarks>
    /// The input may already contain <c>[redacted]</c> inside a header value (output an earlier
    /// boundary redacted). Because <c>[</c> is not a token character, a scan that stopped there
    /// would end the header's value early (or reject the header outright) and expose every LATER
    /// folded segment. Consuming the placeholder keeps pre-redacted input from blinding the scan;
    /// re-redacting it is idempotent.
    /// </remarks>
    private static int ScanCredentialToken(string text, int start)
    {
        var end = start;
        while (end < text.Length)
        {
            if (IsCredentialTokenCharacter(text[end]))
            {
                end++;
                continue;
            }

            if (text.AsSpan(end).StartsWith(Placeholder, StringComparison.Ordinal))
            {
                end += Placeholder.Length;
                continue;
            }

            break;
        }

        return end;
    }

    /// <summary>Whether the character is an ASCII letter or digit.</summary>
    private static bool IsAlphaNumeric(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    /// <summary>Parses one hexadecimal digit, case-insensitively.</summary>
    private static bool TryParseHexDigit(char c, out int value)
    {
        switch (c)
        {
            case >= '0' and <= '9':
                value = c - '0';
                return true;
            case >= 'a' and <= 'f':
                value = c - 'a' + 10;
                return true;
            case >= 'A' and <= 'F':
                value = c - 'A' + 10;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
