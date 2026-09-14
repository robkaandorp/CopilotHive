using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// THE EAGER-RECORDING WIRING for an existing fixture that hands the REAL
/// <see cref="GrpcWorkerGateway"/> a real publisher: a real
/// <see cref="WorkerAssignmentContextStore"/> over a private in-memory SQLite database, plus the real
/// <see cref="WorkerAssignmentPublisher"/> over it. <see cref="Start"/> returns the anchor that keeps
/// the in-memory database alive for the test's lifetime.
/// </summary>
/// <remarks>
/// A SMALL SHARED HELPER, NOT A SECOND HARNESS: three existing fixtures need the same wiring, and
/// there is no transport, no stream reader/writer and no teardown ledger here. The anchor connection
/// must stay open for the duration of the test — an in-memory SQLite database is destroyed when its
/// last connection closes — so the returned <see cref="Session"/> is what the caller disposes.
/// </remarks>
internal static class EagerAssignmentRecording
{
    /// <summary>
    /// Opens the anchor connection, creates the schema with the existing shared test factory, and
    /// returns the REAL store and the REAL publisher the gateway must delegate to.
    /// </summary>
    /// <param name="manager">The routing authority holding the delivered task's pipeline.</param>
    /// <param name="pool">The pool the delivered worker instance is pinned to.</param>
    /// <returns>The session to dispose once the test is done.</returns>
    public static Session Start(GoalPipelineManager manager, WorkerPool pool)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(connection)
            .Options;

        var store = new WorkerAssignmentContextStore(
            new SharedDbContextFactory(connection, options),
            NullLogger<WorkerAssignmentContextStore>.Instance);

        return new Session(connection, store, new WorkerAssignmentPublisher(manager, pool, store));
    }

    /// <summary>The live anchor connection, the real store and the real publisher.</summary>
    internal sealed class Session(
        SqliteConnection connection,
        WorkerAssignmentContextStore store,
        WorkerAssignmentPublisher publisher) : IDisposable
    {
        /// <summary>The REAL publisher the gateway under test is constructed with.</summary>
        public WorkerAssignmentPublisher Publisher { get; } = publisher;

        /// <summary>
        /// The REAL store, used as TEST OBSERVATION only — the production path never reads back.
        /// </summary>
        public WorkerAssignmentContextStore Store { get; } = store;

        /// <inheritdoc />
        public void Dispose() => connection.Dispose();
    }
}
