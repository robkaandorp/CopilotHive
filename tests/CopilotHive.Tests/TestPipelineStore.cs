using CopilotHive.Persistence;

using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Owns an in-memory SQLite <see cref="CopilotHiveDbContext"/> together with the
/// <see cref="PipelineStore"/> built over it, and disposes BOTH at test teardown.
/// <para>
/// The store cannot own the context: its single-context constructor is documented as test-only
/// ("An open context. The store does not take ownership."), so
/// <see cref="PipelineStore.DisposeAsync"/> is a no-op for a test-owned context and the
/// <c>Data Source=:memory:</c> connection would stay open until finalization. This harness is
/// the single owner that closes it: the store is disposed first, then the context — the context
/// disposal is what actually closes the in-memory connection.
/// </para>
/// <para>
/// Usage: per-test local — <c>await using var harness = TestPipelineStore.Create();</c> then
/// read <see cref="Store"/>; fixture field — <c>_harness = TestPipelineStore.Create();</c> with
/// <c>DisposeAsync() =&gt; _harness.DisposeAsync()</c>.
/// </para>
/// </summary>
internal sealed class TestPipelineStore : IAsyncDisposable
{
    private TestPipelineStore(CopilotHiveDbContext dbContext, PipelineStore store)
    {
        DbContext = dbContext;
        Store = store;
    }

    /// <summary>The in-memory context this harness owns and disposes.</summary>
    public CopilotHiveDbContext DbContext { get; }

    /// <summary>The store over <see cref="DbContext"/>, logging to <see cref="NullLogger{T}"/>.</summary>
    public PipelineStore Store { get; }

    /// <summary>Creates the in-memory context and the store over it in one step.</summary>
    public static TestPipelineStore Create()
    {
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        return new TestPipelineStore(dbContext, new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Store first (a no-op for a test-owned context, called anyway for faithfulness), then the
        // context — the disposal that actually closes the in-memory SQLite connection.
        await Store.DisposeAsync();
        await DbContext.DisposeAsync();
    }
}
