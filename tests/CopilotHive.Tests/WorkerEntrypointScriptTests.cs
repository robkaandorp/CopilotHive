namespace CopilotHive.Tests;

/// <summary>
/// Source-content tests for <c>docker/worker/entrypoint.sh</c> after the legacy config-repo
/// clone block was removed. The per-assignment preparation in <c>WorkerService</c> owns the
/// config-repo probe and clone — it runs per task assignment, immediately before that
/// assignment's TaskExecutor — and the entrypoint script contains no clone logic at all.
/// <para>
/// Removal-proof strategy, two directions:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Absence.</b> Every element of the deleted block is asserted absent individually
///     (<c>git clone</c>, the <c>x-access-token</c> URL injection, the
///     <c>CONFIG_REPO_DIR</c>/<c>CONFIG_CLONE_URL</c> shell variables, the git identity
///     config, any <c>git -C</c> invocation). Reintroducing any one of them fails its test.
///   </item>
///   <item>
///     <b>Preservation.</b> The requirement is that everything outside the removed block stays
///     byte-for-byte, so each surviving structural element is asserted SEPARATELY: the shebang,
///     <c>set -euo pipefail</c>, the trap, the shutdown function body (the TERM kill and its
///     guarded wait), the startup banner lines, both ORCHESTRATOR_URL branches (the launch and
///     the required-variable error), the worker launch line with its PID capture, and the final
///     wait plus exit-code propagation. Deleting any one of them fails its own test.
///   </item>
/// </list>
/// </summary>
public sealed class WorkerEntrypointScriptTests
{
    private static string ReadEntrypointScript()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);

        var path = Path.Combine(repoRoot, "docker", "worker", "entrypoint.sh");
        Assert.True(File.Exists(path), $"entrypoint.sh not found at {path}");
        return File.ReadAllText(path);
    }

    // ── The legacy clone block is gone ────────────────────────────────────────

    [Fact]
    public void Entrypoint_DoesNotContainGitClone()
    {
        var script = ReadEntrypointScript();
        Assert.DoesNotContain("git clone", script);
    }

    [Fact]
    public void Entrypoint_DoesNotContainTokenInjectedUrl()
    {
        // The GH_TOKEN → x-access-token URL injection lived inside the removed block.
        var script = ReadEntrypointScript();
        Assert.DoesNotContain("x-access-token", script);
    }

    [Fact]
    public void Entrypoint_DoesNotContainConfigRepoShellVariables()
    {
        var script = ReadEntrypointScript();
        Assert.DoesNotContain("CONFIG_REPO_DIR", script);
        Assert.DoesNotContain("CONFIG_CLONE_URL", script);
    }

    [Fact]
    public void Entrypoint_DoesNotContainGitIdentityConfig()
    {
        // The removed block configured the git identity in the cloned repository. The
        // assertions target the git-config assignment forms and the email value, not the
        // banner text (which legitimately contains "CopilotHive Worker").
        var script = ReadEntrypointScript();
        Assert.DoesNotContain("copilothive-worker@local", script);
        Assert.DoesNotContain("user.email", script);
        Assert.DoesNotContain("user.name", script);
        Assert.DoesNotContain("git config", script);
    }

    [Fact]
    public void Entrypoint_DoesNotContainAnyGitInvocation()
    {
        // All git invocations (clone plus the `git -C <dir>` fetch/reset/config calls) lived
        // in the removed block; the script now invokes git nowhere.
        var script = ReadEntrypointScript();
        Assert.DoesNotContain("git -C", script);
        Assert.DoesNotContain("git fetch", script);
        Assert.DoesNotContain("git reset", script);
    }

    // ── The preserved content survives, element by element ────────────────────

    [Fact]
    public void Entrypoint_StillStartsWithShebang()
    {
        var script = ReadEntrypointScript();
        Assert.StartsWith("#!/usr/bin/env bash", script);
    }

    [Fact]
    public void Entrypoint_StillSetsStrictShellOptions()
    {
        var script = ReadEntrypointScript();
        Assert.Contains("set -euo pipefail", script);
    }

    [Fact]
    public void Entrypoint_StillDeclaresShutdownFunction()
    {
        var script = ReadEntrypointScript();
        Assert.Contains("shutdown() {", script);
        Assert.Contains("[entrypoint] Received shutdown signal, stopping worker...", script);
        Assert.Contains("[entrypoint] Shutdown complete.", script);
    }

    [Fact]
    public void Entrypoint_ShutdownFunctionStillTerminatesAndReapsTheWorker()
    {
        // The shutdown body's two operative lines: the TERM signal and the guarded wait.
        var script = ReadEntrypointScript();
        Assert.Contains("kill -TERM \"$WORKER_PID\" 2>/dev/null || true", script);
        Assert.Contains("wait \"$WORKER_PID\" 2>/dev/null || true", script);
    }

    [Fact]
    public void Entrypoint_StillTrapsTerminationSignals()
    {
        var script = ReadEntrypointScript();
        Assert.Contains("trap shutdown SIGTERM SIGINT", script);
    }

    [Fact]
    public void Entrypoint_StillPrintsStartupInfoBanner()
    {
        var script = ReadEntrypointScript();
        Assert.Contains("============================================", script);
        Assert.Contains("CopilotHive Worker Container", script);
        Assert.Contains("Mode:      SharpCoder (direct LLM)", script);
    }

    [Fact]
    public void Entrypoint_StillEchoesResolvedLlmProvider()
    {
        var script = ReadEntrypointScript();
        Assert.Contains("${LLM_PROVIDER:-copilot}", script);
    }

    [Fact]
    public void Entrypoint_StillResolvesOrchestratorUrlWithDefault()
    {
        var script = ReadEntrypointScript();
        Assert.Contains("ORCHESTRATOR_URL=\"${ORCHESTRATOR_URL:-}\"", script);
        Assert.Contains("if [[ -n \"${ORCHESTRATOR_URL}\" ]]; then", script);
    }

    [Fact]
    public void Entrypoint_StillLaunchesTheWorkerAndCapturesItsPid()
    {
        var script = ReadEntrypointScript();
        Assert.Contains("[entrypoint] Starting CopilotHive Worker (orchestrator=${ORCHESTRATOR_URL})", script);
        Assert.Contains("/opt/worker/CopilotHive.Worker &", script);
        Assert.Contains("WORKER_PID=$!", script);
    }

    [Fact]
    public void Entrypoint_StillFailsWhenOrchestratorUrlIsMissing()
    {
        // The else branch: the required-variable error plus the non-zero exit.
        var script = ReadEntrypointScript();
        Assert.Contains("[entrypoint] ERROR: ORCHESTRATOR_URL is required", script);
        Assert.Contains("exit 1", script);
    }

    [Fact]
    public void Entrypoint_StillWaitsOnTheWorkerPidUnguarded()
    {
        // The FINAL wait — distinct from the shutdown function's guarded wait: it is followed
        // by the exit-code capture rather than the `2>/dev/null || true` suppression.
        var script = ReadEntrypointScript();
        Assert.Contains("wait \"$WORKER_PID\"\nEXIT_CODE=$?", script);
    }

    [Fact]
    public void Entrypoint_StillPropagatesTheWorkerExitCode()
    {
        var script = ReadEntrypointScript();
        Assert.Contains("[entrypoint] Worker exited with code ${EXIT_CODE}", script);
        Assert.Contains("exit \"$EXIT_CODE\"", script);
    }
}