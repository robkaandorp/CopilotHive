using System.Text;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>Iteration-8 repair probes. Every credential-shaped value is a synthetic non-secret stand-in.</summary>
public sealed class GitToolOutputFinalRepairTests
{
    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));
    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    [Theory]
    [InlineData("Proxy-Authorization", false, "\n")]
    [InlineData("proxy-authorization", false, "\n")]
    [InlineData("PROXY-AUTHORIZATION", false, "\n")]
    [InlineData("Proxy-Authorization", true, "\n")]
    [InlineData("proxy-authorization", true, "\n")]
    [InlineData("PROXY-AUTHORIZATION", true, "\n")]
    [InlineData("Proxy-Authorization", false, "\r\n")]
    [InlineData("proxy-authorization", false, "\r\n")]
    [InlineData("PROXY-AUTHORIZATION", false, "\r\n")]
    [InlineData("Proxy-Authorization", true, "\r\n")]
    [InlineData("proxy-authorization", true, "\r\n")]
    [InlineData("PROXY-AUTHORIZATION", true, "\r\n")]
    public void ProxyHeaders_CaseOuterOrderAndAlternation_RedactEveryCredential(string proxy, bool proxyOuter, string newline)
    {
        const string known = "FAKE%KNOWN%";
        var first = proxyOuter ? proxy : "Authorization";
        var second = proxyOuter ? "Authorization" : proxy;
        var input = $"file.txt{newline}{first}: Bearer FAKE_ONE \t {newline}"
            + $"\t{Uri.EscapeDataString(known)}\t  {newline}"
            + $"  {second} : bAsIc FAKE_TWO== \t\t{newline}"
            + $" \t{first}: Bearer FAKE_THREE  \t {newline}"
            + $"\t  {second}: Basic FAKE_FOUR=={newline}  FAKE_FIVE=={newline}end-marker";
        var expected = $"file.txt{newline}{first}: Bearer [redacted] \t {newline}"
            + $"\t[redacted]\t  {newline}"
            + $"  {second} : bAsIc [redacted] \t\t{newline}"
            + $" \t{first}: Bearer [redacted]  \t {newline}"
            + $"\t  {second}: Basic [redacted]{newline}  [redacted]{newline}end-marker";
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input, [known]));
    }

    private static void AssertAllInventoryForms(string value)
    {
        // Independent expected text, not a second invocation of the production redactor.
        var mixed = string.Concat(value.Select((c, i) => i % 2 == 0 ? $"%{(byte)c:X2}" : c.ToString()));
        string[] variants = [value, Percent(value), Percent(value).ToLowerInvariant(), Uri.EscapeDataString(value), mixed, B64(value), B64(value).TrimEnd('=')];
        foreach (var variant in variants)
            Assert.Equal("file.txt | [redacted] | end-marker", GitToolOutputRedactor.Redact("file.txt | " + variant + " | end-marker", [value]));
        var twice = Percent(value).Replace("%", "%25", StringComparison.Ordinal);
        Assert.Equal("file.txt | " + twice + " | end-marker", GitToolOutputRedactor.Redact("file.txt | " + twice + " | end-marker", [value]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void PercentInventory_EveryPosition_RawEncodedAndBase64AreWholeMatches(int position)
    {
        var value = "FAKEVALUE".Insert(position, "%");
        Assert.EndsWith("==", B64(value));
        AssertAllInventoryForms(value);
    }

    [Theory]
    [InlineData("FAKE%%%key%")]
    [InlineData("%%FAKE_key%%")]
    [InlineData("FAKE%25%25%")]
    [InlineData("%FAKE+/=%")]
    public void PercentInventory_MultiplePercentsAndMixedEscapeChoices_AreWholeMatches(string value)
    {
        AssertAllInventoryForms(value);
        var count = value.Count(c => c == '%');
        for (var mask = 0; mask < (1 << count); mask++)
        {
            var percentIndex = 0;
            var variant = string.Concat(value.Select(c => c == '%'
                ? ((mask & (1 << percentIndex++)) != 0 ? "%25" : "%")
                : c.ToString()));
            Assert.Equal("file.txt | [redacted] | end-marker", GitToolOutputRedactor.Redact("file.txt | " + variant + " | end-marker", [value]));
        }
    }

    [Theory]
    [InlineData("header-first")]
    [InlineData("header-continuation")]
    [InlineData("proxy-header")]
    [InlineData("assignment")]
    [InlineData("quoted-assignment")]
    [InlineData("nested-assignment")]
    public void PercentInventory_InsideHeadersAndAssignments_DoesNotBlindStructuralLayer(string site)
    {
        const string value = "FAKE%+/=%";
        var encoded = Uri.EscapeDataString(value);
        var text = site switch
        {
            "header-first" => $"Authorization: Bearer {encoded}\n\tFAKE_CONTINUATION==",
            "header-continuation" => $"Authorization: Basic FAKE_FIRST==\n\t{encoded}\n  FAKE_LAST==",
            "proxy-header" => $"Proxy-Authorization: Bearer {encoded}\n\tAuthorization: Basic FAKE_LAST==",
            "assignment" => $"token = {encoded}",
            "quoted-assignment" => $"password = '{encoded}'",
            "nested-assignment" => $"credential: password: token: {encoded}",
            _ => throw new InvalidOperationException("Unknown cross-layer site"),
        };
        // The unstructured 'ordinary' line separately proves the inventory is active; a structural
        // pattern alone could otherwise make the cross-layer inventory assertion vacuous.
        var input = "file.txt\nordinary " + encoded + "\n" + text + "\nend-marker";
        var actual = GitToolOutputRedactor.Redact(input, [value]);
        Assert.StartsWith("file.txt\nordinary [redacted]\n", actual);
        Assert.EndsWith("\nend-marker", actual);
        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        foreach (var marker in new[] { value, encoded, "FAKE_CONTINUATION", "FAKE_FIRST", "FAKE_LAST" })
            Assert.False(actual.Contains(marker, StringComparison.Ordinal), "Synthetic credential survived cross-layer repair probe.");
        // No partial '%25' residue may survive after an inventory or structural replacement.
        Assert.DoesNotContain("[redacted]25", actual);
    }

    [Theory]
    [InlineData("FAKE%???", "/", "=")]
    [InlineData("FAKE%~~~", "+", "=")]
    [InlineData("FAKE%???.x", "/", "==")]
    [InlineData("FAKE%~~~.x", "+", "==")]
    public void PercentInventory_Base64AlphabetAndBothPaddingWidths_AreActuallyExercised(string value, string alphabetMarker, string padding)
    {
        Assert.Contains(alphabetMarker, B64(value));
        Assert.EndsWith(padding, B64(value));
        Assert.NotEqual(B64(value), B64(value).TrimEnd('='));
        AssertAllInventoryForms(value);
    }
}
