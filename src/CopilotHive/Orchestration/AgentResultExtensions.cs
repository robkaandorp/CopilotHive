using SharpCoder;

namespace CopilotHive.Orchestration;

/// <summary>
/// Helpers for classifying SharpCoder <see cref="AgentResult"/> outcomes.
/// </summary>
internal static class AgentResultExtensions
{
    /// <summary>The <see cref="AgentResult.Status"/> value SharpCoder uses for provider failures.</summary>
    internal const string ErrorStatus = "Error";

    /// <summary>
    /// Returns <c>true</c> when the SharpCoder <see cref="AgentResult"/> represents a provider failure.
    /// Per the SharpCoder contract, <c>CodingAgent.ExecuteAsync</c> returns and
    /// <c>CodingAgent.ExecuteStreamingAsync</c> yields as its final <c>Completed</c> update an
    /// <see cref="AgentResult"/> with <see cref="AgentResult.Status"/> equal to <c>"Error"</c> and
    /// <see cref="AgentResult.Message"/> holding the failure text for most provider failures; only
    /// <c>OperationCanceledException</c>, <c>HttpRequestException</c> and <c>ObjectDisposedException</c>
    /// are rethrown as real exceptions.
    /// </summary>
    /// <param name="result">The SharpCoder result to inspect.</param>
    /// <returns><c>true</c> when the result reports a provider failure.</returns>
    internal static bool IsError(this AgentResult result) => result.Status == ErrorStatus;
}
