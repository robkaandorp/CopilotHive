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
    private int _initial;

    private const int MaxCap = int.MaxValue - 1;

    /// <summary>
    /// Creates a new <see cref="RetryBudget"/> with the given allowance.
    /// </summary>
    /// <param name="allowed">Total number of consume operations allowed. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="allowed"/> is negative.</exception>
    public RetryBudget(int allowed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allowed);
        if (allowed > MaxCap)
        {
            _initial = MaxCap;
            _remaining = MaxCap;
        }
        else
        {
            _initial = allowed;
            _remaining = allowed;
        }
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

    /// <summary>
    /// Atomically restores the remaining budget to the full allowance
    /// (<see cref="Allowed"/>), so <see cref="Used"/> becomes <c>0</c> and
    /// <see cref="Remaining"/> equals <see cref="Allowed"/>.
    /// </summary>
    /// <remarks>
    /// THE EXHAUSTION OUTCOME DEPENDS ON THE ALLOWANCE, so it is NOT promised unconditionally.
    /// For a POSITIVE allowance the budget is unexhausted afterwards
    /// (<see cref="Remaining"/> == <see cref="Allowed"/> and <see cref="IsExhausted"/> is
    /// <c>false</c>). For a ZERO allowance — <c>new RetryBudget(0)</c> is explicitly supported —
    /// the budget is restored but STILL EXHAUSTED (<see cref="Remaining"/> ==
    /// <see cref="Allowed"/> == <c>0</c> and <see cref="IsExhausted"/> remains <c>true</c>),
    /// because this restores the original allowance rather than granting one.
    /// <para>
    /// THIS IS A RESET, NOT A TOP-UP: <see cref="Allowed"/> is left UNCHANGED — only
    /// <c>_remaining</c> is exchanged for the current (possibly previously topped-up)
    /// initial allowance. Thread-safe: the exchange is a single atomic
    /// <see cref="Interlocked.Exchange(ref int, int)"/> of the initial allowance read with
    /// <c>Volatile.Read(ref _initial)</c>, so a racing <see cref="TryConsume"/> can
    /// neither lose the reset nor observe a torn value.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        Interlocked.Exchange(ref _remaining, Volatile.Read(ref _initial));
    }

    /// <summary>
    /// Atomically increases the budget by <paramref name="additional"/> units.
    /// Both <see cref="Allowed"/> and <see cref="Remaining"/> are updated via CAS loops
    /// with saturating arithmetic (capped at <see cref="MaxCap"/>).
    /// </summary>
    /// <param name="additional">Units to add. Must be between 1 and 1000 (inclusive).</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="additional"/> is not in [1, 1000].</exception>
    /// <remarks>
    /// When the saturation cap is reached, the guarantee that exactly <see cref="Allowed"/>
    /// consume operations will succeed no longer holds — <c>MaxCap</c> is effectively infinite.
    /// If <see cref="Remaining"/> is negative (from a racing <see cref="TryConsume"/> call),
    /// it is normalized to zero before the addition.
    /// </remarks>
    public void TopUp(int additional)
    {
        if (additional <= 0 || additional > 1000)
            throw new ArgumentOutOfRangeException(nameof(additional), "Must be positive and <= 1000.");

        // Saturating add to _initial (cap at MaxCap)
        while (true)
        {
            var current = Volatile.Read(ref _initial);
            var newValue = current >= MaxCap - additional ? MaxCap : current + additional;
            if (Interlocked.CompareExchange(ref _initial, newValue, current) == current)
                break;
        }

        // Normalize negative remaining to 0, then saturating add (cap at MaxCap)
        while (true)
        {
            var current = Volatile.Read(ref _remaining);
            var normalized = current < 0 ? 0 : current;
            var newValue = normalized >= MaxCap - additional ? MaxCap : normalized + additional;
            if (Interlocked.CompareExchange(ref _remaining, newValue, current) == current)
                break;
        }
    }
}
