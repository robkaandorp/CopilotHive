using System.Security.Claims;

using CopilotHive.Services;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Security;

/// <summary>
/// THE SINGLE FACADE AUTHORIZATION SEAM: resolves the acting principal FRESH from current stored
/// state through <see cref="HivePrincipalResolver"/> and evaluates exactly one
/// <see cref="HiveCapability"/> through <see cref="HivePolicy"/>.
/// <para>
/// Every facade operation calls this BEFORE doing any of its own work, and the caller turns a
/// <c>false</c> answer into a <see cref="FacadeErrorKind.Forbidden"/> result. The seam deliberately
/// takes the per-request <see cref="ClaimsPrincipal"/> instead of a resolved principal: the cookie
/// (or circuit) identity is only ever used as an IDENTITY to look up, so a role change, a demotion
/// or a disablement between two calls takes effect on the very next call and nothing is cached
/// between operations.
/// </para>
/// <para>
/// FAIL-CLOSED: a refusal (unauthenticated while authentication is enabled, unknown account,
/// disabled account, unrecognised stored role, unreadable directory) and a resolved principal that
/// simply does not hold the capability both answer <c>false</c>. Cancellation is the CALLER'S, not
/// an authorization answer, so <see cref="OperationCanceledException"/> propagates unchanged — this
/// method never converts it into a denial.
/// </para>
/// </summary>
internal static class FacadeAuthorization
{
    /// <summary>
    /// The single, sanitized denial text every facade returns for a refused operation. It names no
    /// account, role, capability, refusal reason or directory detail, so an unauthorized caller
    /// learns nothing about the directory or about who holds what.
    /// </summary>
    internal const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    /// <summary>
    /// Answers whether the caller identified by <paramref name="actingUser"/> may perform
    /// <paramref name="capability"/> right now.
    /// </summary>
    /// <param name="resolver">The stateless principal resolver (never retains a principal).</param>
    /// <param name="policy">The pure policy evaluator.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="capability">The capability the operation requires.</param>
    /// <param name="log">Logger used for secret-free denial diagnostics.</param>
    /// <param name="operation">The facade operation being authorized (log context only).</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates.</param>
    /// <returns><c>true</c> only when the caller currently holds the capability.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal static async Task<bool> IsAllowedAsync(
        HivePrincipalResolver resolver,
        HivePolicy policy,
        ClaimsPrincipal? actingUser,
        HiveCapability capability,
        ILogger log,
        string operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(operation);

        // Validate the capability BEFORE any I/O so a programming error can never be masked as a
        // denial (and never as an allow).
        _ = HivePolicy.MinimumRole(capability);

        // ONE fresh resolution per call: current role/enabled state is authoritative, and the
        // caller's cancellation is never converted into a refusal.
        var resolution = await resolver.ResolveAsync(actingUser, ct).ConfigureAwait(false);

        if (resolution.Principal is not { } principal)
        {
            LogDenial(log, operation, capability, $"the principal resolution refused ({resolution.RefusalReason})");
            return false;
        }

        if (!policy.IsAllowed(principal, capability))
        {
            LogDenial(log, operation, capability, $"the resolved role {principal.Role} does not hold it");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Logs one secret-free denial (operation, capability and the account's OWN current role or the
    /// refusal reason — never a credential, claim dump or other account's data).
    /// </summary>
    private static void LogDenial(ILogger log, string operation, HiveCapability capability, string detail)
    {
        try
        {
            log.LogWarning(
                "{Operation} was denied: the acting account does not hold {Capability} ({Detail}).",
                operation,
                capability,
                detail);
        }
        catch (Exception)
        {
            // Diagnostics never change the authorization outcome.
        }
    }
}
