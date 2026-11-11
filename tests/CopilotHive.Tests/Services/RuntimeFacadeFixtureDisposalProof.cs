using System.Reflection;
using System.Runtime.ExceptionServices;

using CopilotHive.Configuration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests.Services;

/// <summary>Behavioral probes of the actual private DI fixtures, not a copy of their cleanup logic.</summary>
internal static class RuntimeFacadeFixtureDisposalProof
{
    internal static async Task VerifyAsync<T>(
        Func<Action<IServiceCollection>, T> create,
        Func<T, ServiceProvider> providerOf,
        Func<T, ComposerRuntimeManager> managerOf,
        bool bodyFails, bool disposalFails) where T : IAsyncDisposable
    {
        var probe = new DisposalProbe();
        var fixture = create(services => services.AddSingleton(_ => probe));
        var provider = providerOf(fixture);
        var manager = managerOf(fixture);
        var db = (CopilotHiveDbContext)typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        var directory = (string?)(typeof(T).GetProperty("StateDir")?.GetValue(fixture)
            ?? typeof(T).GetField("_dir", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture))!;
        var marker = Path.Combine(directory!, "owned-resource.marker");
        Task<Exception?>? body = null;
        ExceptionDispatchInfo? primary = null;
        var cleanupFailures = new List<Exception>();
        var bodyError = new Xunit.Sdk.XunitException("deliberate test-body assertion failure");
        var disposalError = new InvalidOperationException("deliberate async provider disposal failure");

        try
        {
            File.WriteAllText(marker, "backing state must survive asynchronous disposal");
            // Construct through a DI factory, so the provider owns and awaits this real disposable.
            Assert.Same(probe, provider.GetRequiredService<DisposalProbe>());
            probe.ObserveResources = () => File.Exists(marker) && db.Database.CanConnect();
            probe.Failure = disposalFails ? disposalError : null;

            body = Record.ExceptionAsync(async () =>
            {
                await using var owned = fixture;
                if (bodyFails) throw bodyError;
            }).AsTask();

            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.False(body.IsCompleted, "fixture must await the provider before returning");
            // Check the ACTUAL manager's terminal shutdown, not a mocked ordering callback.
            var shutdown = (Task?)typeof(ComposerRuntimeManager)
                .GetField("_shutdown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager);
            Assert.NotNull(shutdown);
            Assert.True(shutdown.IsCompletedSuccessfully, "manager shutdown must finish before provider disposal");
            Assert.True(File.Exists(marker), "state directory must outlive provider disposal");
            Assert.True(db.Database.CanConnect(), "database must outlive provider disposal");

            probe.Release();
            var error = await body.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (disposalFails)
            {
                var aggregate = Assert.IsType<AggregateException>(error);
                Assert.Contains(disposalError, aggregate.InnerExceptions);
            }
            else if (bodyFails)
            {
                Assert.Same(bodyError, error);
            }
            else
            {
                Assert.Null(error);
            }

            Assert.True(probe.ResourcesAliveAtResume, "backing resources must survive through the disposal's last await");
            Assert.True(probe.Terminal.Task.IsCompletedSuccessfully);
            Assert.False(Directory.Exists(directory), "state cleanup must run even when disposal throws");
            Assert.Throws<ObjectDisposedException>(() => db.Users.Count());
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            probe.Release();
            // Join EVERY owned operation before disposing remaining resources, retaining primary failures.
            async Task Step(Func<Task> action)
            {
                try { await action().WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
            if (body is not null) await Step(async () => { _ = await body; });
            else await Step(() => fixture.DisposeAsync().AsTask());
            await Step(manager.ShutdownAsync);
            await Step(() => provider.DisposeAsync().AsTask());
            if (body is not null) await Step(() => probe.Terminal.Task);
            await Step(() => db.DisposeAsync().AsTask());
            await Step(() => { TestHelpers.ForceDeleteDirectory(directory!); return Task.CompletedTask; });
        }

        if (primary is not null)
        {
            if (cleanupFailures.Count > 0) primary.SourceException.Data["CleanupFailures"] = cleanupFailures;
            primary.Throw();
        }
        Assert.Empty(cleanupFailures);
    }

    internal static async Task VerifyManagerAsync<T>(
        Func<Action<IServiceCollection>, Func<string, IChatClient>, T> create,
        Func<T, ServiceProvider> providerOf, Func<T, ComposerRuntimeManager> managerOf,
        bool bodyFails) where T : IAsyncDisposable
    {
        using var auth = new FacadeAuthorizationHarness();
        await auth.SeedAsync("fixture-disposal-original", UserRole.Member);
        var principal = (await auth.Resolver(HiveAuthMode.AuthenticationEnabled).ResolveAsync(
            FacadeAuthorizationHarness.Principal("fixture-disposal-original"), CancellationToken.None)).Principal!;
        await using var hold = new RuntimeAsyncDisposalHold();
        var probe = new DisposalProbe();
        var fixture = create(services =>
        {
            services.AddSingleton(auth.Directory.Service);
            services.AddSingleton(new HiveConfigFile
            {
                Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "ollama-local/fixture" }] },
                Composer = new ComposerConfig { Model = "ollama-local/fixture" },
            });
            services.AddSingleton(_ => probe);
        }, _ => hold);
        var provider = providerOf(fixture);
        var manager = managerOf(fixture);
        var db = (CopilotHiveDbContext)typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        var directory = (string)(typeof(T).GetProperty("StateDir")?.GetValue(fixture)
            ?? typeof(T).GetField("_dir", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture))!;
        Task<Exception?>? body = null;
        Composer? composer = null;
        ExceptionDispatchInfo? primary = null;
        var cleanupFailures = new List<Exception>();
        var bodyError = new Xunit.Sdk.XunitException("deliberate assertion failure while a fixture owns a runtime");
        try
        {
            var access = await manager.GetOrCreateAsync(principal, CancellationToken.None);
            Assert.Equal(ComposerRuntimeStatus.Connected, access.Status);
            composer = access.Runtime!.Composer;
            // Resolve LAST: if manager shutdown isn't explicitly awaited, DI enters this probe first.
            _ = provider.GetRequiredService<DisposalProbe>();
            var marker = Path.Combine(directory, "manager-ordered.marker");
            File.WriteAllText(marker, "alive");
            hold.Arm();
            body = Record.ExceptionAsync(async () =>
            {
                await using var owned = fixture;
                if (bodyFails) throw bodyError;
            }).AsTask();

            var first = await Task.WhenAny(hold.Entered.Task, probe.Entered.Task, body)
                .WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Same(hold.Entered.Task, first);
            Assert.False(body.IsCompleted);
            Assert.False(probe.Entered.Task.IsCompleted, "provider must not begin before actual manager teardown");
            Assert.False(composer.TeardownCompletion.IsCompleted);
            Assert.True(db.Database.CanConnect());
            Assert.True(File.Exists(marker));

            hold.Release();
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.True(composer.TeardownCompletion.IsCompletedSuccessfully);
            Assert.False(body.IsCompleted);
            Assert.True(db.Database.CanConnect());
            Assert.True(File.Exists(marker));
            probe.Release();
            var error = await body.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (bodyFails) Assert.Same(bodyError, error);
            else Assert.Null(error);
            Assert.False(Directory.Exists(directory));
            Assert.Throws<ObjectDisposedException>(() => db.Users.Count());
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            hold.Release();
            probe.Release();
            async Task Step(Func<Task> action)
            {
                try { await action().WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
            if (body is not null) await Step(async () => { _ = await body; });
            else await Step(() => fixture.DisposeAsync().AsTask());
            await Step(manager.ShutdownAsync);
            await Step(() => provider.DisposeAsync().AsTask());
            if (composer is not null) await Step(() => composer.TeardownCompletion);
            await Step(() => db.DisposeAsync().AsTask());
            await Step(() => { TestHelpers.ForceDeleteDirectory(directory); return Task.CompletedTask; });
        }
        if (primary is not null)
        {
            if (cleanupFailures.Count > 0) primary.SourceException.Data["CleanupFailures"] = cleanupFailures;
            primary.Throw();
        }
        Assert.Empty(cleanupFailures);
        hold.AssertHealthy();
    }

    private sealed class DisposalProbe : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<bool> ObserveResources { get; set; } = () => false;
        internal Exception? Failure { get; set; }
        internal bool ResourcesAliveAtResume { get; private set; }
        internal void Release() => _release.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            try
            {
                Entered.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                ResourcesAliveAtResume = ObserveResources();
                if (Failure is not null) throw Failure;
            }
            finally { Terminal.TrySetResult(); }
        }
    }
}
