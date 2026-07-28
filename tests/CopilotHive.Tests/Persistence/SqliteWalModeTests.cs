using Microsoft.Data.Sqlite;

using Xunit;

namespace CopilotHive.Tests.Persistence;

/// <summary>
/// Tests verifying SQLite WAL (Write-Ahead Logging) behavior used by CopilotHive to avoid
/// "database is locked" errors. WAL persists across connections and allows concurrent reads
/// while an exclusive writer transaction is in progress.
/// </summary>
public sealed class SqliteWalModeTests
{
    /// <summary>
    /// WAL mode must persist across separate connections to the same on-disk database file.
    /// </summary>
    [Fact]
    public async Task WalMode_PersistsAcrossConnections()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"wal-persist-{Guid.NewGuid():N}.db");
        SqliteConnection? connection1 = null;
        SqliteConnection? connection2 = null;

        try
        {
            connection1 = new SqliteConnection($"Data Source={tempFile};Pooling=False");
            await connection1.OpenAsync(TestContext.Current.CancellationToken);
            await using (var command = connection1.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode = WAL;";
                var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                Assert.Equal("wal", result?.ToString());
            }
            await connection1.CloseAsync();

            connection2 = new SqliteConnection($"Data Source={tempFile};Pooling=False");
            await connection2.OpenAsync(TestContext.Current.CancellationToken);
            await using (var command = connection2.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode;";
                var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                Assert.Equal("wal", result?.ToString());
            }
        }
        finally
        {
            // Every cleanup step is independently guarded so a single failure can never
            // prevent the remaining connections from being disposed or the files deleted.
            await SafeDisposeAsync(connection1);
            await SafeDisposeAsync(connection2);

            TryDelete(tempFile);
            TryDelete(tempFile + "-wal");
            TryDelete(tempFile + "-shm");
        }
    }

    /// <summary>
    /// Under WAL mode a concurrent read transaction can proceed while an exclusive writer
    /// holds the lock, reading the committed snapshot. Under the default rollback journal the
    /// same reader quickly gets "database is locked" when the command timeout is short.
    /// Microsoft.Data.Sqlite exposes busy-handling through command timeout; a value of zero
    /// means no timeout (infinite), so the smallest positive timeout is used to keep the test
    /// deterministic and fast.
    /// </summary>
    [Fact]
    public async Task WalMode_AllowsConcurrentReadDuringExclusiveWrite()
    {
        var dbPathWal = Path.Combine(Path.GetTempPath(), $"wal-concurrent-{Guid.NewGuid():N}.db");
        var dbPathRollback = Path.Combine(Path.GetTempPath(), $"rollback-concurrent-{Guid.NewGuid():N}.db");

        SqliteConnection? writerA = null;
        SqliteConnection? readerA = null;
        SqliteConnection? writerB = null;
        SqliteConnection? readerB = null;

        try
        {
            // DB-A: enable WAL using a dedicated, self-disposing connection. It is never
            // reused, so no connection variable is ever overwritten without disposal.
            await using (var walSetup = new SqliteConnection($"Data Source={dbPathWal};Pooling=False"))
            {
                await walSetup.OpenAsync(TestContext.Current.CancellationToken);
                await using var walCommand = walSetup.CreateCommand();
                walCommand.CommandText = "PRAGMA journal_mode = WAL;";
                var result = await walCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                Assert.Equal("wal", result?.ToString());
            }

            // DB-B intentionally keeps the default rollback journal.

            // Create test tables.
            await CreateTableAsync(dbPathWal, TestContext.Current.CancellationToken);
            await CreateTableAsync(dbPathRollback, TestContext.Current.CancellationToken);

            // Begin exclusive writer transactions. A one-second "Default Timeout" is used
            // because Microsoft.Data.Sqlite treats a timeout of 0 as infinite, so the smallest
            // positive value keeps the locked-read assertion below fast and deterministic.
            writerA = new SqliteConnection($"Data Source={dbPathWal};Mode=ReadWrite;Pooling=False;Default Timeout=1");
            await writerA.OpenAsync(TestContext.Current.CancellationToken);
            await using (var beginA = writerA.CreateCommand())
            {
                beginA.CommandText = "BEGIN EXCLUSIVE;";
                await beginA.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await using (var insertA = writerA.CreateCommand())
            {
                insertA.CommandText = "INSERT INTO t VALUES (1);";
                await insertA.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            writerB = new SqliteConnection($"Data Source={dbPathRollback};Mode=ReadWrite;Pooling=False;Default Timeout=1");
            await writerB.OpenAsync(TestContext.Current.CancellationToken);
            await using (var beginB = writerB.CreateCommand())
            {
                beginB.CommandText = "BEGIN EXCLUSIVE;";
                await beginB.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await using (var insertB = writerB.CreateCommand())
            {
                insertB.CommandText = "INSERT INTO t VALUES (1);";
                await insertB.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // WAL reader: should succeed and observe zero committed rows.
            readerA = new SqliteConnection($"Data Source={dbPathWal};Mode=ReadOnly;Pooling=False;Default Timeout=1");
            await readerA.OpenAsync(TestContext.Current.CancellationToken);
            await using (var selectA = readerA.CreateCommand())
            {
                selectA.CommandText = "SELECT * FROM t;";
                await using var reader = await selectA.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
            }

            // Rollback-journal reader: should quickly fail with "database is locked".
            readerB = new SqliteConnection($"Data Source={dbPathRollback};Mode=ReadOnly;Pooling=False;Default Timeout=1");
            await readerB.OpenAsync(TestContext.Current.CancellationToken);
            var exception = await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                await using var selectB = readerB.CreateCommand();
                selectB.CommandText = "SELECT * FROM t;";
                await selectB.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            });
            Assert.Contains("database is locked", exception.Message);
        }
        finally
        {
            // Every cleanup step is independently guarded so a single failure can never
            // prevent the remaining connections from being disposed or the files deleted.
            await SafeCommitAsync(writerA, TestContext.Current.CancellationToken);
            await SafeDisposeAsync(writerA);
            await SafeCommitAsync(writerB, TestContext.Current.CancellationToken);
            await SafeDisposeAsync(writerB);
            await SafeDisposeAsync(readerA);
            await SafeDisposeAsync(readerB);

            TryDelete(dbPathWal);
            TryDelete(dbPathWal + "-wal");
            TryDelete(dbPathWal + "-shm");
            TryDelete(dbPathRollback);
            TryDelete(dbPathRollback + "-wal");
            TryDelete(dbPathRollback + "-shm");
        }
    }

    private static async Task CreateTableAsync(string dbPath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t (x INTEGER);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Best-effort commit of any open transaction. Never throws, so a failure here can never
    /// prevent the subsequent disposal of this or any other connection.
    /// </summary>
    private static async Task SafeCommitAsync(SqliteConnection? connection, CancellationToken cancellationToken)
    {
        if (connection is null)
            return;

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "COMMIT;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            // Best-effort: the connection may be closed or have no active transaction.
        }
    }

    /// <summary>
    /// Disposes a connection, swallowing any failure. Disposal is always attempted even when
    /// closing throws, and this method itself never propagates an exception.
    /// </summary>
    private static async Task SafeDisposeAsync(SqliteConnection? connection)
    {
        if (connection is null)
            return;

        try
        {
            await connection.DisposeAsync();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
