using CopilotHive.Orchestration;

namespace CopilotHive.Tests;

public class BrainActionsTests
{
    [Fact]
    public void AllSpawnActions_MatchSpawnEnumValues()
    {
        var spawnEnumValues = Enum.GetValues<OrchestratorActionType>()
            .Where(a => a.ToString().StartsWith("Spawn"))
            .ToList();

        Assert.Equal(spawnEnumValues.Count, BrainActions.SpawnAll.Length);

        foreach (var action in BrainActions.SpawnAll)
        {
            Assert.StartsWith("spawn_", action);
        }
    }

    [Fact]
    public void PlanningActions_ContainAllSpawnActionsExceptImprover()
    {
        // Improver is not available during planning (it runs after review)
        foreach (var spawn in BrainActions.SpawnAll)
        {
            if (spawn == BrainActions.SpawnImprover)
                Assert.DoesNotContain(spawn, BrainActions.PlanningActions);
            else
                Assert.Contains(spawn, BrainActions.PlanningActions);
        }

        Assert.Contains(BrainActions.Done, BrainActions.PlanningActions);
        Assert.Contains(BrainActions.Skip, BrainActions.PlanningActions);
    }

    [Fact]
    public void NextStepActions_ContainAllSpawnActionsExceptImprover()
    {
        foreach (var spawn in BrainActions.SpawnAll)
        {
            if (spawn == BrainActions.SpawnImprover)
                Assert.DoesNotContain(spawn, BrainActions.NextStepActions);
            else
                Assert.Contains(spawn, BrainActions.NextStepActions);
        }

        Assert.Contains(BrainActions.Merge, BrainActions.NextStepActions);
        Assert.Contains(BrainActions.Done, BrainActions.NextStepActions);
        Assert.Contains(BrainActions.Skip, BrainActions.NextStepActions);
    }

    [Fact]
    public void Constants_MatchSnakeCaseConvention()
    {
        Assert.Equal("spawn_coder", BrainActions.SpawnCoder);
        Assert.Equal("spawn_reviewer", BrainActions.SpawnReviewer);
        Assert.Equal("spawn_tester", BrainActions.SpawnTester);
        Assert.Equal("spawn_improver", BrainActions.SpawnImprover);
        Assert.Equal("spawn_doc_writer", BrainActions.SpawnDocWriter);
        Assert.Equal("request_changes", BrainActions.RequestChanges);
        Assert.Equal("retry", BrainActions.Retry);
        Assert.Equal("merge", BrainActions.Merge);
        Assert.Equal("done", BrainActions.Done);
        Assert.Equal("skip", BrainActions.Skip);
    }

    [Fact]
    public void FormatForPrompt_JoinsWithCommas()
    {
        var result = BrainActions.FormatForPrompt(["a", "b", "c"]);
        Assert.Equal("a, b, c", result);
    }

    [Fact]
    public void EveryEnumValue_HasMatchingConstant()
    {
        var allConstants = new[]
        {
            BrainActions.SpawnCoder, BrainActions.SpawnReviewer, BrainActions.SpawnTester,
            BrainActions.SpawnImprover, BrainActions.SpawnDocWriter,
            BrainActions.RequestChanges, BrainActions.Retry,
            BrainActions.Merge, BrainActions.Done, BrainActions.Skip,
        };

        var enumValues = Enum.GetValues<OrchestratorActionType>();
        Assert.Equal(enumValues.Length, allConstants.Length);
    }
}
