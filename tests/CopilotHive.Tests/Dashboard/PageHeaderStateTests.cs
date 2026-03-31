using CopilotHive.Dashboard;

namespace CopilotHive.Tests.Dashboard;

/// <summary>
/// Tests for <see cref="PageHeaderState"/>.
/// </summary>
public sealed class PageHeaderStateTests
{
    [Fact]
    public void Title_DefaultsToEmpty()
    {
        var state = new PageHeaderState();
        Assert.Equal(string.Empty, state.Title);
    }

    [Fact]
    public void SetTitle_UpdatesTitle()
    {
        var state = new PageHeaderState();
        state.SetTitle("🎯 Test Page");
        Assert.Equal("🎯 Test Page", state.Title);
    }

    [Fact]
    public void SetTitle_RaisesOnChangedEvent()
    {
        var state = new PageHeaderState();
        var eventRaised = false;
        state.OnChanged += () => eventRaised = true;
        
        state.SetTitle("New Title");
        
        Assert.True(eventRaised);
    }

    [Fact]
    public void SetTitle_WithEmptyString_RaisesOnChangedEvent()
    {
        var state = new PageHeaderState();
        state.SetTitle("Initial");
        
        var eventRaised = false;
        state.OnChanged += () => eventRaised = true;
        
        state.SetTitle("");
        
        Assert.True(eventRaised);
        Assert.Equal("", state.Title);
    }

    [Fact]
    public void SetTitle_MultipleSubscribers_AllReceiveEvent()
    {
        var state = new PageHeaderState();
        var callCount = 0;
        
        state.OnChanged += () => callCount++;
        state.OnChanged += () => callCount++;
        state.OnChanged += () => callCount++;
        
        state.SetTitle("Test");
        
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void SetTitle_OverwritesPreviousTitle()
    {
        var state = new PageHeaderState();
        state.SetTitle("First Title");
        state.SetTitle("Second Title");
        
        Assert.Equal("Second Title", state.Title);
    }

    [Fact]
    public void OnChanged_CanBeUnsubscribed()
    {
        var state = new PageHeaderState();
        var callCount = 0;
        
        void Handler() => callCount++;
        
        state.OnChanged += Handler;
        state.SetTitle("First");
        Assert.Equal(1, callCount);
        
        state.OnChanged -= Handler;
        state.SetTitle("Second");
        Assert.Equal(1, callCount); // Handler was unsubscribed, so count stays at 1
    }

    [Fact]
    public void SetTitle_WithEmoji_WorksCorrectly()
    {
        var state = new PageHeaderState();
        state.SetTitle("🏠 Dashboard");
        Assert.Equal("🏠 Dashboard", state.Title);
    }

    [Fact]
    public void Title_CanIncludeGoalId()
    {
        var state = new PageHeaderState();
        state.SetTitle("🎯 Goal: my-goal-123");
        Assert.Equal("🎯 Goal: my-goal-123", state.Title);
    }
}