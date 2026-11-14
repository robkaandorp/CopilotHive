using System.Text;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>Iteration-9 cross-layer and encoded-prefix probes. All values are synthetic stand-ins.</summary>
public sealed class GitToolOutputInventoryInteractionTests
{
    private const string Known = "FAKE_KNOWN_KEY";
    private const string Second = "FAKE_SECOND_KEY";
    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));

    public static TheoryData<string, string, bool> UserinfoCases
    {
        get
        {
            var rows = new TheoryData<string, string, bool>();
            foreach (var site in new[] { "user", "password", "password-prefix", "password-suffix", "username-prefix", "username-suffix", "multiple", "host" })
            {
                rows.Add(site, "ssh", false);
                rows.Add(site, "ssh", true);
                rows.Add(site, "scp", false);
            }
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(UserinfoCases))]
    public void SshScp_InventoryAtEveryPosition_DoesNotLeaveStructuralPasswordTails(string site, string protocol, bool port)
    {
        var (user, password, host, safeUser, safeHost) = site switch
        {
            "user" => (Known, "FAKE_PASSWORD", "h.invalid", "[redacted]", "h.invalid"),
            "password" => ("u", Known, "h.invalid", "u", "h.invalid"),
            "password-prefix" => ("u", Known + "FAKE_TAIL", "h.invalid", "u", "h.invalid"),
            "password-suffix" => ("u", "FAKE_TAIL" + Known, "h.invalid", "u", "h.invalid"),
            "username-prefix" => (Known + "FAKE_USER", "FAKE_PASSWORD", "h.invalid", "[redacted]FAKE_USER", "h.invalid"),
            "username-suffix" => ("FAKE_USER" + Known, "FAKE_PASSWORD", "h.invalid", "FAKE_USER[redacted]", "h.invalid"),
            "multiple" => (Known + Second, Known + "FAKE_TAIL" + Second, Known + Second, "[redacted][redacted]", "[redacted][redacted]"),
            "host" => ("u", "FAKE_PASSWORD", Known, "u", "[redacted]"),
            _ => throw new InvalidOperationException("Unknown position"),
        };
        string input;
        string expected;
        if (protocol == "ssh")
        {
            var suffix = port ? ":2222/r" : "/r";
            input = $"ssh://{user}:{password}@{host}{suffix}";
            expected = $"ssh://{safeUser}:[redacted]@{safeHost}{suffix}";
        }
        else if (protocol == "scp")
        {
            input = $"{user}:{password}@{host}:org/r";
            expected = $"{safeUser}:[redacted]@{safeHost}:org/r";
        }
        else throw new InvalidOperationException("Unknown transport");
        Assert.Equal("file.txt | " + expected + " | end-marker", GitToolOutputRedactor.Redact("file.txt | " + input + " | end-marker", [Known, Second]));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void NewlineAdjacentInventoryAndCompleteUrls_AllLinesRemainIndependent(string newline)
    {
        // Complete URLs on both sides, not invalid newline-folded userinfo or multiline secrets.
        var input = $"file.txt{newline}{Known}{newline}ssh://{Known}:FAKE_PASSWORD@h.invalid/r{newline}"
            + $"u:FAKE_TAIL{Known}@h.invalid:org/r{newline}password: FAKE_PASSWORD{newline}end-marker";
        var expected = $"file.txt{newline}[redacted]{newline}ssh://[redacted]:[redacted]@h.invalid/r{newline}"
            + $"u:[redacted]@h.invalid:org/r{newline}password: [redacted]{newline}end-marker";
        var actual = GitToolOutputRedactor.Redact(input, [Known]);
        Assert.Equal(expected, actual);
        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
    }

    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData(" ")]
    public void ChainedSshAndScpUrls_AllInventoryAndStructuralPartsAreSanitized(string separator)
    {
        string[] urls = [$"ssh://{Known}:FAKE_PASSWORD@h.invalid/r", $"ssh://u:{Known}FAKE_TAIL@h.invalid:2222/r", $"u:FAKE_TAIL{Known}@h.invalid:org/r"];
        string[] safe = ["ssh://[redacted]:[redacted]@h.invalid/r", "ssh://u:[redacted]@h.invalid:2222/r", "u:[redacted]@h.invalid:org/r"];
        Assert.Equal("file.txt " + string.Join(separator, safe) + " end-marker", GitToolOutputRedactor.Redact("file.txt " + string.Join(separator, urls) + " end-marker", [Known]));
    }

    [Theory]
    [InlineData("https://FAKE_KNOWN_KEY@h.invalid/r", "https://h.invalid/r")]
    [InlineData("http://u:FAKE_KNOWN_KEYFAKE_TAIL@h.invalid/r?ref=topic", "http://h.invalid/r?ref=topic")]
    [InlineData("https://FAKE_KNOWN_KEY:FAKE_PASSWORD@h.invalid/r", "https://h.invalid/r")]
    [InlineData("https://u:FAKE_TAILFAKE_KNOWN_KEY@h.invalid/r", "https://h.invalid/r")]
    [InlineData("https://u:FAKE_PASSWORD@h.invalid/r?ref=FAKE_KNOWN_KEY&token=FAKE_QUERY&x=1", "https://h.invalid/r?ref=[redacted]&x=1")]
    [InlineData("https://h.invalid/r?token=FAKE_KNOWN_KEYFAKE_TAIL&ref=topic", "https://h.invalid/r?ref=topic")]
    [InlineData("https://h.invalid/r?ref=FAKE_KNOWN_KEY&x=1", "https://h.invalid/r?ref=[redacted]&x=1")]
    // Inherited composition contract: HTTP username-only userinfo is removed, not preserved.
    [InlineData("https://ordinary-user@h.invalid/r", "https://h.invalid/r")]
    public void HttpComposition_InventoryPlaceholdersDoNotBlindUserinfoOrQuery(string url, string expected)
    {
        Assert.Equal("file.txt " + expected + " end-marker", GitToolOutputRedactor.Redact("file.txt " + url + " end-marker", [Known]));
    }

    public static TheoryData<int> SelfPrefixLengths
    {
        get
        {
            var rows = new TheoryData<int>();
            foreach (var length in Enumerable.Range(8, 121).Concat(new[] { 255, 256, 257, 1023, 1024, 1025 }))
                rows.Add(length);
            return rows;
        }
    }

    private static string SyntheticSelfPrefix(int length)
    {
        // Prefix of the base64 fixed-point word, constructed only from a literal 'V'. Capping the
        // scratch word bounds allocation; each tested value's property is explicitly asserted below.
        var word = "V";
        for (var i = 0; i < 64; i++)
        {
            word = B64(word);
            if (word.Length > 4096) word = word[..4096];
        }
        return word[..length];
    }

    [Theory]
    [MemberData(nameof(SelfPrefixLengths))]
    public void Base64SelfPrefix_EveryEnumeratedLength_RawPaddedAndUnpaddedAreWholeMatches(int length)
    {
        var value = SyntheticSelfPrefix(length);
        var encoded = B64(value);
        Assert.StartsWith(value, encoded, StringComparison.Ordinal);
        Assert.True(encoded.Length > value.Length, "Fixture must have an encoded tail.");
        foreach (var variant in new[] { value, encoded, encoded.TrimEnd('=') })
            Assert.Equal("file.txt | [redacted] | end-marker", GitToolOutputRedactor.Redact("file.txt | " + variant + " | end-marker", [value]));
    }

    [Theory]
    [InlineData("FAKE", 1)]
    [InlineData("FAKE_KEY", 0)]
    [InlineData("FAKE_VALUE", 2)]
    public void Base64ValuedInventory_PaddingPhasesRemainBounded(string seed, int expectedPadding)
    {
        var value = B64(seed); // Inventory itself is valid base64, not an actual credential.
        Assert.Equal(seed, Encoding.UTF8.GetString(Convert.FromBase64String(value)));
        var encoded = B64(value);
        Assert.Equal(expectedPadding, encoded.Length - encoded.TrimEnd('=').Length);
        foreach (var variant in new[] { value, encoded, encoded.TrimEnd('=') })
            Assert.Equal("file.txt | [redacted] | end-marker", GitToolOutputRedactor.Redact("file.txt | " + variant + " | end-marker", [value]));
    }

    [Theory]
    [InlineData("FAKE%KEY%")]
    [InlineData("FAKE%+/=xy")]
    [InlineData("FAKE_KEY%25")]
    public void PercentAndBase64Inventory_SingleLayersRedact_ComposedEncodingIsAControl(string value)
    {
        foreach (var variant in new[] { value, Percent(value), Uri.EscapeDataString(value), B64(value), B64(value).TrimEnd('=') })
            Assert.Equal("file.txt | [redacted] | end-marker", GitToolOutputRedactor.Redact("file.txt | " + variant + " | end-marker", [value]));
        // base64(percent(value)) composes two layers and is explicitly outside bounded decoding.
        var composed = B64(Percent(value));
        Assert.Equal("file.txt | " + composed + " | end-marker", GitToolOutputRedactor.Redact("file.txt | " + composed + " | end-marker", [value]));
    }

    [Theory]
    [InlineData(9, false, false)]
    [InlineData(9, false, true)]
    [InlineData(9, true, false)]
    [InlineData(9, true, true)]
    [InlineData(10, false, false)]
    [InlineData(10, false, true)]
    [InlineData(10, true, false)]
    [InlineData(10, true, true)]
    public void Base64PrefixCollisionBetweenInventories_WholeEncodedValueWins(int prefixLength, bool unpadded, bool reverseInventory)
    {
        const string value = "FAKE_KEY";
        var fullEncoding = B64(value);
        var otherInventoryValue = fullEncoding[..prefixLength];
        Assert.True(otherInventoryValue.Length > value.Length, "Collision must exercise longest-raw-value-first ordering.");
        Assert.StartsWith(otherInventoryValue, fullEncoding, StringComparison.Ordinal);
        var text = unpadded ? fullEncoding.TrimEnd('=') : fullEncoding;
        string[] inventory = reverseInventory ? [value, otherInventoryValue] : [otherInventoryValue, value];
        Assert.Equal("file.txt\n[redacted]\nend-marker", GitToolOutputRedactor.Redact("file.txt\n" + text + "\nend-marker", inventory));
    }
}
