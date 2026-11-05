using CopilotHive.Services;

namespace CopilotHive.Security;

/// <summary>
/// THE CENTRAL POLICY CATALOG: the single place that maps the four design-matrix capabilities onto
/// the minimum <see cref="UserRole"/> that holds them, plus the pure evaluator over an
/// <see cref="ActingUserPrincipal"/>.
/// <para>
/// This is a CATALOG and a PURE EVALUATION: it is stateless (so it is safe as a singleton), wraps no
/// directory lookup, mutates nothing, and enforces nothing at any existing boundary. Today no
/// endpoint, facade, route or Composer tool consults it, so this type on its own changes no
/// behaviour. A later boundary that wants to enforce a capability must (a) resolve the acting
/// principal from current stored state through the trusted directory APIs
/// (<see cref="UserService.GetByGitHubIdAsync"/> for an OAuth identity,
/// <see cref="UserService.GetActivatedUserByIdAsync"/> for a known account id) via
/// <see cref="HivePrincipalResolver"/>, and only then (b) ask <see cref="IsAllowed"/> — never the
/// reverse, and never from cookie claims.
/// </para>
/// <para>
/// The evaluation NEVER trusts the principal's own <see cref="ActingUserPrincipal.IsOriginal"/> flag:
/// original identity confers no authority, so a demoted original account is evaluated exactly like
/// any other account of that role, and a DISABLED principal holds no capability at all.
/// </para>
/// </summary>
public sealed class HivePolicy
{
    /// <summary>
    /// The minimum role that holds <paramref name="capability"/> — the one authoritative statement of
    /// the design matrix:
    /// <list type="bullet">
    /// <item><see cref="HiveCapability.SharedRead"/> — every role, from <see cref="UserRole.Viewer"/>.</item>
    /// <item><see cref="HiveCapability.InteractiveMember"/> — <see cref="UserRole.Member"/> and above.</item>
    /// <item><see cref="HiveCapability.ExecutionOperator"/> — <see cref="UserRole.Operator"/> and above.</item>
    /// <item><see cref="HiveCapability.GlobalAdmin"/> — <see cref="UserRole.Admin"/> only.</item>
    /// </list>
    /// </summary>
    /// <param name="capability">The capability to look up.</param>
    /// <returns>The minimum role holding the capability.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is not a defined capability.</exception>
    public static UserRole MinimumRole(HiveCapability capability) => capability switch
    {
        HiveCapability.SharedRead => UserRole.Viewer,
        HiveCapability.InteractiveMember => UserRole.Member,
        HiveCapability.ExecutionOperator => UserRole.Operator,
        HiveCapability.GlobalAdmin => UserRole.Admin,

        // No catch-all role fallback: an undefined capability (including default(HiveCapability)) is a
        // programming error, and answering it with the widest or the narrowest role would hide that.
        _ => throw new ArgumentOutOfRangeException(
            nameof(capability),
            capability,
            $"'{capability}' is not a defined CopilotHive capability."),
    };

    /// <summary>
    /// Evaluates one capability for one principal. This is the ONLY authorization answer this type
    /// gives; it performs no I/O and no lookup of its own.
    /// </summary>
    /// <param name="principal">The acting principal resolved from current stored state.</param>
    /// <param name="capability">The capability being asked about.</param>
    /// <returns><c>true</c> only when the principal is enabled and its role meets the capability's minimum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capability"/> is not defined, or the principal carries an undefined role value
    /// (which is refused rather than compared as if it were a role).
    /// </exception>
    public bool IsAllowed(ActingUserPrincipal principal, HiveCapability capability)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var minimumRole = MinimumRole(capability);
        UserRoleText.EnsureDefined(principal.Role, nameof(principal));

        // A disabled account holds NOTHING, whatever its role says. This is the evaluator's own second
        // line of defence: the resolver already refuses disabled accounts, so a disabled principal can
        // only appear if a caller hand-built one.
        if (!principal.Enabled)
        {
            return false;
        }

        // Roles are ordered by increasing authority (Viewer < Member < Operator < Admin), so the
        // capability is held when the principal's role is at least the capability's minimum.
        return principal.Role >= minimumRole;
    }

    /// <summary>
    /// Evaluates every capability for one principal — handy for a later UI/route decision without
    /// re-deriving the matrix. The map covers exactly the four defined capabilities.
    /// </summary>
    /// <param name="principal">The acting principal resolved from current stored state.</param>
    /// <returns>A capability → allowed map.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The principal carries an undefined role value.</exception>
    public IReadOnlyDictionary<HiveCapability, bool> EvaluateAll(ActingUserPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return new Dictionary<HiveCapability, bool>
        {
            [HiveCapability.SharedRead] = IsAllowed(principal, HiveCapability.SharedRead),
            [HiveCapability.InteractiveMember] = IsAllowed(principal, HiveCapability.InteractiveMember),
            [HiveCapability.ExecutionOperator] = IsAllowed(principal, HiveCapability.ExecutionOperator),
            [HiveCapability.GlobalAdmin] = IsAllowed(principal, HiveCapability.GlobalAdmin),
        };
    }
}
