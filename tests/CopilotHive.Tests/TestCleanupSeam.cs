using System.Reflection;
using Xunit.Sdk;

namespace CopilotHive.Tests;

/// <summary>
/// A test-only seam over the shared temp-directory cleanup helper
/// (<see cref="TestHelpers.ForceDeleteDirectory"/>).
/// <para>
/// The fixtures that clean up a temp directory through the shared helper hand it this seam's
/// <see cref="Delete"/> / <see cref="Wait"/> overrides. BOTH ARE NULL IN EVERY NORMAL RUN, so the
/// helper takes its ordinary production path (clear read-only attributes, delete, retry a
/// transient failure, fail loudly). A test can set them to force the helper's loud failure
/// DETERMINISTICALLY — which a real filesystem cannot do on Linux, where a one-shot directory
/// delete virtually always succeeds — and then observe how the fixture reports that failure.
/// </para>
/// <para>
/// The seam is per fixture INSTANCE (not static), so two fixtures running in parallel can never
/// observe each other's override.
/// </para>
/// </summary>
internal sealed class TestCleanupSeam
{
    /// <summary>
    /// Replaces the shared helper's directory delete. Null = the helper's own delete.
    /// </summary>
    internal Action<string>? Delete { get; set; }

    /// <summary>
    /// Replaces the shared helper's bounded backoff wait. Null = the helper's real wait. Set it to
    /// a no-op so a forced-failure test does not spend the helper's 3.6 s of backoff.
    /// </summary>
    internal Action<int>? Wait { get; set; }
}

/// <summary>
/// Captures the diagnostic messages a test emits through
/// <c>TestContext.Current.SendDiagnosticMessage(...)</c>, so a test can assert on the CONTENT of
/// a best-effort cleanup's diagnostic (the directory path and the exception) instead of only on
/// the fact that a diagnostic was sent.
/// <para>
/// xUnit v3 exposes the diagnostic channel to the test code but does not expose the sink itself,
/// so the sink is swapped for a capturing one for the lifetime of this object and restored on
/// <see cref="Dispose"/>. The swap is strictly local to the current test: the original sink is
/// put back even when the test fails.
/// </para>
/// </summary>
internal sealed class DiagnosticMessageCapture : IDisposable
{
    private readonly ITestContext _context;
    private readonly FieldInfo _sinkField;
    private readonly object? _originalSink;
    private readonly List<string> _messages = new();

    /// <summary>
    /// Installs the capturing sink on the current test context.
    /// </summary>
    public DiagnosticMessageCapture()
    {
        _context = TestContext.Current;
        _sinkField = FindDiagnosticSinkField(_context.GetType())
            ?? throw new InvalidOperationException(
                "The xUnit v3 test context no longer exposes a 'diagnosticMessageSink' field, so "
                + "diagnostic messages cannot be captured. Update DiagnosticMessageCapture to the "
                + "new xUnit internals.");
        _originalSink = _sinkField.GetValue(_context);
        _sinkField.SetValue(_context, new CapturingSink(_messages));
    }

    /// <summary>
    /// The diagnostic messages captured so far, in order.
    /// </summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <summary>
    /// Restores the original diagnostic sink.
    /// </summary>
    public void Dispose()
    {
        _sinkField.SetValue(_context, _originalSink);
    }

    /// <summary>
    /// Finds the diagnostic-sink field on the test context type or any of its base types.
    /// </summary>
    private static FieldInfo? FindDiagnosticSinkField(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(
                "diagnosticMessageSink",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field is not null)
                return field;
        }

        return null;
    }

    /// <summary>
    /// A minimal message sink that records the diagnostic messages sent to it.
    /// </summary>
    private sealed class CapturingSink : IMessageSink
    {
        private readonly List<string> _messages;

        public CapturingSink(List<string> messages) => _messages = messages;

        public bool OnMessage(IMessageSinkMessage message)
        {
            if (message is IDiagnosticMessage diagnostic)
                _messages.Add(diagnostic.Message);

            return true;
        }
    }
}
