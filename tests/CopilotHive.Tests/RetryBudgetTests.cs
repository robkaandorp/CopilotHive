using CopilotHive.Services;

namespace CopilotHive.Tests;

public sealed class RetryBudgetTests
{
    [Fact]
    public void Constructor_NegativeInput_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBudget(-1));
    }

    [Fact]
    public void TryConsume_ReturnsTrueAndDecrementsRemaining_UntilExhausted()
    {
        var budget = new RetryBudget(3);

        Assert.True(budget.TryConsume());
        Assert.Equal(2, budget.Remaining);

        Assert.True(budget.TryConsume());
        Assert.Equal(1, budget.Remaining);

        Assert.True(budget.TryConsume());
        Assert.Equal(0, budget.Remaining);

        Assert.False(budget.TryConsume());
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void TryConsume_ReturnsFalse_WhenExhausted()
    {
        var budget = new RetryBudget(1);
        budget.TryConsume();

        Assert.False(budget.TryConsume());
    }

    [Fact]
    public void IsExhausted_True_WhenRemainingIsZero()
    {
        var budget = new RetryBudget(2);

        Assert.False(budget.IsExhausted);
        budget.TryConsume();
        Assert.False(budget.IsExhausted);
        budget.TryConsume();
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void Used_Remaining_Allowed_CorrectThroughoutLifecycle()
    {
        var budget = new RetryBudget(3);

        Assert.Equal(0, budget.Used);
        Assert.Equal(3, budget.Remaining);
        Assert.Equal(3, budget.Allowed);

        budget.TryConsume();
        Assert.Equal(1, budget.Used);
        Assert.Equal(2, budget.Remaining);
        Assert.Equal(3, budget.Allowed);

        budget.TryConsume();
        Assert.Equal(2, budget.Used);
        Assert.Equal(1, budget.Remaining);

        budget.TryConsume();
        Assert.Equal(3, budget.Used);
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void ZeroBudget_ExhaustedImmediately_TryConsumeAlwaysReturnsFalse()
    {
        var budget = new RetryBudget(0);

        Assert.True(budget.IsExhausted);
        Assert.Equal(0, budget.Allowed);
        Assert.Equal(0, budget.Remaining);
        Assert.Equal(0, budget.Used);
        Assert.False(budget.TryConsume());
        Assert.Equal(0, budget.Used);
    }
}
