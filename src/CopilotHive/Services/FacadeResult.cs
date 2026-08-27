namespace CopilotHive.Services;

/// <summary>
/// Categorises the failure mode of a facade operation, mirroring the HTTP status an
/// endpoint would return for the same condition.
/// </summary>
public enum FacadeErrorKind
{
    /// <summary>No error — the operation succeeded.</summary>
    None,

    /// <summary>The requested resource does not exist (HTTP 404).</summary>
    NotFound,

    /// <summary>The operation conflicts with existing state (HTTP 409).</summary>
    Conflict,

    /// <summary>The request was malformed or failed validation (HTTP 400).</summary>
    BadRequest,

    /// <summary>The operation requires configuration that is not present.</summary>
    NotConfigured,

    /// <summary>The operation requires a dependency that is not available (HTTP 503).</summary>
    ServiceUnavailable,

    /// <summary>An unexpected internal failure occurred (HTTP 500).</summary>
    Internal,
}

/// <summary>
/// Result contract for facade operations, carrying a typed value on success.
/// </summary>
/// <remarks>
/// A facade catches ONLY the exceptions its endpoint handler catches today and maps them to
/// <see cref="FacadeErrorKind"/> values; anything else is RETHROWN (never converted to a
/// result), so unexpected failures surface as exceptions instead of being silently swallowed.
/// </remarks>
/// <typeparam name="T">The value type produced by the operation.</typeparam>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Value">The produced value when <paramref name="Success"/> is <c>true</c>; otherwise <c>default</c>.</param>
/// <param name="Error">Human-readable error message when <paramref name="Success"/> is <c>false</c>; otherwise <c>null</c>.</param>
/// <param name="Kind">The failure category when <paramref name="Success"/> is <c>false</c>; otherwise <see cref="FacadeErrorKind.None"/>.</param>
public record FacadeResult<T>(bool Success, T? Value, string? Error, FacadeErrorKind Kind);

/// <summary>
/// Result contract for facade operations that produce no value. Follows the same
/// catch-only-what-the-endpoint-catches rule as <see cref="FacadeResult{T}"/>.
/// </summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Error">Human-readable error message when <paramref name="Success"/> is <c>false</c>; otherwise <c>null</c>.</param>
/// <param name="Kind">The failure category when <paramref name="Success"/> is <c>false</c>; otherwise <see cref="FacadeErrorKind.None"/>.</param>
public record FacadeResult(bool Success, string? Error, FacadeErrorKind Kind);
