using System.Globalization;
using System.Reflection;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>Tests for the <c>PrintBanner()</c> static method in <c>Program.cs</c>.</summary>
public sealed class PrintBannerTests
{
    /// <summary>
    /// Verifies that <c>PrintBanner()</c> writes a line starting with "Started at " and
    /// ending with " UTC", and that the embedded timestamp is parseable as
    /// <c>yyyy-MM-dd HH:mm:ss</c>.
    /// </summary>
    [Fact]
    public void PrintBanner_WritesUtcTimestampLine()
    {
        // Arrange — redirect stdout so we can capture what PrintBanner writes.
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            // Act — invoke the compiler-generated local function via reflection.
            // Top-level local functions are emitted as "<<Main>$>g__<name>|0_N" on the Program type.
            var method = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name.Contains("PrintBanner", StringComparison.Ordinal));

            Assert.NotNull(method);
            method!.Invoke(null, null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Assert — find the timestamp line in the captured output.
        var output = writer.ToString();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var timestampLine = Array.Find(
            lines,
            l => l.TrimEnd().StartsWith("Started at ", StringComparison.Ordinal));

        Assert.NotNull(timestampLine);
        Assert.EndsWith(" UTC", timestampLine.TrimEnd(), StringComparison.Ordinal);

        // Extract the datetime portion between "Started at " and " UTC".
        var trimmed = timestampLine.Trim();
        var dateTimePart = trimmed["Started at ".Length..^" UTC".Length];

        var parsed = DateTime.ParseExact(
            dateTimePart,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture);

        Assert.True(parsed > DateTime.MinValue, "Timestamp must be a valid non-default DateTime.");
    }
}
