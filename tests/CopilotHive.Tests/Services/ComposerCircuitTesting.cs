using System.Security.Claims;

using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE SHARED FIXTURE HELPERS for the ComposerChat CIRCUIT tests: the REAL production runtime-accessor
/// wiring (so a rendered page exercises the production seam rather than a re-implementation), plus the two
/// test doubles a page test needs when the runtime <em>selection</em> is not what the row is about.
/// </summary>
internal static class ComposerCircuitTesting
{
    /// <summary>
    /// Registers the FULL production circuit seam over an already-configured collection: the shared
    /// <see cref="ComposerFactory"/>, the legacy singleton Composer, the singleton
    /// <see cref="ComposerRuntimeManager"/>, the scoped <see cref="CircuitUserPrincipalAccessor"/>, the scoped
    /// <see cref="ComposerRuntimeAccessor"/> and its <see cref="IComposerRuntimeAccessor"/> alias.
    /// <para>
    /// The registrations mirror <see cref="ComposerFacadeRegistration.AddComposerRuntimeFacades"/> for the
    /// accessor half (which is what a component injects), so a rendered page asks the REAL accessor.
    /// </para>
    /// </summary>
    /// <param name="services">The collection to extend.</param>
    /// <param name="stateDir">The isolated state directory of the test.</param>
    internal static IServiceCollection AddProductionCircuitAccessor(
        this IServiceCollection services, string stateDir)
    {
        services.AddSingleton(sp => new ComposerFactory(
            sp, stateDir, ollamaApiKey: null, providerOf: _ => "ollama-local"));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(),
            sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));
        services.AddScoped(sp => new ComposerRuntimeAccessor(
            sp.GetRequiredService<ComposerRuntimeManager>(),
            sp.GetRequiredService<CircuitUserPrincipalAccessor>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            NullLogger<ComposerRuntimeAccessor>.Instance));
        services.AddScoped<IComposerRuntimeAccessor>(sp => sp.GetRequiredService<ComposerRuntimeAccessor>());
        return services;
    }

    /// <summary>
    /// Builds the circuit's cascading authentication state over <paramref name="principal"/> — exactly what
    /// <c>AddCascadingAuthenticationState</c> hands a component in an authenticated host.
    /// </summary>
    /// <param name="principal">The circuit's authenticated principal.</param>
    internal static Task<AuthenticationState> AuthenticatedState(ClaimsPrincipal principal) =>
        Task.FromResult(new AuthenticationState(principal));

    /// <summary>
    /// Builds the production-shaped GRANTED access for <paramref name="composer"/>: a real
    /// <see cref="ComposerUserRuntime"/> handle over that Composer (and its own attachment store) for the
    /// validated account, exactly as the manager would hand one out.
    /// </summary>
    /// <param name="composer">The Composer the handle wraps.</param>
    /// <param name="attachments">The attachment store of the same selection.</param>
    /// <param name="users">The trusted directory the identity is validated against.</param>
    /// <param name="userId">The activated internal account id.</param>
    /// <param name="status">The status the manager would report.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<ComposerRuntimeAccess> GrantedAccessAsync(
        Composer composer,
        ComposerAttachmentService attachments,
        UserService users,
        int userId,
        ComposerRuntimeStatus status = ComposerRuntimeStatus.Connected,
        CancellationToken ct = default)
    {
        var state = await ComposerUserState.ResolveAsync(users, userId, ct);
        var runtime = new ComposerUserRuntime(
            state, composer, attachments, subscriber: null,
            new ComposerConstructionSnapshot(composer.StartupDefaultModel, compactionModel: null));

        return ComposerRuntimeAccess.Granted(ComposerRuntimeResult.Granted(status, runtime));
    }

    /// <summary>
    /// A scripted <see cref="IComposerRuntimeAccessor"/> for page tests whose subject is NOT the runtime
    /// selection. It answers exactly what it is told, and records how many times it was asked — so a row can
    /// still prove that the page RE-QUERIES per operation instead of caching the first answer.
    /// </summary>
    internal sealed class ScriptedCircuitRuntimeAccessor : IComposerRuntimeAccessor
    {
        private readonly Func<int, CancellationToken, Task<ComposerRuntimeAccess>> _answer;
        private int _acquires;
        private int _connects;

        /// <summary>Creates the scripted accessor.</summary>
        /// <param name="answer">The answer for the Nth acquisition (1-based) and the caller's token.</param>
        /// <param name="authEnabled">The host authentication mode the accessor reports.</param>
        internal ScriptedCircuitRuntimeAccessor(
            Func<int, CancellationToken, Task<ComposerRuntimeAccess>> answer, bool authEnabled = true)
        {
            _answer = answer ?? throw new ArgumentNullException(nameof(answer));
            AuthEnabled = authEnabled;
        }

        /// <inheritdoc />
        public bool AuthEnabled { get; }

        /// <summary>How many acquisitions (reads and mutations together) the page performed.</summary>
        internal int AcquireCalls => Volatile.Read(ref _acquires);

        /// <summary>How many operation-driven connects the page performed.</summary>
        internal int ConnectCalls => Volatile.Read(ref _connects);

        /// <inheritdoc />
        public Task<ComposerRuntimeAccess> AcquireForCircuitAsync(
            Task<AuthenticationState>? authenticationState, CancellationToken ct) =>
            _answer(Interlocked.Increment(ref _acquires), ct);

        /// <inheritdoc />
        public Task<ComposerRuntimeAccess> ConnectForCircuitAsync(
            Task<AuthenticationState>? authenticationState, CancellationToken ct)
        {
            Interlocked.Increment(ref _connects);
            return _answer(Interlocked.Increment(ref _acquires), ct);
        }

        /// <inheritdoc />
        public Task<ComposerRuntimeAccess> AcquireForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            _answer(Interlocked.Increment(ref _acquires), ct);

        /// <inheritdoc />
        public Task<ComposerRuntimeAccess> ConnectForRequestAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            Interlocked.Increment(ref _connects);
            return _answer(Interlocked.Increment(ref _acquires), ct);
        }
    }
}
