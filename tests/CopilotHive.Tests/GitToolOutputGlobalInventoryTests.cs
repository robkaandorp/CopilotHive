using System.Text;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Pins that inventory matching is GLOBAL rather than per value: the longest variant at a position
/// wins whichever inventory entry it belongs to, in any caller order. Complements the 9- and
/// 10-character collision pins by covering every other prefix length. Synthetic values only.
/// </summary>
public sealed class GitToolOutputGlobalInventoryTests
{
    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    [Theory]
    [InlineData("FAKE_KEY")]
    [InlineData("FAKE_KEY_LONGER_VALUE")]
    [InlineData("a1b2c3d4e5")]
    public void AnyPrefixOfAnEncodedVariant_AsAnotherInventoryValue_StillRemovesTheWholeVariant(string value)
    {
        var encoded = B64(value);
        var covered = 0;
        for (var length = 8; length <= encoded.Length; length++)
        {
            var other = encoded[..length];
            foreach (var text in new[] { encoded, encoded.TrimEnd('=') })
            foreach (var inventory in new[] { new[] { value, other }, new[] { other, value } })
            {
                Assert.Equal(
                    "file.txt\n[redacted]\nend-marker",
                    GitToolOutputRedactor.Redact("file.txt\n" + text + "\nend-marker", inventory));
                covered++;
            }
        }

        Assert.True(covered >= 8, "The sweep must exercise several prefix lengths.");
    }

    [Fact]
    public void ChainedPrefixes_AcrossThreeInventoryValues_RemoveWholeVariantsInEveryOrder()
    {
        // A's base64 starts with B's raw value, and B's base64 starts with C's raw value.
        const string a = "FAKE_KEY_ALPHA";
        var b = B64(a)[..12];
        var c = B64(b)[..10];
        Assert.StartsWith(b, B64(a), StringComparison.Ordinal);
        Assert.StartsWith(c, B64(b), StringComparison.Ordinal);

        string[][] orders = [[a, b, c], [a, c, b], [b, a, c], [b, c, a], [c, a, b], [c, b, a]];
        foreach (var form in new[] { B64(a), B64(a).TrimEnd('='), B64(b), B64(b).TrimEnd('='), a, b, c })
        foreach (var order in orders)
        {
            Assert.Equal(
                "file.txt\n[redacted]\nend-marker",
                GitToolOutputRedactor.Redact("file.txt\n" + form + "\nend-marker", order));
        }
    }

    [Fact]
    public void AdjacentDistinctSecrets_KeepTheirOwnPlaceholders()
    {
        Assert.Equal(
            "[redacted][redacted]",
            GitToolOutputRedactor.Redact("FAKE_ONE1FAKE_TWO2", ["FAKE_ONE1", "FAKE_TWO2"]));
    }

    [Fact]
    public void Base64OfPercentEscapedValue_RemainsAnUnchangedDoubleEncodingControl()
    {
        const string value = "FAKE/a+bZQ";
        var percent = string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));
        var doubleEncoded = B64(percent);

        Assert.Equal(doubleEncoded, GitToolOutputRedactor.Redact(doubleEncoded, [value]));
    }

    [Fact]
    public void ShortInventoryValues_AreStillNotExactReplaced()
    {
        Assert.Equal("abcdefg", GitToolOutputRedactor.Redact("abcdefg", ["abcdefg"]));
    }
}
