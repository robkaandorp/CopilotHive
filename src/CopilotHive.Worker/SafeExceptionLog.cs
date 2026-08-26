using Grpc.Core;

namespace CopilotHive.Worker;

/// <summary>
/// Renders exceptions for worker log output WITHOUT ever emitting an exception message,
/// stack trace, or response body.
/// <para>
/// Worker retry, recovery and fatal paths run immediately around the gRPC and LLM HTTP
/// boundaries. Both boundaries can echo request configuration back into an error payload,
/// so <c>ex.Message</c> and <c>ex.ToString()</c> may carry a PROVISIONED SECRET (a GitHub
/// token or an Ollama API key handed to the worker by the orchestrator). Those paths must
/// therefore log a CLASSIFICATION — exception type plus a protocol status code — never the
/// exception itself.
/// </para>
/// </summary>
public static class SafeExceptionLog
{
    /// <summary>Rendered when there is no exception to describe.</summary>
    public const string NoneDescription = "(none)";

    /// <summary>Maximum number of inner-exception links rendered in the chain.</summary>
    private const int MaxChainDepth = 3;

    /// <summary>
    /// Produces a single-line, secret-free classification of <paramref name="ex"/> and up to
    /// <see cref="MaxChainDepth"/> of its inner exceptions.
    /// </summary>
    /// <param name="ex">The exception to classify. May be <c>null</c>.</param>
    /// <returns>
    /// A string built exclusively from exception TYPE NAMES and protocol status codes, for
    /// example <c>RpcException(status=Unavailable)</c> or
    /// <c>HttpRequestException(httpStatus=401)</c>. Never contains any message text.
    /// </returns>
    public static string Describe(Exception? ex)
    {
        if (ex is null)
            return NoneDescription;

        var parts = new List<string>(MaxChainDepth + 1);
        var current = ex;
        for (var depth = 0; current is not null && depth <= MaxChainDepth; depth++)
        {
            parts.Add(DescribeSingle(current));
            current = current.InnerException;
        }

        return string.Join(" <- ", parts);
    }

    /// <summary>
    /// Classifies one exception instance without reading its message.
    /// </summary>
    private static string DescribeSingle(Exception ex)
    {
        var type = ex.GetType().Name;
        return ex switch
        {
            RpcException rpc => $"{type}(status={rpc.StatusCode})",
            HttpRequestException { StatusCode: { } status } => $"{type}(httpStatus={(int)status})",
            _ => type,
        };
    }
}
