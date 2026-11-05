namespace CopilotHive.Security;

/// <summary>
/// The four authorization CAPABILITIES of the multi-user design matrix. They are the only vocabulary
/// a later boundary (endpoint, facade, route, Composer tool) should ask about; every one of them is
/// evaluated over an <see cref="ActingUserPrincipal"/> by <see cref="HivePolicy"/>.
/// <para>
/// Values start at <c>1</c> so a <c>default</c>-constructed capability is never a valid capability:
/// <see cref="HivePolicy"/> rejects an undefined value instead of silently mapping it onto one.
/// </para>
/// </summary>
public enum HiveCapability
{
    /// <summary>
    /// SHARED READ — view the shared dashboard and execution data. Held by every role
    /// (<see cref="CopilotHive.Services.UserRole.Viewer"/> and above).
    /// </summary>
    SharedRead = 1,

    /// <summary>
    /// INTERACTIVE MEMBER — use the private Composer, create goals and issues, and edit one's own
    /// drafts. <see cref="CopilotHive.Services.UserRole.Member"/> and above.
    /// </summary>
    InteractiveMember = 2,

    /// <summary>
    /// EXECUTION OPERATOR — approve, dispatch, cancel and resume goals, and manage and execute
    /// releases. <see cref="CopilotHive.Services.UserRole.Operator"/> and above.
    /// </summary>
    ExecutionOperator = 3,

    /// <summary>
    /// GLOBAL ADMIN — change global models, repositories, agent configuration and provider
    /// credentials, manage users and roles, and run backup/restore and other global or destructive
    /// operations. <see cref="CopilotHive.Services.UserRole.Admin"/> only.
    /// </summary>
    GlobalAdmin = 4,
}
