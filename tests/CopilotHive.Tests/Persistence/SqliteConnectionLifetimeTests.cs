using System.Data;

using CopilotHive.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CopilotHive.Tests.Persistence;

/// <summary>
/// Contract of the CALLER-OWNED in-memory SQLite connections the persistence tests create: EF Core
/// never closes an externally supplied connection, so a test that creates one owns its lifetime
/// and must dispose it deterministically — otherwise the in-memory database (and its connection)
/// survives the test and leaks into the process.
/// <para>
/// The timezone test
/// (<c>CompletionReceipts_FirstStoredAtUtc_LocalInputUsesOffsetConversion_NotKindRelabeling</c>)
/// and the other same-pattern tests in <see cref="CopilotHiveDbContextTests"/> were converted to
/// a <c>using</c> local. The assertions below pin the ownership semantics that make that
/// conversion necessary, so a future change back to a leaked connection is visible.
/// </para>
/// </summary>
public sealed class SqliteConnectionLifetimeTests
{
    /// <summary>
    /// The ownership fact that makes the conversion necessary: disposing an EF context that was
    /// built over a CALLER-OWNED connection leaves that connection OPEN — the context does not
    /// own it, so it does not close it.
    /// </summary>
    /// <remarks>
    /// The connection is a <c>using</c> local, so the very leak this test demonstrates in the
    /// CONTEXT's disposal cannot become a leak in the TEST: a failing assertion still releases it.
    /// </remarks>
    [Fact]
    public void CallerOwnedConnection_SurvivingTheContextDispose_StaysOpen()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var context = new CopilotHiveDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        // The context's disposal did NOT close the caller's connection — which is exactly why
        // the test that creates such a connection has to dispose it itself. Asserted BEFORE the
        // `using` local's implicit disposal, so the observation is unchanged by it.
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>
    /// The CONTRAST: a context that OWNS its connection closes it when the context is disposed.
    /// This is the shape <c>CreateEmptyDbContext</c> uses (<c>contextOwnsConnection: true</c>), so
    /// the tests that dispose the returned context also release the in-memory database.
    /// </summary>
    /// <remarks>
    /// The connection is held in a <c>try</c>/<c>finally</c> rather than a <c>using</c> local, and
    /// the <c>finally</c> is an explicit FALLBACK: it guarantees the connection is released if the
    /// body throws before the context has taken (and disposed) it — an <c>Open()</c> failure, an
    /// <c>EnsureCreated()</c> failure, or the state assertion itself failing because the ownership
    /// it asserts regressed. The state assertion runs INSIDE the <c>try</c>, i.e. BEFORE the
    /// fallback, so the observation of "the context closed it" is made on the connection exactly
    /// as the context left it; the fallback's own disposal can never mask that discrimination.
    /// (A <c>using</c> local would be equally safe for the observation — it disposes at method
    /// exit, after the context and after the assertion — the explicit <c>finally</c> is simply the
    /// form that documents the fallback role.)
    /// </remarks>
    [Fact]
    public void ContextOwnedConnection_IsClosedByTheContextDispose()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        try
        {
            connection.Open();
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(connection, contextOwnsConnection: true)
                .Options;

            using (var context = new CopilotHiveDbContext(options))
            {
                context.Database.EnsureCreated();
            }

            Assert.Equal(ConnectionState.Closed, connection.State);
        }
        finally
        {
            // Fallback only: on the healthy path the context already disposed it, and
            // SqliteConnection.Dispose is idempotent, so this second call is a no-op then.
            connection.Dispose();
        }
    }

    /// <summary>
    /// The timezone test's connection is disposed deterministically: the test creates a
    /// CALLER-OWNED connection, and a caller-owned connection is only released when its owner
    /// disposes it. The test's <c>using</c> does that on every exit path.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: the assertion is made against the AUTHORITATIVE source — the test method's
    /// own body — rather than against a re-implementation of it. Reverting the connection to a
    /// plain (non-<c>using</c>) local makes this test fail.
    /// </remarks>
    [Fact]
    public void TimezoneTest_DisposesItsCallerOwnedConnection()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Persistence", "CopilotHiveDbContextTests.cs"))));

        var method = TestSourceScanner.ExtractMethodBlock(
            code,
            "public void CompletionReceipts_FirstStoredAtUtc_LocalInputUsesOffsetConversion_NotKindRelabeling()");
        Assert.NotNull(method);

        // The connection is a `using` local, so the compiler disposes it on every exit path.
        Assert.Contains(
            "using var connection = new SqliteConnection(",
            method!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The timezone test's connection is the CALLER-OWNED overload: the context must NOT take
    /// ownership of it, because the test needs the same connection for the raw-storage read after
    /// the write context was disposed.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: switching the timezone test to the context-owns overload would close the
    /// connection when the write context is disposed, and the subsequent raw SELECT would fail.
    /// </remarks>
    [Fact]
    public void TimezoneTest_UsesTheCallerOwnedConnectionOverload()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Persistence", "CopilotHiveDbContextTests.cs"))));

        var method = TestSourceScanner.ExtractMethodBlock(
            code,
            "public void CompletionReceipts_FirstStoredAtUtc_LocalInputUsesOffsetConversion_NotKindRelabeling()");
        Assert.NotNull(method);

        // The context is built with the caller-owns overload (no contextOwnsConnection: true).
        Assert.Contains(".UseSqlite(connection)", method!, StringComparison.Ordinal);
        Assert.DoesNotContain("contextOwnsConnection", method!, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY in-memory connection the persistence tests create is released deterministically.
    /// A connection is released when EITHER of two things happens, and the sweep requires one of
    /// them for every single connection:
    /// <list type="bullet">
    /// <item>the connection is a <c>using</c> local — the test disposes it itself; or</item>
    /// <item>the connection is handed to a context built with
    /// <c>contextOwnsConnection: true</c> — the context's disposal closes it.</item>
    /// </list>
    /// A connection that satisfies neither survives the test and leaks its in-memory database
    /// into the process.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: a new <c>new SqliteConnection(...)</c> that satisfies neither rule makes
    /// this test fail, naming the offending line.
    /// </remarks>
    [Fact]
    public void EveryInMemoryConnectionInThePersistenceTests_IsReleasedDeterministically()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Persistence", "CopilotHiveDbContextTests.cs"))));

        var lines = code.Split('\n');
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineStart = offset;
            offset += line.Length + 1;

            if (!line.Contains("new SqliteConnection(", StringComparison.Ordinal))
                continue;

            // A FACTORY that RETURNS the connection transfers the ownership to its caller, which
            // disposes it in a `using` block at every call site (pinned by the call-site sweep
            // below). Such a factory is not itself an owner.
            if (IsConnectionFactory(code, lineStart))
                continue;

            // The connection is released by the test itself …
            if (line.Contains("using var connection = new SqliteConnection(", StringComparison.Ordinal)
                || line.Contains("using (var connection = new SqliteConnection(", StringComparison.Ordinal))
            {
                continue;
            }

            // … or by a context that owns it.
            var method = TestSourceScanner.EnclosingMethodBlock(code, lineStart);
            Assert.NotNull(method);
            if (method!.Contains("contextOwnsConnection: true", StringComparison.Ordinal))
                continue;

            Assert.Fail(
                $"The SqliteConnection created in CopilotHiveDbContextTests.cs line {i + 1} is never "
                + $"released: '{line.Trim()}'. Make it a `using` local, or hand it to a context "
                + "built with `contextOwnsConnection: true`.");
        }
    }

    /// <summary>
    /// True when the line belongs to a <see cref="SqliteConnection"/>-RETURNING factory — a method
    /// whose DECLARATION returns that type, handing the connection's ownership to its caller.
    /// </summary>
    /// <remarks>
    /// The RETURN TYPE is read from the method's DECLARATION (the signature preceding its opening
    /// brace), never from its body: <c>CreateEmptyDbContext</c> returns a
    /// <see cref="CopilotHiveDbContext"/> whose BODY mentions <c>SqliteConnection</c> and contains
    /// <c>return</c>, so a body-based check would misclassify it as a connection factory and skip
    /// that site in the every-connection sweep. It is also not a factory in the ownership sense: the
    /// context it returns OWNS its connection, which is why the sweep accepts it through the
    /// <c>contextOwnsConnection: true</c> branch below.
    /// </remarks>
    private static bool IsConnectionFactory(string code, int charIndex)
    {
        var declaration = TestSourceScanner.EnclosingMethodDeclaration(code, charIndex);
        if (declaration is null)
            return false;

        // The return type is the token immediately before the method name: "private static
        // SqliteConnection OpenFileConnection(string path)". Normalize whitespace so a signature
        // spread over several lines is still matched at token granularity.
        var normalized = string.Join(' ', declaration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Contains(" SqliteConnection ", StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY call site of the file-backed connection factory disposes the connection it returns.
    /// The factory opens the connection and hands it to the caller, so the caller owns it.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: a call site that drops its <c>using</c> makes this test fail, naming the
    /// offending line.
    /// </remarks>
    [Fact]
    public void EveryFileConnectionFactoryCallSite_DisposesTheConnectionItReturns()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Persistence", "CopilotHiveDbContextTests.cs"))));

        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("OpenFileConnection(", StringComparison.Ordinal))
                continue;

            // The factory's own declaration is not a call site.
            if (line.Contains("private static SqliteConnection OpenFileConnection", StringComparison.Ordinal))
                continue;

            Assert.True(
                line.Contains("using (var ", StringComparison.Ordinal)
                    || line.Contains("using var ", StringComparison.Ordinal),
                $"The connection returned by OpenFileConnection at CopilotHiveDbContextTests.cs "
                + $"line {i + 1} is never disposed: '{line.Trim()}'");
        }
    }

    /// <summary>
    /// The <c>CreateEmptyDbContext</c> helper hands out a context that OWNS its connection, so
    /// every test that disposes that context also closes — and thereby destroys — the in-memory
    /// database instead of leaking it.
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: reverting <c>UseSqlite(connection, contextOwnsConnection: true)</c> to the
    /// caller-owns overload makes this test fail.
    /// </remarks>
    [Fact]
    public void CreateEmptyDbContext_HandsOutAContextThatOwnsItsConnection()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Persistence", "CopilotHiveDbContextTests.cs"))));

        var helper = TestSourceScanner.ExtractMethodBlock(code, "private static CopilotHiveDbContext CreateEmptyDbContext()");
        Assert.NotNull(helper);

        Assert.Contains(
            ".UseSqlite(connection, contextOwnsConnection: true)",
            helper!,
            StringComparison.Ordinal);
    }
}
