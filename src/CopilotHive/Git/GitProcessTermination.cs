using System.Diagnostics;

namespace CopilotHive.Git;

/// <summary>
/// Best-effort, BOUNDED termination of a cancelled git process tree — the ONE shared implementation
/// used by every real-process git runner in the orchestrator (this assembly's
/// <see cref="BrainRepoManager"/> and <c>CopilotHive.Configuration.ConfigRepoManager</c>).
/// <para>
/// <c>ReadToEndAsync(ct)</c>/<c>WaitForExitAsync(ct)</c> observe a token but do NOT terminate the
/// child process, so a cancelled caller would otherwise return while git is still mutating the
/// clone. Every runner therefore funnels its cancellation cleanup through this method before
/// rethrowing the original cancellation.
/// </para>
/// </summary>
internal static class GitProcessTermination
{
    /// <summary>
    /// Best-effort, BOUNDED termination of a cancelled git process tree. <c>Kill</c> is guarded by
    /// <see cref="Process.HasExited"/> (checked before and after) so post-cancellation cleanup does
    /// not race a process that is exiting anyway, the confirming wait is bounded by 5s, and the
    /// whole helper is wrapped so that NO cleanup failure — including an unconfirmable termination —
    /// can ever prevent the caller's cancellation from propagating.
    /// </summary>
    internal static void TerminateTreeBestEffort(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // Process already exited between the HasExited check and Kill — safe to proceed.
                }
                catch (Exception)
                {
                    // Kill failed for another reason. If the process has since exited we are safe;
                    // otherwise there is nothing more we can do — proceed best-effort.
                }

                if (!process.HasExited)
                {
                    // Best-effort bounded wait. If this returns false the process may still be alive
                    // after 5s; the cancellation is still rethrown by the caller.
                    process.WaitForExit(5000);
                }
            }
        }
        catch
        {
            // Last-resort guard: never prevent the cancellation from propagating.
        }
    }
}
