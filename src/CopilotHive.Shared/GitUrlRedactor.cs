using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace CopilotHive.Services;

/// <summary>
/// Removes credentials from HTTP/HTTPS repository URLs embedded in free-form text
/// (log messages, exception messages, task-result fields).
/// </summary>
/// <remarks>
/// <para>
/// Credential-bearing clone URLs (<c>https://x-access-token:&lt;token&gt;@github.com/org/repo.git</c>)
/// routinely end up inside git error text, which is then copied into log lines and exception
/// messages. This type is the single, deterministic text transform used when such a message is
/// CONSTRUCTED — it never mutates raw git process output. Raw stdout/stderr stay functional data
/// (SHAs, statuses, diffs, filenames); redaction happens only at the boundary where a message is
/// built for a human-visible sink.
/// </para>
/// <para>
/// The algorithm is deliberately two-stage and regex-free:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>Stage 1 — candidate scanning (generous, structural).</b> Scanning starts at every
///     <c>http://</c> / <c>https://</c> occurrence (scheme comparison is case-insensitive) and
///     continues while characters belong to the accepted URL character set: the RFC 3986
///     path/query/fragment characters plus <c>(</c>, <c>[</c> and <c>{</c> as an accepted
///     extension. It terminates at the first character outside that set (whitespace,
///     <c>&lt;</c>, <c>&gt;</c>, <c>"</c>, backtick, …) or at an UNBALANCED closer — a
///     <c>)</c>, <c>]</c> or <c>}</c> whose opener did not appear earlier INSIDE the recognized
///     URL (counter-based balancing).
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Stage 2 — trailing cleanup.</b> The designated trailing punctuation
///     <c>.</c> <c>,</c> <c>;</c> <c>:</c> <c>!</c> <c>?</c> is stripped from the END of the
///     stage-1 candidate. Those characters are valid URL characters, so they never terminate
///     stage 1; stage 2 removes them only when trailing.
///     </description>
///   </item>
/// </list>
/// <para>
/// Only HTTP and HTTPS absolute URLs (any host) are recognized. Non-HTTP schemes
/// (<c>ssh</c>, <c>file</c>, <c>git</c>) and relative paths are never matched.
/// </para>
/// <para>
/// A recognized candidate is redacted when it carries userinfo (everything before the last raw
/// <c>@</c> of the authority is dropped) or a query parameter whose decoded name equals
/// <c>token</c> (case-insensitively; EVERY occurrence is removed). A fragment is dropped only
/// from a URL that is otherwise redacted — a URL with no credential-bearing component is
/// returned completely unchanged. Host text is preserved exactly; the only case-insensitive
/// comparisons are the scheme and the decoded query-parameter name.
/// </para>
/// </remarks>
public static class GitUrlRedactor
{
    /// <summary>The lower-case HTTP scheme prefix recognized by stage 1.</summary>
    private const string HttpScheme = "http://";

    /// <summary>The lower-case HTTPS scheme prefix recognized by stage 1.</summary>
    private const string HttpsScheme = "https://";

    /// <summary>The decoded query-parameter name treated as a credential.</summary>
    private const string TokenParameterName = "token";

    /// <summary>The punctuation stripped from the END of a stage-1 candidate by stage 2.</summary>
    private const string TrailingPunctuation = ".,;:!?";

    /// <summary>
    /// Returns <paramref name="text"/> with every credential-bearing HTTP/HTTPS URL redacted.
    /// </summary>
    /// <param name="text">Arbitrary text that may embed repository URLs. May be null or empty.</param>
    /// <returns>
    /// The text with userinfo and <c>token</c> query parameters removed from every recognized
    /// URL. Text containing no credential-bearing URL is returned unchanged (reference-identical).
    /// </returns>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        StringBuilder? builder = null;
        var copied = 0;
        var index = 0;

        while (index < text.Length)
        {
            var schemeLength = MatchScheme(text, index);
            if (schemeLength == 0)
            {
                index++;
                continue;
            }

            // Stage 1: structural scan from just after the scheme.
            var candidateEnd = ScanCandidate(text, index + schemeLength);

            // Stage 1b: a candidate never swallows a LATER http(s):// occurrence. Because
            // `,`, `:`, `/`, `?`, `#` and friends are deliberately accepted during stage 1,
            // two adjacent URLs separated only by accepted characters would otherwise scan as
            // ONE candidate and only the first authority would be redacted — leaving the
            // second URL's credential intact. Scanning therefore starts at EVERY occurrence:
            // the candidate is cut at the next one, which is then processed as its own
            // candidate on a subsequent iteration.
            var nestedStart = FindNestedScheme(text, index + schemeLength, candidateEnd);
            var truncated = nestedStart >= 0;
            if (truncated)
                candidateEnd = nestedStart;

            // Stage 2: trailing punctuation cleanup on the candidate ONLY.
            var trimmedEnd = TrimTrailingPunctuation(text, index + schemeLength, candidateEnd);

            var redacted = RedactUrl(text[index..trimmedEnd], schemeLength, truncated);
            if (redacted is not null)
            {
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, index - copied);
                builder.Append(redacted);
                copied = trimmedEnd;
            }

            // Continue after the full stage-1 candidate: the stripped punctuation is ordinary
            // surrounding text and can never start another URL. Any nested occurrence is not
            // skipped — stage 1b already cut the candidate short at it.
            index = candidateEnd;
        }

        if (builder is null)
            return text;

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    /// <summary>
    /// Stage 1b. Returns the index of the FIRST nested <c>http://</c> / <c>https://</c>
    /// occurrence that starts strictly after <paramref name="searchFrom"/> and before
    /// <paramref name="end"/>, or <c>-1</c> when the candidate contains no nested occurrence.
    /// </summary>
    /// <remarks>
    /// Every occurrence must start its own candidate, otherwise a second URL embedded in the
    /// first candidate's accepted-character run (<c>…/r,https://u:s@b.example/r</c>,
    /// <c>…?next=https://u:s@b.example/r</c>, <c>…#https://u:s@b.example/r</c>) would never be
    /// examined and its credential would survive. Cutting the candidate here is safe: the
    /// nested occurrence becomes the next iteration's candidate, so no text is skipped.
    /// </remarks>
    /// <param name="text">The full text being scanned.</param>
    /// <param name="searchFrom">Index just past the current candidate's scheme.</param>
    /// <param name="end">The stage-1 candidate end.</param>
    private static int FindNestedScheme(string text, int searchFrom, int end)
    {
        for (var i = searchFrom; i < end; i++)
        {
            if (MatchScheme(text, i) != 0)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Returns the length of the HTTP/HTTPS scheme prefix at <paramref name="index"/>, or 0 when
    /// no scheme starts there. The comparison is case-insensitive.
    /// </summary>
    private static int MatchScheme(string text, int index)
    {
        var rest = text.AsSpan(index);
        if (rest.StartsWith(HttpsScheme, StringComparison.OrdinalIgnoreCase))
            return HttpsScheme.Length;
        if (rest.StartsWith(HttpScheme, StringComparison.OrdinalIgnoreCase))
            return HttpScheme.Length;
        return 0;
    }

    /// <summary>
    /// Stage 1. Scans forward from <paramref name="start"/> while characters belong to the
    /// accepted URL character set, honouring counter-based bracket balancing, and returns the
    /// exclusive end index of the candidate.
    /// </summary>
    private static int ScanCandidate(string text, int start)
    {
        var round = 0;
        var square = 0;
        var curly = 0;

        var i = start;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '(':
                    round++;
                    continue;
                case '[':
                    square++;
                    continue;
                case '{':
                    curly++;
                    continue;
                case ')':
                    if (round == 0)
                        return i;
                    round--;
                    continue;
                case ']':
                    if (square == 0)
                        return i;
                    square--;
                    continue;
                case '}':
                    if (curly == 0)
                        return i;
                    curly--;
                    continue;
                default:
                    if (!IsAcceptedUrlCharacter(c))
                        return i;
                    continue;
            }
        }

        return i;
    }

    /// <summary>
    /// Whether the character belongs to the accepted URL character set: RFC 3986
    /// path/query/fragment characters (unreserved, percent, sub-delims, <c>:</c>, <c>@</c>,
    /// <c>/</c>, <c>?</c>) plus the fragment delimiter <c>#</c>. Brackets and braces are handled
    /// separately by the balancing logic in <see cref="ScanCandidate"/>.
    /// </summary>
    private static bool IsAcceptedUrlCharacter(char c) =>
        c is >= 'a' and <= 'z'
        || c is >= 'A' and <= 'Z'
        || c is >= '0' and <= '9'
        || c is '-' or '.' or '_' or '~'      // unreserved
        || c is '%'                            // pct-encoded
        || c is '!' or '$' or '&' or '\'' or '*' or '+' or ',' or ';' or '=' // sub-delims
        || c is ':' or '@' or '/' or '?' or '#';

    /// <summary>
    /// Stage 2. Returns the candidate end with all designated trailing punctuation removed.
    /// Never trims into the scheme (<paramref name="minimumEnd"/> is the index just past it).
    /// </summary>
    private static int TrimTrailingPunctuation(string text, int minimumEnd, int end)
    {
        while (end > minimumEnd && TrailingPunctuation.Contains(text[end - 1]))
            end--;
        return end;
    }

    /// <summary>
    /// Applies the redaction rules to a recognized candidate URL.
    /// </summary>
    /// <param name="url">The stage-2 candidate.</param>
    /// <param name="schemeLength">Length of the matched <c>http(s)://</c> prefix.</param>
    /// <param name="truncated">
    /// <c>true</c> when stage 1b cut this candidate short at a NESTED http(s) occurrence, so the
    /// candidate is a PREFIX of the scanned span rather than a whole URL. A truncated candidate
    /// keeps its fragment: everything after the <c>#</c> is text that leads into the nested URL
    /// and must survive verbatim, otherwise redacting <c>…/r#https://u:s@b/r</c> would delete
    /// the <c>#</c> and splice the two URLs together.
    /// </param>
    /// <returns>
    /// The redacted URL, or <c>null</c> when the URL carries no credential component and must be
    /// returned unchanged (fragment included).
    /// </returns>
    private static string? RedactUrl(string url, int schemeLength, bool truncated)
    {
        var authorityStart = schemeLength;
        var authorityEnd = authorityStart;
        while (authorityEnd < url.Length && url[authorityEnd] is not ('/' or '?' or '#'))
            authorityEnd++;

        var authority = url[authorityStart..authorityEnd];

        // Userinfo: everything before the LAST raw '@' of the authority, removed unconditionally.
        // Positional and raw — no decoding is needed or performed.
        var lastAt = authority.LastIndexOf('@');
        var userInfoRemoved = lastAt >= 0;
        if (userInfoRemoved)
            authority = authority[(lastAt + 1)..];

        var rest = url[authorityEnd..];

        // Split off the fragment at the FIRST raw '#'. For a WHOLE candidate it is DISCARDED
        // here and re-checked below: the fragment is dropped only from a URL that is otherwise
        // redacted, and an unredacted URL is returned verbatim (fragment included) by returning
        // null. For a TRUNCATED candidate the fragment is retained instead — see the parameter
        // documentation.
        var hash = rest.IndexOf('#');
        var fragment = hash >= 0 && truncated ? rest[hash..] : string.Empty;
        var pathAndQuery = hash >= 0 ? rest[..hash] : rest;

        // Split off the query at the FIRST raw '?'.
        var question = pathAndQuery.IndexOf('?');
        var path = question >= 0 ? pathAndQuery[..question] : pathAndQuery;
        var query = question >= 0 ? pathAndQuery[(question + 1)..] : null;

        var tokenRemoved = false;
        if (query is not null)
        {
            var kept = new List<string>();
            // Parameters are separated by RAW '&' only; an encoded '&' (%26) is part of a value.
            foreach (var parameter in query.Split('&'))
            {
                // Name/value split on the RAW '=' only: an encoded '=' (%3D) inside a value
                // never terminates it. A bare parameter (no '=') is all name.
                var equals = parameter.IndexOf('=');
                var name = equals >= 0 ? parameter[..equals] : parameter;

                if (string.Equals(PercentDecode(name), TokenParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    tokenRemoved = true;
                    continue;
                }

                kept.Add(parameter);
            }

            if (tokenRemoved)
            {
                // Dangling-delimiter cleanup. Removing a parameter can strand '&' separators —
                // as a leading '?&', a trailing '&', or a doubled '&&' — and a sole-token query
                // surrounded by separators (`?&token=secret&`) would otherwise leave a bare '?'
                // with an empty-but-present query. An EMPTY segment carries neither a name nor a
                // value, so it is pure delimiter noise: dropping every empty segment guarantees
                // the rewritten query never starts with '&', never contains '&&', never ends
                // with '&', and collapses to no query at all when nothing substantive remains.
                //
                // This normalization applies ONLY when this method actually rewrote the query.
                // A query that keeps all of its parameters (userinfo-only redaction) is emitted
                // byte-for-byte, preserving each retained component's original form.
                kept.RemoveAll(string.IsNullOrEmpty);
                query = kept.Count == 0 ? null : string.Join('&', kept);
            }
        }

        if (!userInfoRemoved && !tokenRemoved)
            return null;

        var builder = new StringBuilder(url.Length);
        builder.Append(url, 0, schemeLength);
        builder.Append(authority);
        builder.Append(path);
        if (query is not null)
        {
            builder.Append('?');
            builder.Append(query);
        }

        // The fragment is dropped ONLY from a WHOLE URL that is otherwise redacted — which is
        // exactly the case reached here, so `fragment` is empty unless this candidate was
        // truncated at a nested occurrence, in which case it is retained verbatim.
        builder.Append(fragment);

        return builder.ToString();
    }

    /// <summary>
    /// Percent-decodes a query-parameter NAME for identification purposes, in a single
    /// non-recursive pass. Malformed escapes are preserved verbatim (fail-safe: they simply do
    /// not match the token name), and a literal double encoding such as <c>%2526</c> decodes to
    /// <c>%26</c> and no further.
    /// </summary>
    /// <remarks>
    /// Decoding is byte-wise over ASCII, which is all that is needed to compare against
    /// <c>token</c>; multi-byte UTF-8 sequences decode to individual characters and simply fail
    /// the comparison. The decoded form is used for MATCHING only — retained components keep
    /// their original encoding.
    /// </remarks>
    private static string PercentDecode(string name)
    {
        if (!name.Contains('%'))
            return name;

        var builder = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '%'
                && i + 2 < name.Length
                && TryParseHexDigit(name[i + 1], out var high)
                && TryParseHexDigit(name[i + 2], out var low))
            {
                builder.Append((char)((high << 4) | low));
                i += 2;
                continue;
            }

            builder.Append(name[i]);
        }

        return builder.ToString();
    }

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
