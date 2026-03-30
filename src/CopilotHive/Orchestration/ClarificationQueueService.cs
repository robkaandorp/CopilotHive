using System.Collections.Concurrent;

namespace CopilotHive.Orchestration;

/// <summary>
/// In-memory queue that stores pending <see cref="ClarificationRequest"/> instances
/// and allows polling or <see cref="TaskCompletionSource{T}"/>-based waiting for
/// resolution. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class ClarificationQueueService
{
    /// <summary>Timeout for the Composer LLM to auto-answer a clarification.</summary>
    public static readonly TimeSpan ComposerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for a human to answer a clarification after Composer escalation.</summary>
    public static readonly TimeSpan HumanTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Fallback message returned when all timeouts expire.</summary>
    public const string TimeoutFallbackMessage = "Please proceed with your best judgment.";

    private readonly ConcurrentDictionary<string, ClarificationRequest> _requests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _waiters = new();

    /// <summary>Raised when a request is added or resolved, so the UI can update badge counts.</summary>
    public event Action? OnChanged;

    /// <summary>
    /// Adds a new clarification request and creates a <see cref="TaskCompletionSource{T}"/>
    /// that callers can await for the answer.
    /// </summary>
    /// <param name="request">The clarification request to enqueue.</param>
    /// <returns>A <see cref="TaskCompletionSource{T}"/> that completes when the answer is submitted.</returns>
    public TaskCompletionSource<string> Enqueue(ClarificationRequest request)
    {
        _requests[request.Id] = request;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[request.Id] = tcs;
        OnChanged?.Invoke();
        return tcs;
    }

    /// <summary>
    /// Submits an answer for a pending clarification request, completing the waiter's
    /// <see cref="TaskCompletionSource{T}"/> and updating the request state.
    /// </summary>
    /// <param name="requestId">The ID of the clarification request to answer.</param>
    /// <param name="answer">The answer text.</param>
    /// <param name="resolvedBy">Who resolved it: <c>"composer"</c> or <c>"human"</c>.</param>
    /// <returns><c>true</c> if the request existed and was answered; <c>false</c> otherwise.</returns>
    public bool SubmitAnswer(string requestId, string answer, string resolvedBy)
    {
        if (!_requests.TryGetValue(requestId, out var request))
            return false;

        request.Answer = answer;
        request.Status = ClarificationStatus.Answered;
        request.ResolvedBy = resolvedBy;

        if (_waiters.TryRemove(requestId, out var tcs))
            tcs.TrySetResult(answer);

        OnChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Marks a request as timed out and completes the waiter with the fallback message.
    /// </summary>
    /// <param name="requestId">The ID of the clarification request that timed out.</param>
    public void MarkTimedOut(string requestId)
    {
        if (_requests.TryGetValue(requestId, out var request))
        {
            request.Status = ClarificationStatus.TimedOut;
            request.Answer = TimeoutFallbackMessage;
        }

        if (_waiters.TryRemove(requestId, out var tcs))
            tcs.TrySetResult(TimeoutFallbackMessage);

        OnChanged?.Invoke();
    }

    /// <summary>
    /// Returns all requests currently awaiting a human answer.
    /// </summary>
    public IReadOnlyList<ClarificationRequest> GetPendingHumanRequests()
    {
        return _requests.Values
            .Where(r => r.Status == ClarificationStatus.AwaitingHuman)
            .OrderBy(r => r.RequestedAt)
            .ToList();
    }

    /// <summary>
    /// Returns all requests regardless of status, ordered by creation time (newest first).
    /// </summary>
    public IReadOnlyList<ClarificationRequest> GetAllRequests()
    {
        return _requests.Values
            .OrderByDescending(r => r.RequestedAt)
            .ToList();
    }

    /// <summary>
    /// Returns the count of requests currently awaiting a human answer.
    /// </summary>
    public int PendingHumanCount => _requests.Values.Count(r => r.Status == ClarificationStatus.AwaitingHuman);

    /// <summary>
    /// Gets a specific request by ID, or <c>null</c> if not found.
    /// </summary>
    /// <param name="requestId">The clarification request ID.</param>
    /// <returns>The request, or <c>null</c>.</returns>
    public ClarificationRequest? GetRequest(string requestId)
    {
        _requests.TryGetValue(requestId, out var request);
        return request;
    }

    /// <summary>
    /// Updates a request's status to <see cref="ClarificationStatus.AwaitingHuman"/>.
    /// Called when the Composer LLM cannot answer and escalates to human.
    /// </summary>
    /// <param name="requestId">The clarification request ID.</param>
    public void EscalateToHuman(string requestId)
    {
        if (_requests.TryGetValue(requestId, out var request))
        {
            request.Status = ClarificationStatus.AwaitingHuman;
            OnChanged?.Invoke();
        }
    }
}
