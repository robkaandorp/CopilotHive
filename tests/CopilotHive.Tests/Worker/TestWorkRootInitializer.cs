using System.Runtime.CompilerServices;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Points <c>TaskExecutor</c>'s fallback work directory (used when a task has no
/// <c>Repositories</c>) at a real, existing directory for the whole test run.
/// </summary>
/// <remarks>
/// In production, <c>TaskExecutor.WorkRoot</c> defaults to the hardcoded <c>/copilot-home</c>,
/// which is only guaranteed to exist inside the worker container (see docker/worker/Dockerfile).
/// Tests that exercise the real <c>SharpCoderRunner</c>/<c>CodingAgent</c> pipeline (rather than a
/// fake agent runner) with a task that has no repositories would otherwise hit
/// <c>AgentOptions.WorkDirectory</c>'s existence check, which throws
/// <see cref="DirectoryNotFoundException"/> on any OS/machine where <c>/copilot-home</c> doesn't
/// exist (e.g. Windows dev machines and CI). That exception is caught inside
/// <c>TaskExecutor.ExecuteAsync</c> and logged rather than rethrown, so the task's body finishes
/// early without ever reaching the fake chat client's streaming call — a real hang was traced back
/// to exactly this in <c>WorkerLifecycleConcurrencyIntegrationTests</c>. Setting
/// <c>WORKER_WORK_ROOT</c> once for the whole assembly avoids the need for every such test to
/// manage this itself.
/// </remarks>
internal static class TestWorkRootInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "copilothive-test-work-root");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("WORKER_WORK_ROOT", dir);
    }
}
