using System.Text;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Focused tests for the ANSI-SGR normalization <see cref="GitToolOutputRedactor"/> applies at its
/// boundary, and for the inventory/structural contracts that normalization must not weaken.
/// </summary>
/// <remarks>
/// Every vector is synthetic text in, text out: no live credentials, no network. The named coverage
/// groups are SgrGrammar, NormalizedStructures, RawMixedPercentProjection, InventoryForms,
/// UrlQueryPreservation and UrlInventoryOverlap; the real-Git blame premise and the caller
/// truncation contract are pinned at the Composer boundary instead
/// (<c>ComposerToolTests.ColoredGitRedaction.cs</c>).
/// </remarks>
[Collection(GitOutputInventoryCollection.Name)]
public sealed class GitToolOutputRedactorSgrTests
{
    /// <summary>The bare reset, the shortest complete SGR sequence.</summary>
    private const string Reset = "\u001b[m";

    /// <summary>A one-level UTF-8 percent encoding of every byte of <paramref name="value"/>.</summary>
    private static string Percent(string value) =>
        string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));

    // ── SgrGrammar ───────────────────────────────────────────────────────────

    /// <summary>
    /// A COMPLETE sequence — <c>ESC '['</c>, zero or more ASCII parameter bytes
    /// <c>0-9 : ; &lt; = &gt; ?</c>, and a final lowercase <c>m</c> — is removed, including the
    /// bare reset and including several sequences in a row.
    /// </summary>
    [Theory]
    [InlineData("a\u001b[mb", "ab")]                              // bare reset ESC[m
    [InlineData("\u001b[31mred\u001b[m", "red")]                  // ordinary colour, then reset
    [InlineData("\u001b[38;5;208morange", "orange")]              // extended-colour form
    [InlineData("x\u001b[0;1:2<3=4>5?6my", "xy")]                 // every allowed parameter byte
    [InlineData("\u001b[m\u001b[31m\u001b[1;32mZ", "Z")]          // adjacent sequences
    [InlineData("plain", "plain")]                               // nothing to remove
    public void SgrGrammar_CompleteSequences_AreRemoved(string input, string expected) =>
        // No inventory and no credential syntax, so the only transform left is normalization.
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));

    /// <summary>
    /// Anything that is not a COMPLETE SGR sequence survives byte-for-byte: a non-<c>m</c> final
    /// byte, an OSC introducer, an unterminated sequence, a bare ESC, a sequence interrupted by a
    /// line break, and parameter positions filled with a Unicode digit LOOKALIKE rather than an
    /// ASCII parameter byte.
    /// </summary>
    [Theory]
    [InlineData("\u001b[31c")]                 // CSI whose final byte is not 'm'
    [InlineData("\u001b[2J")]                  // erase-display: final byte 'J'
    [InlineData("\u001b]0;title\u0007")]       // OSC, not CSI
    [InlineData("\u001b[31")]                  // unterminated
    [InlineData("\u001b")]                     // bare ESC
    [InlineData("\u001b[31\nm")]               // interrupted by a line break
    [InlineData("\u001b[\u0663\u0661m")]       // Arabic-Indic digits, not ASCII parameter bytes
    [InlineData("\u001b[\uff13m")]             // fullwidth digit, not an ASCII parameter byte
    [InlineData("\u001b[31;1")]                // parameter bytes but no final 'm'
    [InlineData("\u001b[?25l")]                // '?' is allowed, but the final byte is 'l'
    public void SgrGrammar_UnsupportedIncompleteAndLookalikeEscapes_AreUnchanged(string input) =>
        Assert.Equal(input, GitToolOutputRedactor.Redact(input));

    /// <summary>
    /// Normalization removes sequences only: CR, LF, tab, vertical tab and the surrounding ordinary
    /// text stay exactly as they were, even when a sequence sits beside them.
    /// </summary>
    [Fact]
    public void SgrGrammar_CarriageReturnsLineFeedsTabsAndOrdinaryText_ArePreserved()
    {
        const string input = "line1\r\n\tcolored: \u001b[31mvalue\u001b[m\r\nplain\ttab\nend";
        const string expected = "line1\r\n\tcolored: value\r\nplain\ttab\nend";

        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));
    }

    /// <summary>
    /// Complete sequences are removed even when they are interleaved, in ONE string, with
    /// unsupported and incomplete controls and with CR/LF/tab. The exact output pins WHICH bytes
    /// disappear: the surviving escapes are byte-for-byte the input ones, and no whitespace or
    /// ordinary character outside a removal span moves.
    /// </summary>
    [Fact]
    public void SgrGrammar_MixedCompleteAndUnsupportedControls_OnlyCompleteSpansAreRemoved()
    {
        const string input =
            "head\r\n\t\u001b[31mX\u001b[31c\u001b[?25lY\u001b[31\nmZ\u001b[\u0663\u0661mW\u001b[1;32m!\u001b[m\nend";
        // ESC[31m, ESC[1;32m and ESC[m are complete; ESC[31c, ESC[?25l, the LF-interrupted
        // ESC[31\nm and the Arabic-Indic-digit lookalike are not.
        const string expected =
            "head\r\n\tX\u001b[31c\u001b[?25lY\u001b[31\nmZ\u001b[\u0663\u0661mW!\nend";

        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));
    }

    /// <summary>
    /// An uppercase final byte, a tab or BEL inside the parameter run, and a non-CSI introducer
    /// are all outside the closed grammar. Each is preserved exactly while an adjacent COMPLETE
    /// sequence in the same string is still removed — so a broad "strip everything from ESC to m"
    /// implementation cannot pass both halves.
    /// </summary>
    [Fact]
    public void SgrGrammar_UpperCaseFinalByteAndNonSgrIntroducers_ArePreservedBesideCompleteSpans()
    {
        const string input =
            "\u001b[31Mkeep\u001b[1;32Mkeep\u001b(Bkeep\u001b=keep"
            + "\u001b[?25Mkeep\u001b[31\tmkeep\u001b[31\u0007mkeep\u001b[mdone";
        const string expected =
            "\u001b[31Mkeep\u001b[1;32Mkeep\u001b(Bkeep\u001b=keep"
            + "\u001b[?25Mkeep\u001b[31\tmkeep\u001b[31\u0007mkeepdone";

        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));
    }

    // ── NormalizedStructures ─────────────────────────────────────────────────

    /// <summary>
    /// With NO inventory, decoration on a structural marker or inside a credential must not stop
    /// the structural scans: the assignment, the folded header (including its continuation line),
    /// the <c>ssh://</c> password and the scp password are all redacted, and ordinary content,
    /// tabs and line breaks survive.
    /// </summary>
    [Fact]
    public void NormalizedStructures_DecoratedCredentials_RedactWithoutInventory()
    {
        const string input =
            "file.txt\n"
            + "\u001b[33mGH_TOKEN\u001b[m=ghp_decorated_assignment\n"
            + "Authorization: Bearer \u001b[36mZGVjb3JhdGVk\u001b[m\n"
            + "\t\u001b[36mZm9sZGVk\u001b[m\n"
            + "ssh://deploy:\u001b[31mpa55\u001b[m@h.invalid/r\n"
            + "deploy:pa\u001b[31m55@h.invalid:org/r\n"
            + "ordinary content survives\n";

        const string expected =
            "file.txt\n"
            + "GH_TOKEN=[redacted]\n"
            + "Authorization: Bearer [redacted]\n"
            + "\t[redacted]\n"
            + "ssh://deploy:[redacted]@h.invalid/r\n"
            + "deploy:[redacted]@h.invalid:org/r\n"
            + "ordinary content survives\n";

        // No inventory is passed: only normalization plus the structural grammar can remove these.
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));
    }

    // ── RawMixedPercentProjection ────────────────────────────────────────────

    /// <summary>
    /// A decorated one-level escape of an eligible inventory value. The raw text holds
    /// <c>%41B</c> + <c>ESC[31m</c> + <c>CD</c>; removing the sequence makes <c>%41BCD</c>, which is
    /// NOT a literal match for the visible spelling <c>ABCD</c>. Only projecting the RAW match
    /// through the exact SGR offset map removes it whole, and the neighboring text must survive.
    /// </summary>
    [Fact]
    public void RawMixedPercentProjection_DecoratedEscape_WholeMatchRedactsAndNeighboursSurvive()
    {
        const string secret = "AB\u001b[31mCD";
        const string input = "%41B\u001b[31mCD";

        // The inventory value is eligible on its ORIGINAL length (11 characters).
        Assert.True(secret.Length >= GitToolOutputRedactor.MinimumInventoryValueLength);

        Assert.Equal("before [redacted] after", GitToolOutputRedactor.Redact("before " + input + " after", [secret]));

        // Premise: once the sequence is gone the text is `%41BCD`, which no literal or one-level
        // reading of the visible spelling `ABCD` covers — the projection is what closes the gap.
        Assert.Equal("before %41BCD after", GitToolOutputRedactor.Redact("before %41BCD after", [secret]));
    }

    /// <summary>
    /// An undecorated inventory containing only <c>ABCD</c> is ineligible; the eligible decorated
    /// spelling contributes only the literal derived <c>ABCD</c>, not a reading of <c>%41BCD</c>.
    /// With the decorated inventory AND input, the raw match redacts exactly its projected extent,
    /// leaving even the characters immediately after it intact.
    /// </summary>
    [Fact]
    public void RawMixedPercentProjection_UndecoratedInventoryAndTrailingNeighbour_ProveExactProjection()
    {
        const string decoratedInput = "before %41B\u001b[31mCD after";

        // `ABCD` is too short to participate as an original inventory value, even though
        // `%41BCD` would be a one-level percent reading of it.
        Assert.Equal("before %41BCD after", GitToolOutputRedactor.Redact(decoratedInput, ["ABCD"]));

        // The normalized form itself is the non-matching control from the row above, stated again
        // against the decorated inventory so the two directions cannot be confused.
        Assert.Equal("before %41BCD after", GitToolOutputRedactor.Redact("before %41BCD after", ["AB\u001b[31mCD"]));

        // Exact mapped span: the trailing `XYZ` is outside the projected extent and survives.
        Assert.Equal(
            "before [redacted]XYZ after",
            GitToolOutputRedactor.Redact("before %41B\u001b[31mCDXYZ after", ["AB\u001b[31mCD"]));
    }

    // ── InventoryForms ───────────────────────────────────────────────────────

    /// <summary>
    /// The decorated RAW spelling of an eligible value is redacted, and a plain value keeps the
    /// three forms the inventory has always supported: its raw text, its one-level percent reading,
    /// and its standard base64 in both the padded and the unpadded spelling.
    /// </summary>
    [Fact]
    public void InventoryForms_DecoratedRawSpellingAndOriginalEncodings_AreRedacted()
    {
        const string decorated = "decorated\u001b[31mvalue";
        Assert.Equal("file [redacted] end", GitToolOutputRedactor.Redact("file " + decorated + " end", [decorated]));

        const string plain = "originalFORMsecret!";
        var padded = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        var unpadded = padded.TrimEnd('=');
        Assert.NotEqual(padded, unpadded);

        Assert.Equal("file [redacted] end", GitToolOutputRedactor.Redact("file " + plain + " end", [plain]));
        Assert.Equal("file [redacted] end", GitToolOutputRedactor.Redact("file " + Percent(plain) + " end", [plain]));
        Assert.Equal("file [redacted] end", GitToolOutputRedactor.Redact("file " + padded + " end", [plain]));
        Assert.Equal("file [redacted] end", GitToolOutputRedactor.Redact("file " + unpadded + " end", [plain]));
    }

    /// <summary>
    /// The encodings of the ORIGINAL decorated raw value are still supported even though the value
    /// now also contributes a derived visible spelling: its one-level percent reading (fully escaped
    /// and mixed literal/escape), its padded standard base64 and its unpadded form all redact, with
    /// both neighbours intact. This is the positive control for the "no derived encodings" test —
    /// the same inventory must still cover the encodings of the value the caller actually holds.
    /// </summary>
    [Fact]
    public void InventoryForms_EncodingsOfTheOriginalDecoratedValue_AreStillRedacted()
    {
        const string decorated = "decorated\u001b[31mvalue";
        var padded = Convert.ToBase64String(Encoding.UTF8.GetBytes(decorated));
        var unpadded = padded.TrimEnd('=');
        Assert.NotEqual(padded, unpadded);

        Assert.Equal("alpha ([redacted]) omega", GitToolOutputRedactor.Redact("alpha (" + decorated + ") omega", [decorated]));
        Assert.Equal("alpha ([redacted]) omega", GitToolOutputRedactor.Redact("alpha (" + Percent(decorated) + ") omega", [decorated]));
        Assert.Equal("alpha ([redacted]) omega", GitToolOutputRedactor.Redact("alpha (%64ecorated\u001b[31mvalue) omega", [decorated]));
        Assert.Equal("alpha ([redacted]) omega", GitToolOutputRedactor.Redact("alpha (" + padded + ") omega", [decorated]));
        Assert.Equal("alpha ([redacted]) omega", GitToolOutputRedactor.Redact("alpha (" + unpadded + ") omega", [decorated]));
    }

    /// <summary>
    /// An eligible decorated value contributes its VISIBLE spelling as an ordinal, literal-only
    /// match, even when that spelling is shorter than the inventory bound, while a short value that
    /// was never decorated stays below the bound and is not replaced.
    /// </summary>
    [Fact]
    public void InventoryForms_ShortDerivedSpelling_IsMatchedButUnchangedShortRawValueIsNot()
    {
        // 12 raw characters (eligible) whose visible spelling is only 7.
        const string decorated = "SHORT\u001b[31mAB";
        Assert.Equal("file [redacted] end", GitToolOutputRedactor.Redact("file SHORTAB end", [decorated]));

        // The same 7 characters supplied as the INVENTORY VALUE are ineligible: nothing changes.
        Assert.Equal("file SHORTAB end", GitToolOutputRedactor.Redact("file SHORTAB end", ["SHORTAB"]));
    }

    /// <summary>
    /// An eligible value that is nothing but complete sequences normalizes to the EMPTY spelling;
    /// an empty spelling is never matched, because it would match at every position.
    /// </summary>
    [Fact]
    public void InventoryForms_EmptyDerivedSpelling_IsNotMatched()
    {
        const string onlySequences = "\u001b[31m\u001b[0m";
        Assert.True(onlySequences.Length >= GitToolOutputRedactor.MinimumInventoryValueLength);

        Assert.Equal("file anything at all end", GitToolOutputRedactor.Redact("file anything at all end", [onlySequences]));
    }

    /// <summary>
    /// No encoded variant of a DERIVED visible spelling is generated: its one-level percent
    /// spelling and its standard base64 (padded and unpadded) gain no coverage. Only the literal
    /// visible spelling itself is matched.
    /// </summary>
    [Fact]
    public void InventoryForms_DerivedPercentAndBase64Spellings_GainNoCoverage()
    {
        const string decorated = "visible/decorated\u001b[31mvalue";
        const string visible = "visible/decoratedvalue";

        var derivedPadded = Convert.ToBase64String(Encoding.UTF8.GetBytes(visible));
        var derivedUnpadded = derivedPadded.TrimEnd('=');
        Assert.NotEqual(derivedPadded, derivedUnpadded);

        // The literal visible spelling IS matched (that is the only derived form that is).
        Assert.Equal("file [redacted] end", GitToolOutputRedactor.Redact("file " + visible + " end", [decorated]));

        // Its encoded variants are not.
        Assert.Equal("file " + Percent(visible) + " end", GitToolOutputRedactor.Redact("file " + Percent(visible) + " end", [decorated]));
        Assert.Equal("file " + derivedPadded + " end", GitToolOutputRedactor.Redact("file " + derivedPadded + " end", [decorated]));
        Assert.Equal("file " + derivedUnpadded + " end", GitToolOutputRedactor.Redact("file " + derivedUnpadded + " end", [decorated]));
    }

    // ── UrlQueryPreservation ─────────────────────────────────────────────────

    /// <summary>
    /// A colour sequence inside the HTTP scheme means the RAW text is not a URL at all, so the URL
    /// rewrite can only run on the normalized text. The credential query assignment is intact in
    /// the raw text, and the non-credential <c>ref=topic</c> parameter must survive: a structural
    /// scan of the RAW text would have claimed it as part of the assignment's value.
    /// </summary>
    [Fact]
    public void UrlQueryPreservation_SgrSplitHttpScheme_KeepsNonCredentialQueryParameter()
    {
        const string input = "see https:/\u001b[31m/h.invalid/r?token=ghp_query_credential&ref=topic end";

        // Premise: the raw text carries the sequence AND still holds the whole query assignment.
        Assert.Contains("\u001b[31m", input, StringComparison.Ordinal);
        Assert.Contains("token=ghp_query_credential&ref=topic end", input, StringComparison.Ordinal);

        Assert.Equal("see https://h.invalid/r?ref=topic end", GitToolOutputRedactor.Redact(input));
    }

    /// <summary>
    /// The URL rewrite must run on the NORMALIZED text and BEFORE the structural assignment scan.
    /// An assignment scan of the RAW text would claim `token=ghp_query_credential&amp;ref=topic&amp;x=1`
    /// (or the reordered query) as one value, and its raw coordinates would not even describe the
    /// rewritten text. Exact outputs pin that every non-credential parameter survives.
    /// </summary>
    [Theory]
    [InlineData("?token=ghp_query_credential&ref=topic", "?ref=topic")]
    [InlineData("?token=ghp_query_credential&ref=topic&x=1", "?ref=topic&x=1")]
    [InlineData("?ref=topic&token=ghp_query_credential", "?ref=topic")]
    [InlineData("?ref=topic&token=ghp_query_credential&x=1", "?ref=topic&x=1")]
    public void UrlQueryPreservation_SgrSplitHttpScheme_KeepsEveryNonCredentialParameter(
        string rawQuery, string expectedQuery)
    {
        var input = "see https:/\u001b[31m/h.invalid/r" + rawQuery + " end";

        // Premise: the raw text really holds the intact credential assignment and the neighbour
        // parameter, and the sequence really splits the scheme.
        Assert.Contains(rawQuery, input, StringComparison.Ordinal);
        Assert.Contains("https:/\u001b[31m/", input, StringComparison.Ordinal);

        Assert.Equal("see https://h.invalid/r" + expectedQuery + " end", GitToolOutputRedactor.Redact(input));
    }

    /// <summary>
    /// A known value whose own extent crosses BOTH an SGR deletion and the URL rewrite leaves no
    /// credential residue and loses no neighboring text, from either match direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row 1 spells the value's first character as the one-level escape <c>%41</c>, so the value is
    /// only found on the RAW text: the normalized <c>%41BCD…</c> is not a literal match for the
    /// visible <c>ABCD…</c>. Under the real transform its span is therefore projected through the
    /// exact SGR offset map and then through the URL alignment, which must cover the value across
    /// the <c>u:pw@</c> the rewrite deletes. It is a NO-REGRESSION row rather than a discriminator:
    /// the same raw spelling is still present when normalization is disabled, so a
    /// normalization-disabled build removes it through the pre-existing percent + URL-alignment
    /// path. Row 2 is the discriminator.
    /// </para>
    /// <para>
    /// Row 2 splits the value's own <c>https://</c> scheme with a sequence, so the raw text holds
    /// no match of any form and only normalization can expose it; its span is then mapped through
    /// the URL alignment directly.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(
        "before %41B\u001b[31mCDhttps://u:pw@h.invalid/r after",
        "AB\u001b[31mCDhttps://u:pw@h.invalid/r",
        "%41B\u001b[31mCDhttps://u:pw@h.invalid/r")]
    [InlineData(
        "before tokhttps:/\u001b[31m/u:pw@h.invalid/r after",
        "tokhttps://u:pw@h.invalid/r",
        "tokhttps:/\u001b[31m/u:pw@h.invalid/r")]
    public void UrlInventoryOverlap_KnownValueSpanningSgrDeletionAndUrlRewrite_LeavesNoResidue(
        string input, string secret, string spellingInInput)
    {
        // Premise — BOTH deletion maps are genuinely in play, not just one:
        //   * the value is eligible on its ORIGINAL length,
        //   * the input carries the row's exact spelling,
        //   * a complete SGR sequence sits inside (or beside) that spelling, and
        //   * a credentialed HTTP URL lies within the spanned extent.
        Assert.True(secret.Length >= GitToolOutputRedactor.MinimumInventoryValueLength);
        Assert.Contains(spellingInInput, input, StringComparison.Ordinal);
        Assert.Contains("\u001b[", spellingInInput, StringComparison.Ordinal);
        Assert.Contains("u:pw@", spellingInInput, StringComparison.Ordinal);

        // The whole value becomes one placeholder, so no fragment of it — and no fragment of the
        // URL's userinfo — survives, and both neighboring words are intact.
        Assert.Equal("before [redacted] after", GitToolOutputRedactor.Redact(input, [secret]));
    }

    /// <summary>
    /// Exact-output neighbours for BOTH overlap directions: punctuation immediately outside the
    /// known value's extent survives, and no fragment of the value, the userinfo or the deleted
    /// userinfo leaks. Row 1 is the RAW percent projection (`%41` + SGR + `CD` + URL); row 2 has
    /// no form of the value in the raw text at all, so only the NORMALIZED match can start it.
    /// </summary>
    [Theory]
    [InlineData(
        "alpha (%41B\u001b[31mCDhttps://u:pw@h.invalid/r) omega",
        "AB\u001b[31mCDhttps://u:pw@h.invalid/r",
        "alpha ([redacted]) omega")]
    [InlineData(
        "alpha (tokhttps:/\u001b[31m/u:pw@h.invalid/r) omega",
        "tokhttps://u:pw@h.invalid/r",
        "alpha ([redacted]) omega")]
    public void UrlInventoryOverlap_ExactNeighboursAndNoResidue_ArePreserved(
        string input, string secret, string expected)
    {
        // Premise: the input is decorated AND credentialed, and the neighbour text is present.
        Assert.Contains("\u001b[31m", input, StringComparison.Ordinal);
        Assert.Contains("u:pw@", input, StringComparison.Ordinal);
        Assert.Contains("alpha (", input, StringComparison.Ordinal);
        Assert.Contains(") omega", input, StringComparison.Ordinal);

        var output = GitToolOutputRedactor.Redact(input, [secret]);
        Assert.Equal(expected, output);

        // Independently assert on the PRODUCTION output that no fragment of the value, its
        // userinfo, or the decoration survives outside the exact placeholder and neighbours.
        Assert.DoesNotContain("u:pw@", output, StringComparison.Ordinal);
        Assert.DoesNotContain("%41B", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", output, StringComparison.Ordinal);
        Assert.Equal(1, output.Split("[redacted]", StringSplitOptions.None).Length - 1);
    }

    // ── ProjectionEmpty / ProjectionStartInside / ProjectionEndInside ─────────

    /// <summary>
    /// A raw match is a RAW coordinate span; it is projected through the exact SGR offset map, and
    /// the three tests below pin the three branches of that projection independently: nothing
    /// survives, the match STARTS inside a removed sequence, and the match ENDS inside one.
    /// Each proves its raw-match premise on the production inputs, so normalized matching (which
    /// would never see an escape) cannot be what satisfies the assertion.
    /// </summary>
    /// <remarks>
    /// A removed sequence is only partly part of a raw match when the inventory value itself
    /// contains a PREFIX or SUFFIX of a sequence (<c>[31m</c> without its ESC, or <c>ESC[3</c>
    /// without its <c>1m</c>). Neither such value changes under removal, because it holds no
    /// COMPLETE sequence, so no derived visible spelling exists either: only the raw match can
    /// find it.
    /// </remarks>
    private static string WithoutSgr(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[[0-9:;<=>?]*m", string.Empty);

    /// <summary>
    /// ProjectionEmpty. An ELIGIBLE value made only of complete sequences matches its own raw
    /// spelling in the input, but every character of that match is removed, so the projection is
    /// empty: no placeholder is inserted and the neighbors survive byte-for-byte.
    /// </summary>
    [Fact]
    public void ProjectionEmpty_EligibleAllSgrRawMatch_InsertsNoPlaceholder()
    {
        const string secret = "\u001b[31m\u001b[0m";
        const string input = "left \u001b[31m\u001b[0m right";

        // Premises: eligible on its ORIGINAL length; the RAW text matches it literally; the
        // NORMALIZED text holds nothing of it.
        Assert.True(secret.Length >= GitToolOutputRedactor.MinimumInventoryValueLength);
        Assert.Contains(secret, input, StringComparison.Ordinal);
        Assert.Equal("left  right", WithoutSgr(input));
        Assert.DoesNotContain("\u001b", WithoutSgr(input), StringComparison.Ordinal);

        Assert.Equal("left  right", GitToolOutputRedactor.Redact(input, [secret]));
    }

    /// <summary>
    /// ProjectionStartInside. The raw match starts INSIDE a removed sequence (at its <c>[</c>, past
    /// the ESC), so only the characters that survive removal are redacted: the text before the
    /// sequence and the text after the matched extent are untouched.
    /// </summary>
    [Fact]
    public void ProjectionStartInside_RawMatchBeginningInsideRemovedSequence_RedactsOnlySurvivingExtent()
    {
        const string secret = "[31mSECRET";
        const string input = "left \u001b[31mSECRETVALUE right";

        // Premises: eligible (9 chars); no complete sequence inside it, so no derived spelling;
        // the RAW text contains it, starting after the ESC; the NORMALIZED text does not.
        Assert.True(secret.Length >= GitToolOutputRedactor.MinimumInventoryValueLength);
        Assert.Equal(secret, WithoutSgr(secret));
        Assert.Contains(secret, input, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, WithoutSgr(input), StringComparison.Ordinal);
        Assert.Equal("left SECRETVALUE right", WithoutSgr(input));

        // Only `SECRET` survived inside the match: `VALUE` and both neighbors remain.
        Assert.Equal("left [redacted]VALUE right", GitToolOutputRedactor.Redact(input, [secret]));
    }

    /// <summary>
    /// ProjectionEndInside. The raw match ends INSIDE a removed sequence (after its <c>[3</c>), so
    /// only the surviving characters before it are redacted and both neighbors survive.
    /// </summary>
    [Fact]
    public void ProjectionEndInside_RawMatchEndingInsideRemovedSequence_RedactsOnlySurvivingExtent()
    {
        const string secret = "SECRETVAL\u001b[3";
        const string input = "left PRE-SECRETVAL\u001b[31m right";

        // Premises: eligible (12 chars); the value's trailing `ESC[3` is an INCOMPLETE sequence, so
        // it is unchanged by removal and has no derived spelling; the RAW text contains it, ending
        // before the `1m`; the NORMALIZED text does not.
        Assert.True(secret.Length >= GitToolOutputRedactor.MinimumInventoryValueLength);
        Assert.Equal(secret, WithoutSgr(secret));
        Assert.Contains(secret, input, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, WithoutSgr(input), StringComparison.Ordinal);
        Assert.Equal("left PRE-SECRETVAL right", WithoutSgr(input));

        Assert.Equal("left PRE-[redacted] right", GitToolOutputRedactor.Redact(input, [secret]));
    }

    // ── Alignment* / RewrittenOnly ────────────────────────────────────────────

    /// <summary>
    /// The URL alignment is expensive on an AMBIGUOUS userinfo/host pair (the deleted `hunter:` and
    /// the kept `hunter` host are indistinguishable without probing) followed by a long ordinary
    /// tail. The comparison is on ALLOCATED BYTES of a warmed call on this thread, never elapsed time.
    /// </summary>
    private const string AmbiguousUrl = "see https://hunter:hunter@hunter.invalid/r";

    private static readonly string LongTail = new('h', 200_000);

    private static (string Output, long Bytes) MeasureWarmed(string input, string[] inventory)
    {
        var output = string.Empty;
        for (var i = 0; i < 3; i++)
            output = GitToolOutputRedactor.Redact(input, inventory);

        var before = GC.GetAllocatedBytesForCurrentThread();
        output = GitToolOutputRedactor.Redact(input, inventory);
        return (output, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// Ceiling for a call that must NOT build the alignment. Dispatch base measured ~2.4 MB here and
    /// the eager iteration-1 code ~61 MB; the ceiling sits between them with wide margin on both sides.
    /// </summary>
    private const long NoAlignmentCeilingBytes = 8_000_000;

    /// <summary>AlignmentNoMatch. An eligible value that matches NOTHING never pays for the alignment.</summary>
    [Fact]
    public void AlignmentNoMatch_UnrelatedEligibleValue_DoesNotBuildAlignment()
    {
        var input = AmbiguousUrl + LongTail;
        var (output, bytes) = MeasureWarmed(input, ["UNRELATED_ELIGIBLE_VALUE"]);

        Assert.Equal("see https://hunter.invalid/r" + LongTail, output);
        Assert.True(bytes < NoAlignmentCeilingBytes, $"Allocated {bytes} bytes; the alignment was built without a match.");
    }

    /// <summary>
    /// AlignmentEmptyProjection. A raw match that projects to NOTHING must not request an alignment
    /// either: the all-SGR value occurs in the input, the URL rewrite is present, and still no
    /// alignment is built.
    /// </summary>
    [Fact]
    public void AlignmentEmptyProjection_AllSgrRawMatch_DoesNotBuildAlignment()
    {
        const string secret = "\u001b[31m\u001b[0m";
        var input = AmbiguousUrl + " \u001b[31m\u001b[0m" + LongTail;
        Assert.Contains(secret, input, StringComparison.Ordinal);

        var (output, bytes) = MeasureWarmed(input, [secret]);

        Assert.Equal("see https://hunter.invalid/r " + LongTail, output);
        Assert.True(bytes < NoAlignmentCeilingBytes, $"Allocated {bytes} bytes; an empty projection built the alignment.");
    }

    /// <summary>
    /// AlignmentNoRewrite. With SGR removed but NO URL rewrite, projected spans need no alignment
    /// and the normalized text (identical to the rewritten text) is not collected a second time.
    /// </summary>
    [Fact]
    public void AlignmentNoRewrite_SgrOnly_ProjectsWithoutAlignment()
    {
        const string secret = "AB\u001b[31mCD";
        var input = "left %41B\u001b[31mCD right " + LongTail;

        var (output, bytes) = MeasureWarmed(input, [secret]);

        Assert.Equal("left [redacted] right " + LongTail, output);
        Assert.True(bytes < NoAlignmentCeilingBytes, $"Allocated {bytes} bytes without any URL rewrite.");
    }

    [Fact]
    public void AlignmentNoRewrite_NormalizedMatches_AreCollectedOnlyOnce()
    {
        const string secret = "VISIBLE_MATCH";
        const string unrelated = "UNRELATED_ELIGIBLE_VALUE";
        // Force a second scan (if present) to materialize thousands of duplicate matches. The
        // decorated input has no raw match; both inputs yield the same visible text with no URL
        // rewrite. The no-match runs subtract the cost of constructing the SGR text and offset map.
        const int repeats = 8192;
        var plain = string.Concat(Enumerable.Repeat(secret + "|", repeats));
        var decorated = string.Concat(Enumerable.Repeat("VISIBLE_" + Reset + "MATCH|", repeats));
        var expected = string.Concat(Enumerable.Repeat("[redacted]|", repeats));
        Assert.DoesNotContain(secret, decorated, StringComparison.Ordinal);
        Assert.Equal(plain, WithoutSgr(decorated));

        var (plainControl, plainControlBytes) = MeasureWarmed(plain, [unrelated]);
        var (decoratedControl, decoratedControlBytes) = MeasureWarmed(decorated, [unrelated]);
        var (plainOutput, plainBytes) = MeasureWarmed(plain, [secret]);
        var (decoratedOutput, decoratedBytes) = MeasureWarmed(decorated, [secret]);

        Assert.Equal(plain, plainControl);
        Assert.Equal(plain, decoratedControl);
        Assert.Equal(expected, plainOutput);
        Assert.Equal(expected, decoratedOutput);
        var excess = (decoratedBytes - decoratedControlBytes) - (plainBytes - plainControlBytes);
        // Allow hundreds of KB of slack; the duplicate-scan build allocated over 1 MB extra.
        // Exact byte equality is not a requirement.
        Assert.True(excess < 512_000, $"The decorated call allocated {excess} extra match bytes without a URL rewrite.");
    }

    /// <summary>
    /// RewrittenOnly. A value that spans the text the URL rewrite DELETED occurs only in the
    /// REWRITTEN text, which the existing rewritten-text matching still finds, with no pre-rewrite
    /// span and therefore no alignment.
    /// </summary>
    [Fact]
    public void RewrittenOnly_ValueFoundOnlyInRewrittenText_StillRedactsWithoutAlignment()
    {
        const string secret = "https://hunter.invalid/r";
        var input = AmbiguousUrl + LongTail;
        Assert.DoesNotContain(secret, input, StringComparison.Ordinal);
        Assert.Equal("see " + secret + LongTail, GitUrlRedactor.Redact(input));

        var (output, bytes) = MeasureWarmed(input, [secret]);

        Assert.Equal("see [redacted]" + LongTail, output);
        Assert.True(bytes < NoAlignmentCeilingBytes, $"Allocated {bytes} bytes for a rewritten-only match.");
    }

    /// <summary>
    /// AlignmentNeeded. When a non-empty pre-rewrite span exists the alignment IS used, from each
    /// source on its own and from both together, and the output is exact. The URL rewrite deletes
    /// userinfo inside each value's own extent, so a wrong or missing alignment leaves residue.
    /// </summary>
    [Theory]
    // raw-only: the one-level escape is found only on the raw text.
    [InlineData("before %41B\u001b[31mCDhttps://u:pw@h.invalid/r after", "AB\u001b[31mCDhttps://u:pw@h.invalid/r")]
    // normalized-only: the sequence splits the value's own scheme, so the raw text holds no match.
    [InlineData("before tokhttps:/\u001b[31m/u:pw@h.invalid/r after", "tokhttps://u:pw@h.invalid/r")]
    public void AlignmentNeeded_EachSourceAlone_ProducesExactOutput(string input, string secret)
    {
        Assert.Contains("u:pw@", input, StringComparison.Ordinal);
        Assert.Equal("before [redacted] after", GitToolOutputRedactor.Redact(input, [secret]));
    }

    /// <summary>AlignmentNeeded (both sources). One call needs the alignment for BOTH sources and for two values.</summary>
    [Fact]
    public void AlignmentNeeded_BothSourcesTogether_ProducesExactOutput()
    {
        const string rawOnly = "AB\u001b[31mCDhttps://u:pw@h.invalid/r";
        const string normalizedOnly = "tokhttps://v:qw@g.invalid/s";
        const string input =
            "first %41B\u001b[31mCDhttps://u:pw@h.invalid/r mid tokhttps:/\u001b[31m/v:qw@g.invalid/s last";

        // Only the first value's mixed-percent spelling occurs in raw input; stripping SGR
        // exposes the second value but does not make the first visible spelling match literally.
        var normalized = WithoutSgr(input);
        Assert.Contains(rawOnly.Replace("AB", "%41B", StringComparison.Ordinal), input, StringComparison.Ordinal);
        Assert.DoesNotContain(WithoutSgr(rawOnly), normalized, StringComparison.Ordinal);
        Assert.DoesNotContain(normalizedOnly, input, StringComparison.Ordinal);
        Assert.Contains(normalizedOnly, normalized, StringComparison.Ordinal);

        Assert.Equal("first [redacted] mid [redacted] last", GitToolOutputRedactor.Redact(input, [rawOnly, normalizedOnly]));
    }

    /// <summary>
    /// AlignmentNeeded (single creation). When both sources need the alignment on the SAME text it
    /// is built ONCE: allocation with a raw AND a normalized match stays well under twice the cost of
    /// the same input with only one of them.
    /// </summary>
    [Fact]
    public void AlignmentNeeded_BothSources_ShareOneAlignmentAttempt()
    {
        const string rawOnly = "AB\u001b[31mCDhttps://u:pw@h.invalid/r";
        const string normalizedOnly = "tokhttps://v:qw@g.invalid/s";
        // Keep the entire input identical across the three calls. Both inventory extents cross
        // deletions, meaning neither can be recovered by scanning rewritten text alone.
        var input = AmbiguousUrl
            + " %41B\u001b[31mCDhttps://u:pw@h.invalid/r mid tokhttps:/\u001b[31m/v:qw@g.invalid/s " + LongTail;
        var (rawOut, rawBytes) = MeasureWarmed(input, [rawOnly]);
        var (normalizedOut, normalizedBytes) = MeasureWarmed(input, [normalizedOnly]);
        var (bothOut, bothBytes) = MeasureWarmed(input, [rawOnly, normalizedOnly]);

        Assert.Equal("see https://hunter.invalid/r [redacted] mid tokhttps://g.invalid/s " + LongTail, rawOut);
        Assert.Equal("see https://hunter.invalid/r %41BCDhttps://h.invalid/r mid [redacted] " + LongTail, normalizedOut);
        Assert.Equal("see https://hunter.invalid/r [redacted] mid [redacted] " + LongTail, bothOut);
        Assert.True(bothBytes < Math.Min(rawBytes, normalizedBytes) * 1.5,
            $"Two sources allocated {bothBytes} bytes vs {rawBytes} (raw) and {normalizedBytes} (normalized).");
    }
}
