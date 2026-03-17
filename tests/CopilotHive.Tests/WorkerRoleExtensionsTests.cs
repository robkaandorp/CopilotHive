using CopilotHive.Workers;
using Xunit;

namespace CopilotHive.Tests.Workers;

public class WorkerRoleExtensionsTests
{
    [Fact] public void Coder_ReturnsCorrectDisplayName() => Assert.Equal("Coder", WorkerRole.Coder.ToDisplayName());
    [Fact] public void Tester_ReturnsCorrectDisplayName() => Assert.Equal("Tester", WorkerRole.Tester.ToDisplayName());
    [Fact] public void Reviewer_ReturnsCorrectDisplayName() => Assert.Equal("Reviewer", WorkerRole.Reviewer.ToDisplayName());
    [Fact] public void Improver_ReturnsCorrectDisplayName() => Assert.Equal("Improver", WorkerRole.Improver.ToDisplayName());
    [Fact] public void Orchestrator_ReturnsCorrectDisplayName() => Assert.Equal("Orchestrator", WorkerRole.Orchestrator.ToDisplayName());
    [Fact] public void DocWriter_ReturnsCorrectDisplayName() => Assert.Equal("Doc Writer", WorkerRole.DocWriter.ToDisplayName());
    [Fact] public void MergeWorker_ReturnsCorrectDisplayName() => Assert.Equal("Merge Worker", WorkerRole.MergeWorker.ToDisplayName());
    [Fact] public void InvalidValue_ThrowsInvalidOperationException() =>
        Assert.Throws<InvalidOperationException>(() => ((WorkerRole)999).ToDisplayName());
}
