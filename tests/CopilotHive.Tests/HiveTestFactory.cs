using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CopilotHive.Tests;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that boots the real CopilotHive application
/// using an isolated temporary directory for state (SQLite database, metrics) so tests do not
/// touch production storage paths such as <c>/app/state</c>.
/// </summary>
/// <remarks>
/// The <c>STATE_DIR</c> environment variable must be set before the entry point reads it (before
/// <c>WebApplicationFactory</c> triggers host creation via <c>CreateClient()</c>), so it is set
/// eagerly in the constructor.
/// </remarks>
public sealed class HiveTestFactory : WebApplicationFactory<Program>
{
    private readonly string _stateDir =
        Path.Combine(Path.GetTempPath(), $"copilothive-test-{Guid.NewGuid():N}");

    /// <summary>
    /// Initialises the factory and points <c>STATE_DIR</c> at a temporary directory.
    /// </summary>
    public HiveTestFactory()
    {
        Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("STATE_DIR", null);

        if (!disposing || !Directory.Exists(_stateDir))
            return;

        // Best-effort cleanup — SQLite may still hold file locks briefly after host shutdown.
        // Files are in the OS temp directory and will be cleaned up eventually.
        try
        {
            Directory.Delete(_stateDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
