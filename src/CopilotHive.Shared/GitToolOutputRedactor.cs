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
/// <b>Two independent layers.</b>
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>Known-secret inventory.</b> Values the Composer itself holds are replaced verbatim,
///     together with their one-level percent-encoded and standard-base64 forms. Only values of at
///     least <see cref="MinimumInventoryValueLength"/> characters participate: a one- or
///     two-character "secret" would match ordinary text everywhere and destroy the output for no
///     security benefit. Shorter structural credentials are still removed by layer 2.
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

        // Layer 1 runs FIRST. Exact replacement removes a WHOLE inventory value, so a structural
        // pass can never leave a fraction of a known secret behind by redacting part of it (for
        // example a secret that itself contains '@' or ':').
        var redacted = RedactKnownSecrets(text, knownSecrets);
        return RedactStructuralPatterns(redacted);
    }

    // ── Layer 1: the known-secret inventory ───────────────────────────────────

    /// <summary>
    /// Applies the inventory: raw, one-level percent-encoded and standard-base64 forms of every
    /// eligible value, longest value first so a secret that CONTAINS another is removed whole.
    /// </summary>
    private static string RedactKnownSecrets(string text, IEnumerable<string?>? knownSecrets)
    {
        if (knownSecrets is null)
            return text;

        var eligible = new List<string>();
        foreach (var candidate in knownSecrets)
        {
            if (candidate is not { Length: >= MinimumInventoryValueLength })
                continue;
            if (!eligible.Contains(candidate, StringComparer.Ordinal))
                eligible.Add(candidate);
        }

        if (eligible.Count == 0)
            return text;

        eligible.Sort(static (left, right) => right.Length.CompareTo(left.Length));

        var redacted = text;
        foreach (var secret in eligible)
        {
            // The percent-aware pass runs FIRST because it also matches the raw form: a plain
            // replacement first would fire on a raw PREFIX of a longer one-level encoding (a value
            // ending in '%' against text "...%25") and leave the escape tail ("25") behind. The raw
            // replacement afterwards only mops up what the percent-aware pass cannot decode
            // (non-ASCII values are skipped there).
            redacted = ReplacePercentEncoded(redacted, secret);
            redacted = redacted.Replace(secret, Placeholder, StringComparison.Ordinal);
            foreach (var encoded in StandardBase64Forms(secret))
                redacted = redacted.Replace(encoded, Placeholder, StringComparison.Ordinal);
        }

        return redacted;
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
    /// Replaces every occurrence of <paramref name="secret"/> in which EACH character may appear
    /// either literally or as a ONE-LEVEL UTF-8 percent escape (<c>%XX</c>, hex digits in either
    /// case).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching is a single forward pass that consumes each expected character or escape exactly
    /// once, so no recursive decoding can occur: <c>%255F</c> decodes to <c>%5F</c> — the byte of
    /// <c>%</c>, not of the escaped character — and therefore never matches the character whose
    /// escape it is a fragment of.
    /// </para>
    /// <para>
    /// The scan compares one expected BYTE against one text character, which is exact only for
    /// single-byte (ASCII) characters. A value with any non-ASCII character is left to the raw and
    /// base64 replacements rather than matched approximately.
    /// </para>
    /// </remarks>
    private static string ReplacePercentEncoded(string text, string secret)
    {
        if (secret.Any(static c => c > 0x7F))
            return text;

        StringBuilder? builder = null;
        var copied = 0;
        var index = 0;

        while (index < text.Length)
        {
            // Fast path: only a literal first character or a leading escape can start a match.
            if ((text[index] == secret[0] || text[index] == '%')
                && TryMatchPercentEncoded(text, index, secret, out var end))
            {
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, index - copied);
                builder.Append(Placeholder);
                copied = end;
                index = end;
                continue;
            }

            index++;
        }

        if (builder is null)
            return text;

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    /// <summary>
    /// Attempts to match <paramref name="secret"/> at <paramref name="start"/>, allowing every
    /// character to be either literal or a single one-level percent escape of that character.
    /// </summary>
    private static bool TryMatchPercentEncoded(string text, int start, string secret, out int end)
    {
        // Only a '%' in the secret itself is ambiguous: in the text it may appear as a literal '%'
        // or as the one-level escape "%25". Every other character has exactly one possible form
        // (a literal can never begin an escape, and an escape never equals a non-'%' literal), so
        // the matcher only ever needs to BACKTRACK at a '%'.
        return TryMatchPercentEncodedFrom(text, start, secret, 0, out end);
    }

    /// <summary>
    /// Matches <paramref name="secret"/> from character <paramref name="index"/> onward at
    /// <paramref name="position"/>, trying the literal form first and — for a <c>%</c> only — the
    /// one-level escape <c>%25</c> when the literal form leads to a dead end.
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

    // ── Layer 2: structural patterns ──────────────────────────────────────────

    /// <summary>
    /// Applies every structural pattern, in an order where each pass can only make the following
    /// pass's input safer: scp-style and SSH passwords first, then the composed
    /// <see cref="GitUrlRedactor"/> grammar over the remaining URLs, then authorization headers,
    /// then name/value assignments last (an assignment value may itself have been a URL).
    /// </summary>
    private static string RedactStructuralPatterns(string text)
    {
        var redacted = RedactScpStylePasswords(text);
        redacted = RedactSshUrlPasswords(redacted);
        redacted = GitUrlRedactor.Redact(redacted) ?? string.Empty;
        redacted = RedactAuthorizationHeaders(redacted);
        return RedactSecretAssignments(redacted);
    }

    /// <summary>
    /// Redacts the PASSWORD of an scp-style remote (<c>user:password@host:path</c>), keeping the
    /// user name and the host:path form intact.
    /// </summary>
    /// <remarks>
    /// A form without a password — <c>git@host:path</c> or <c>user@host:path</c> — never matches,
    /// because the userinfo run before the <c>@</c> must contain a separator with a non-empty user
    /// on its left and a non-empty password on its right. The host must itself be followed by
    /// <c>:</c>, which is what distinguishes the scp form from a URL authority.
    /// </remarks>
    private static string RedactScpStylePasswords(string text)
    {
        StringBuilder? builder = null;
        var copied = 0;
        var at = text.IndexOf('@');

        while (at >= 0)
        {
            // A match that starts before the last redaction would rewrite text already replaced, so
            // an overlapping candidate is skipped rather than spliced twice.
            if (TryFindScpPassword(text, at, out var passwordStart, out var passwordEnd)
                && passwordStart >= copied)
            {
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, passwordStart - copied);
                builder.Append(Placeholder);
                copied = passwordEnd;
            }

            at = text.IndexOf('@', at + 1);
        }

        if (builder is null)
            return text;

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
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
        while (hostEnd < text.Length && IsHostCharacter(text[hostEnd]))
            hostEnd++;

        if (hostEnd == hostStart || hostEnd >= text.Length || text[hostEnd] != ':')
            return false;
        if (hostEnd + 1 >= text.Length || char.IsWhiteSpace(text[hostEnd + 1]))
            return false;

        // The userinfo run immediately before the '@' — `user[:password]`, so the separator is
        // INSIDE the run and the password is what follows it.
        var userEnd = at;
        var userStart = userEnd;
        while (userStart > 0 && IsScpUserInfoCharacter(text[userStart - 1]))
            userStart--;
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
    /// Redacts the PASSWORD userinfo of an <c>ssh://</c> URL
    /// (<c>ssh://user:password@host[:port]/path</c>), keeping the user name.
    /// </summary>
    /// <remarks>
    /// A URL with no userinfo (<c>ssh://host/path</c>, so a port's <c>:</c> is not a credential) or
    /// with a password-less userinfo (<c>ssh://git@host/path</c>) is returned unchanged.
    /// </remarks>
    private static string RedactSshUrlPasswords(string text)
    {
        StringBuilder? builder = null;
        var copied = 0;
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
            while (authorityEnd < text.Length && IsAuthorityCharacter(text[authorityEnd]))
                authorityEnd++;

            if (authorityEnd > authorityStart)
            {
                var lastAt = text.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
                if (lastAt > authorityStart)
                {
                    var separator = text.IndexOf(':', authorityStart, lastAt - authorityStart);
                    if (separator >= authorityStart && separator + 1 < lastAt
                        && separator + 1 >= copied)
                    {
                        builder ??= new StringBuilder(text.Length);
                        builder.Append(text, copied, separator + 1 - copied);
                        builder.Append(Placeholder);
                        copied = lastAt;
                    }
                }
            }

            index = Math.Max(authorityEnd, index + SshScheme.Length);
        }

        if (builder is null)
            return text;

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    /// <summary>
    /// Redacts the credential value of an <c>Authorization</c> (and, at its own name boundary,
    /// <c>Proxy-Authorization</c>) header whose scheme is <c>Basic</c> or <c>Bearer</c>.
    /// </summary>
    /// <remarks>
    /// Git and credential helpers print headers verbatim, and a long value is often FOLDED across
    /// continuation lines that begin with whitespace (RFC 5322 folding). Every folded segment is
    /// redacted in place, so the line breaks and indentation — and therefore the total line count a
    /// caller reports after truncation — are preserved exactly.
    /// </remarks>
    private static string RedactAuthorizationHeaders(string text)
    {
        StringBuilder? builder = null;
        var copied = 0;
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

            // A candidate starting inside a value already redacted would splice text twice.
            if (segments[0].Start < copied)
                continue;

            builder ??= new StringBuilder(text.Length);
            foreach (var (start, end) in segments)
            {
                builder.Append(text, copied, start - copied);
                builder.Append(Placeholder);
                copied = end;
            }

            index = segments[^1].End;
        }

        if (builder is null)
            return text;

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
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
    /// Redacts the value of every credential-named assignment: the four inventory environment
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
    private static string RedactSecretAssignments(string text)
    {
        StringBuilder? builder = null;
        var copied = 0;
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

            // A value starting inside an earlier redaction would splice the text twice.
            if (value.Start < copied)
            {
                index = Math.Max(value.End, index + nameLength);
                continue;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, value.Start - copied);
            builder.Append(Placeholder);
            copied = value.End;
            index = value.End;
        }

        if (builder is null)
            return text;

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
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
    /// The inventory layer runs first and leaves <c>[redacted]</c> inside a header value. Because
    /// <c>[</c> is not a token character, a scan that stopped there would end the header's value
    /// early (or reject the header outright) and expose every LATER folded segment. Consuming the
    /// placeholder keeps the two layers from blinding each other; re-redacting it is idempotent.
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
