using System.Threading;

namespace CopilotHive.Services;

/// <summary>
/// Encapsulates a "budget that depletes" counter. Call <see cref="TryConsume"/> to
/// decrement the remaining budget; when exhausted, <see cref="IsExhausted"/> is
/// <c>true</c> and further <see cref="TryConsume"/> calls return <c>false</c>.
/// </summary>
public sealed class RetryBudget
{
    private int _remaining;
    private readonly int _initial;

    /// <summary>
    /// Creates a new <see cref="RetryBudget"/> with the given allowance.
    /// </summary>
    /// <param name="allowed">Total number of consume operations allowed. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="allowed"/> is negative.</exception>
    public RetryBudget(int allowed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allowed);
        _initial = allowed;
        _remaining = allowed;
    }

    /// <summary>Number of times <see cref="TryConsume"/> has succeeded.</summary>
    public int Used => _initial - Math.Max(0, _remaining);

    /// <summary>Number of consume operations still available.</summary>
    public int Remaining => Math.Max(0, _remaining);

    /// <summary>Total budget that was originally granted.</summary>
    public int Allowed => _initial;

    /// <summary><c>true</c> when no consume operations remain.</summary>
    public bool IsExhausted => _remaining <= 0;

    /// <summary>
    /// Attempts to consume one unit of the budget.
    /// Returns <c>true</c> and decrements <see cref="Remaining"/> on success;
    /// returns <c>false</c> when the budget is already exhausted.
    /// Thread-safe: uses <see cref="Interlocked.Decrement(ref int)"/> to ensure
    /// concurrent callers cannot both succeed when only one unit remains.
    /// </summary>
    public bool TryConsume()
    {
        var prev = Interlocked.Decrement(ref _remaining);
        return prev >= 0;
    }
}
