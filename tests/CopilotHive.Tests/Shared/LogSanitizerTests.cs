using CopilotHive.Services;

// NOTE: the file lives under tests/CopilotHive.Tests/Shared/ (it covers a CopilotHive.Shared
// type) but deliberately declares the ROOT test namespace. Declaring
// `CopilotHive.Tests.Shared` would make the relative name `Shared` inside the existing
// `CopilotHive.Tests` files resolve to the test namespace instead of `CopilotHive.Shared`,
// breaking their `Shared.Grpc.*` references.
namespace CopilotHive.Tests;

/// <summary>
/// Removal-proof tests for <see cref="LogSanitizer.SanitizeText"/> — the free-text counterpart
/// of <see cref="LogSanitizer.SanitizePath"/>. Every unsafe-character class (C0 controls
/// including NUL/tab/newline/ESC, DEL, the C1 range, U+0085 NEL, U+2028 and U+2029) is asserted
/// individually, so deleting the replacement (or narrowing the classification) fails the suite.
/// The parity theory pins the contract to <see cref="LogSanitizer.SanitizePath"/>: identical
/// inputs must produce identical outputs from BOTH methods.
/// </summary>
public sealed class LogSanitizerTests
{
    /// <summary>
    /// Every unsafe-character class, one row each: the raw character and the text embedding it.
    /// Each row is surrounded by safe text so a mutant that returned a constant, an empty
    /// string or a fully-stripped value is also caught.
    /// </summary>
    public static TheoryData<string, string> UnsafeCharacterCases => new()
    {
        // C0 controls.
        { "a\u0000b", "a?b" },   // NUL
        { "a\tb", "a?b" },       // tab
        { "a\nb", "a?b" },       // line feed
        { "a\rb", "a?b" },       // carriage return
        { "a\u000Bb", "a?b" },   // vertical tab
        { "a\u000Cb", "a?b" },   // form feed
        { "a\u001Bb", "a?b" },   // ESC
        { "a\u001Fb", "a?b" },   // last C0
        // DEL.
        { "a\u007Fb", "a?b" },
        // C1 range.
        { "a\u0080b", "a?b" },   // first C1
        { "a\u009Fb", "a?b" },   // last C1
        // NEL — inside the C1 range, asserted explicitly because it is its own line breaker.
        { "a\u0085b", "a?b" },
        // Unicode line/paragraph separators.
        { "a\u2028b", "a?b" },
        { "a\u2029b", "a?b" },
    };

    [Theory]
    [MemberData(nameof(UnsafeCharacterCases))]
    public void SanitizeText_ReplacesEveryUnsafeCharacterClass(string input, string expected)
    {
        Assert.Equal(expected, LogSanitizer.SanitizeText(input));

        // The input really did carry the unsafe character — a row whose character were safe
        // would pass vacuously.
        Assert.NotEqual(input, LogSanitizer.SanitizeText(input));
    }

    /// <summary>
    /// The WHOLE unsafe domain, character by character: every code point classified unsafe by
    /// <see cref="LogSanitizer.IsLogUnsafe"/> must be replaced, and every other one preserved.
    /// </summary>
    [Fact]
    public void SanitizeText_MatchesIsLogUnsafeOverTheFullBmpRange()
    {
        for (var code = 0; code <= 0x2100; code++)
        {
            var c = (char)code;
            var expected = LogSanitizer.IsLogUnsafe(c) ? LogSanitizer.Placeholder : c;

            Assert.Equal(expected.ToString(), LogSanitizer.SanitizeText(c.ToString()));
        }
    }

    [Fact]
    public void SanitizeText_NullInput_ReturnsNull() =>
        Assert.Null(LogSanitizer.SanitizeText(null!));

    [Fact]
    public void SanitizeText_EmptyInput_ReturnsEmpty() =>
        Assert.Equal(string.Empty, LogSanitizer.SanitizeText(string.Empty));

    /// <summary>
    /// Safe characters — letters, digits, punctuation, legal non-ASCII and leading/trailing
    /// spaces — survive untouched. A mutant that stripped, trimmed or normalized anything
    /// beyond the unsafe set fails here.
    /// </summary>
    public static TheoryData<string> SafeTextCases =>
    [
        "plain text",
        "Path/to/file-name_1.txt",
        "0123456789",
        "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~",
        "café-résumé",
        "漢字テスト",
        "  leading and trailing  ",
        "\u00A0non-breaking space",
        "emoji \U0001F600 pair",
    ];

    [Theory]
    [MemberData(nameof(SafeTextCases))]
    public void SanitizeText_PreservesSafeCharacters(string input) =>
        Assert.Equal(input, LogSanitizer.SanitizeText(input));

    /// <summary>
    /// Only the unsafe characters change: a mixed string keeps every safe character in place
    /// and its length, so a mutant that dropped (rather than replaced) unsafe characters fails.
    /// </summary>
    [Fact]
    public void SanitizeText_ReplacesInPlaceWithoutChangingLength()
    {
        const string input = "  before\nmiddle\u0000after\u2028é  ";
        const string expected = "  before?middle?after?é  ";

        var actual = LogSanitizer.SanitizeText(input);

        Assert.Equal(expected, actual);
        Assert.Equal(input.Length, actual.Length);
    }

    /// <summary>
    /// Long inputs take the heap-allocated branch (the span buffer switches over 512 chars) —
    /// the replacement must behave identically there.
    /// </summary>
    [Fact]
    public void SanitizeText_LongInput_TakesHeapBufferAndStillReplaces()
    {
        var input = new string('a', 600) + "\n" + new string('b', 600);
        var expected = new string('a', 600) + "?" + new string('b', 600);

        Assert.Equal(expected, LogSanitizer.SanitizeText(input));
    }

    /// <summary>
    /// PARITY with <see cref="LogSanitizer.SanitizePath"/>: the two methods share the unsafe
    /// set, the placeholder and the null/empty passthrough, so identical inputs must produce
    /// identical outputs.
    /// </summary>
    public static TheoryData<string> ParityCases =>
    [
        "",
        "plain",
        "  spaced  ",
        "a\nb",
        "a\tb\u0000c",
        "a\u007Fb\u0085c",
        "a\u2028b\u2029c",
        "café/漢字\u001Bfile.txt",
        new string('x', 600) + "\r\n" + new string('y', 600),
    ];

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void SanitizeText_IsIdenticalToSanitizePath(string input) =>
        Assert.Equal(LogSanitizer.SanitizePath(input), LogSanitizer.SanitizeText(input));

    [Fact]
    public void SanitizeText_NullParityWithSanitizePath() =>
        Assert.Equal(LogSanitizer.SanitizePath(null!), LogSanitizer.SanitizeText(null!));
}
