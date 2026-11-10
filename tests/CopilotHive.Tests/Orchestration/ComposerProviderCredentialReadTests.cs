using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using CopilotHive.Orchestration;
using CopilotHive.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CopilotHive.Tests.Orchestration;

public sealed partial class ComposerRuntimeManagerTests
{
    /// <summary>
    /// Observes the REAL UserService database queries and entity materialization, not a copied query or a
    /// substitute directory. Inert during setup; records column names and counts only, never credential values.
    /// The client-factory/token-probe counters cannot detect a secret loaded through a directory query.
    /// </summary>
    private sealed class CredentialReadObserver
    {
        // EF treats materialization interceptors as singleton services. One shared router avoids creating a new
        // internal service provider per test. Weak context keys route to per-fixture counters without leaking
        // contexts, sharing observation state, or altering/suppressing EF's provider-cache diagnostics.
        private static readonly ConditionalWeakTable<DbContext, CredentialReadObserver> Observers = new();
        public static IInterceptor Interceptor { get; } = new Router();
        public static void Bind(DbContext context, CredentialReadObserver observer) => Observers.Add(context, observer);

        private readonly ConcurrentQueue<string> _queries = new();
        private int _originalId;
        private int _originalMaterializations;

        public string[] Queries => _queries.ToArray();
        public int OriginalAccountMaterializations => Volatile.Read(ref _originalMaterializations);

        public void Start(int originalId) => Volatile.Write(ref _originalId, originalId);

        private void Observe(DbCommand command)
        {
            if (Volatile.Read(ref _originalId) != 0)
                _queries.Enqueue(command.CommandText);
        }

        private sealed class Router : DbCommandInterceptor, IMaterializationInterceptor
        {
            public override InterceptionResult<DbDataReader> ReaderExecuting(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
            {
                if (eventData.Context is { } context && Observers.TryGetValue(context, out var observer))
                    observer.Observe(command);
                return result;
            }

            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                if (eventData.Context is { } context && Observers.TryGetValue(context, out var observer))
                    observer.Observe(command);
                return ValueTask.FromResult(result);
            }

            public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
            {
                if (Observers.TryGetValue(materializationData.Context, out var observer)
                    && entity is UserEntity user && user.Id == Volatile.Read(ref observer._originalId))
                    Interlocked.Increment(ref observer._originalMaterializations);
                return entity;
            }
        }
    }

    private static bool SelectsCredentialColumns(string sql) =>
        sql.Contains("access_token", StringComparison.OrdinalIgnoreCase)
        || sql.Contains("refresh_token", StringComparison.OrdinalIgnoreCase);

    private void AssertNoOriginalCredentialReads()
    {
        var observer = _factory.CredentialReads;
        var credentialQueries = observer.Queries.Where(SelectsCredentialColumns).ToArray();
        Assert.NotEmpty(observer.Queries); // A disconnected/miswired observer must not make this proof vacuous.
        Assert.True(credentialQueries.Length == 0 && observer.OriginalAccountMaterializations == 0,
            $"Expected no original credential reads; observed {credentialQueries.Length} credential-column SELECT(s) "
            + $"and {observer.OriginalAccountMaterializations} original UserEntity materialization(s).\n"
            + string.Join("\n", credentialQueries));
    }

    [Fact]
    public async Task CredentialReadObserver_FullOriginalEntityLookup_DetectsTokenColumnsAndMaterialization()
    {
        var (original, _) = await TwoUsersAsync();
        _factory.CredentialReads.Start(original.UserId);

        // Deliberate positive control: this legacy API is allowed to read credentials for the ORIGINAL user.
        var entity = await _users.GetAdminUserAsync(Ct);

        Assert.NotNull(entity);
        Assert.Equal(original.UserId, entity.Id);
        Assert.False(string.IsNullOrEmpty(entity.AccessToken));
        Assert.False(string.IsNullOrEmpty(entity.RefreshToken));
        Assert.Equal(1, _factory.CredentialReads.OriginalAccountMaterializations);
        Assert.Contains(_factory.CredentialReads.Queries, sql =>
            sql.Contains("access_token", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("refresh_token", StringComparison.OrdinalIgnoreCase));
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(Record.Exception(AssertNoOriginalCredentialReads));
    }

    [Fact]
    public async Task CredentialReadObserver_ActivatedAccountProjection_IsSecretFreeAndObservable()
    {
        var (original, _) = await TwoUsersAsync();
        _factory.CredentialReads.Start(original.UserId);

        var summary = await _users.GetActivatedUserByIdAsync(original.UserId, Ct);

        Assert.NotNull(summary);
        Assert.Equal(original.UserId, summary.Id);
        AssertNoOriginalCredentialReads();
    }

    [Fact]
    public Task NonOriginal_CopilotPrimary_ConstructionAndRetry_NeverReadOriginalCredentialColumns() =>
        AssertGateNeverReadsOriginalCredentialsAsync("copilot/main", null, ComposerRuntimeStatus.Deferred);

    [Fact]
    public Task NonOriginal_CopilotCompaction_ConstructionAndRetry_NeverReadOriginalCredentialColumns() =>
        AssertGateNeverReadsOriginalCredentialsAsync("ollama-local/main", "copilot/compact", ComposerRuntimeStatus.Deferred);

    [Fact]
    public Task NonOriginal_Ollama_ConstructionAndRetry_NeverReadOriginalCredentialColumns() =>
        AssertGateNeverReadsOriginalCredentialsAsync("ollama-local/main", null, ComposerRuntimeStatus.Connected);

    private async Task AssertGateNeverReadsOriginalCredentialsAsync(
        string primary, string? compaction, ComposerRuntimeStatus expectedStatus)
    {
        var (original, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = true;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider(primary, compaction));
        _factory.CredentialReads.Start(original.UserId); // AFTER setup; includes actual construction AND retry.

        var created = await Request(manager, invited);
        var retry = await Retry(manager, invited);

        foreach (var result in new[] { created, retry })
        {
            Assert.Equal(expectedStatus, result.Status);
            Assert.True(result.IsGranted);
            Assert.Equal(ComposerRuntimeRefusal.None, result.Refusal);
            Assert.Null(result.Failure);
        }
        Assert.Same(created.Runtime, retry.Runtime);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(expectedStatus == ComposerRuntimeStatus.Deferred ? 0 : 1, _clientCreations);
        AssertNoOriginalCredentialReads();
    }
}
