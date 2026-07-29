using Microsoft.Extensions.Logging;

namespace CopilotHive.Dashboard.Tests;

public class DashboardLogSinkTests
{
    [Fact]
    public void LogError_WithException_CapturesExceptionTypeAndMessage()
    {
        var sink = new DashboardLogSink();
        var provider = new DashboardLoggerProvider(sink);
        var logger = provider.CreateLogger("TestCategory");
        var exception = new InvalidOperationException("Something went wrong");

        logger.LogError(exception, "some message");

        var entry = sink.GetRecent(1)[0];
        Assert.Contains("some message", entry.Message);
        Assert.Contains("InvalidOperationException: Something went wrong", entry.Message);
    }

    [Fact]
    public void LogError_WithoutException_CapturesOnlyMessage()
    {
        var sink = new DashboardLogSink();
        var provider = new DashboardLoggerProvider(sink);
        var logger = provider.CreateLogger("TestCategory");

        logger.LogError("some message");

        var entry = sink.GetRecent(1)[0];
        Assert.Equal("some message", entry.Message);
    }

    [Fact]
    public void LogError_WithInnerException_CapturesInnerExceptionDetails()
    {
        var sink = new DashboardLogSink();
        var provider = new DashboardLoggerProvider(sink);
        var logger = provider.CreateLogger("TestCategory");
        var inner = new ArgumentException("inner issue");
        var exception = new InvalidOperationException("outer issue", inner);

        logger.LogError(exception, "some message");

        var entry = sink.GetRecent(1)[0];
        Assert.Contains("some message", entry.Message);
        Assert.Contains("InvalidOperationException: outer issue", entry.Message);
        Assert.Contains("Inner: ArgumentException: inner issue", entry.Message);
    }

    [Fact]
    public void LogWarning_WithException_AppendsExceptionDetails()
    {
        var sink = new DashboardLogSink();
        var provider = new DashboardLoggerProvider(sink);
        var logger = provider.CreateLogger("TestCategory");
        var exception = new InvalidOperationException("warning exception");

        logger.LogWarning(exception, "warning message");

        var entry = sink.GetRecent(1)[0];
        Assert.Contains("warning message", entry.Message);
        Assert.Contains("InvalidOperationException: warning exception", entry.Message);
    }
}
