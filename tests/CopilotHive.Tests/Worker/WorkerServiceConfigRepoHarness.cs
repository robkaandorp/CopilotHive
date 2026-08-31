using System.Reflection;
using System.Threading.Channels;

using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

using Microsoft.Extensions.AI;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// The shared harness for the slice 2c-c-ii-a <c>WorkerService</c> production-wiring tests: it
/// drives the REAL <c>WorkerService.ProcessMessagesAsync</c> loop through exactly one assignment
/// and gives the test full control over every seam the per-assignment config-repo preparation
/// touches.
/// </summary>
/// <remarks>
/// Everything is TCS/counter based — the harness never sleeps, polls or asserts on timing. The
/// single completion signal is the assignment's <c>WorkerReady</c>, which the body emits on the
/// success path AND on every failure path, so a preparation failure can never hang a test.
/// </remarks>
internal static class WorkerServiceConfigRepoHarness
{
    /// <summary>A bound on every await a mutant could otherwise block forever.</summary>
    internal static readonly TimeSpan AwaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Builds a <see cref="WorkerService"/> whose agent runner is the supplied fake.</summary>
    internal static WorkerService BuildService(IAgentRunner runner, string configRepoDir)
    {
        var service = new WorkerService("http://localhost:9999", "worker-1", ["coder"], configRepoDir);

        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);

        typeof(WorkerService).GetField("_assignedId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, "worker-1");

        return service;
    }

    /// <summary>
    /// Pushes ONE coder assignment through the real message loop and returns once the assignment
    /// body has emitted its Ready (i.e. the body — including the seam's disposal — has finished).
    /// </summary>
    internal static async Task RunOneAssignmentAsync(
        WorkerService service, string taskId, CancellationToken ct)
    {
        var responses = new ScriptedResponseStream();
        var requests = new ReadyGateRequestStream();
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        var method = typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var loop = (Task)method.Invoke(service, [stream, "worker-1", ct])!;

        responses.Push(new OrchestratorMessage
        {
            Assignment = new TaskAssignment
            {
                TaskId = taskId,
                GoalId = "goal-1",
                GoalDescription = "desc",
                Prompt = "prompt",
                Role = GrpcWorkerRole.Coder,
            },
        });

        // The body ALWAYS reaches its Ready claim — success, failure and cancellation alike.
        await requests.FirstReady.WaitAsync(AwaitTimeout, ct);

        responses.Complete();
        await loop.WaitAsync(AwaitTimeout, ct);
    }

    /// <summary>
    /// Installs the static <see cref="GitOperations.ProcessRunner"/> seam and restores the
    /// previous value on disposal, so no test can leak a fake launcher into another.
    /// </summary>
    internal static IDisposable InstallProcessRunner(FakeGitLauncher launcher)
    {
        var original = GitOperations.ProcessRunner;
        GitOperations.ProcessRunner = launcher.Launch;
        return new Restore(() => GitOperations.ProcessRunner = original);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>
/// Records every git launch the config-repo seam performs and answers each one from a
/// test-supplied handler keyed on the tokenized arguments. No real process is ever started.
/// </summary>
/// <remarks>
/// The COMPLETE <see cref="GitProcessRequest"/> is retained for every launch — the tokenized
/// arguments, the working directory AND a defensive SNAPSHOT of the child environment block —
/// so a test can assert what production actually handed the process layer (for example that the
/// <c>GIT_ASKPASS</c> entry points at the generated helper file and that the credential appears
/// in <c>GITHUB_CONFIG_REPO_TOKEN</c> and nowhere else). The environment is copied at launch
/// time because the seam builds a fresh dictionary per launch and a later mutation must never
/// rewrite what an earlier assertion observes.
/// </remarks>
internal sealed class FakeGitLauncher
{
    private readonly object _gate = new();
    private readonly List<CapturedGitLaunch> _launches = [];
    private readonly Func<IReadOnlyList<string>, GitProcessResult> _handler;
    private readonly Action? _onLaunch;

    internal FakeGitLauncher(
        Func<IReadOnlyList<string>, GitProcessResult> handler, Action? onLaunch = null)
    {
        _handler = handler;
        _onLaunch = onLaunch;
    }

    /// <summary>Every COMPLETE captured launch, in order.</summary>
    internal IReadOnlyList<CapturedGitLaunch> Launches
    {
        get { lock (_gate) return [.. _launches]; }
    }

    /// <summary>Every tokenized launch, in order.</summary>
    internal IReadOnlyList<string[]> Calls => [.. Launches.Select(l => l.Tokens)];

    /// <summary>The number of launches performed so far.</summary>
    internal int CallCount
    {
        get { lock (_gate) return _launches.Count; }
    }

    /// <summary>Whether any launch's tokens start with <paramref name="prefix"/>.</summary>
    internal bool Saw(params string[] prefix) => Launches.Any(l => l.StartsWith(prefix));

    /// <summary>The SINGLE launch whose tokens start with <paramref name="prefix"/>.</summary>
    internal CapturedGitLaunch Single(params string[] prefix) =>
        Launches.Where(l => l.StartsWith(prefix)).Single();

    internal Task<GitProcessResult> Launch(GitProcessRequest request, CancellationToken ct)
    {
        var tokens = request.TokenizedArgs ?? request.Args;

        // A defensive copy: the seam hands over a per-launch dictionary, and the assertion must
        // observe exactly what THIS launch carried.
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Env)
            env[key] = value;

        lock (_gate)
            _launches.Add(new CapturedGitLaunch([.. tokens], request.WorkingDirectory, env));

        _onLaunch?.Invoke();
        return Task.FromResult(_handler(tokens));
    }
}

/// <summary>One complete git launch as production handed it to the process layer.</summary>
/// <param name="Tokens">The tokenized arguments, verbatim.</param>
/// <param name="WorkingDirectory">The launch's working directory.</param>
/// <param name="Env">A snapshot of the child environment block.</param>
internal sealed record CapturedGitLaunch(
    string[] Tokens, string WorkingDirectory, IReadOnlyDictionary<string, string?> Env)
{
    /// <summary>Whether the tokens start with <paramref name="prefix"/>.</summary>
    internal bool StartsWith(params string[] prefix)
    {
        if (Tokens.Length < prefix.Length) return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(Tokens[i], prefix[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>The value of one child-environment variable, or <c>null</c> when absent.</summary>
    internal string? EnvValue(string name) => Env.TryGetValue(name, out var value) ? value : null;

    /// <summary>Whether <paramref name="needle"/> occurs in ANY token.</summary>
    internal bool AnyTokenContains(string needle) =>
        Tokens.Any(t => t.Contains(needle, StringComparison.Ordinal));

    /// <summary>The environment variable NAMES whose value contains <paramref name="needle"/>.</summary>
    internal IReadOnlyList<string> EnvNamesContaining(string needle) =>
        [.. Env.Where(kv => kv.Value is not null && kv.Value.Contains(needle, StringComparison.Ordinal))
              .Select(kv => kv.Key)
              .OrderBy(k => k, StringComparer.Ordinal)];
}

/// <summary>
/// A fake <see cref="WorkerConfigProvisioner"/> BACKING: an in-memory environment plus a counting
/// fetch delegate. The provisioner itself is sealed, so the fetch count is the exact proxy for
/// the number of <c>EnsureProvisionedAsync</c> calls — each one performs exactly one fetch.
/// </summary>
internal sealed class ProvisionerHarness
{
    private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _fetchCount;
    private readonly Action? _onFetch;

    internal ProvisionerHarness(string? configRepoUrl = null, string? ghToken = null, Action? onFetch = null)
    {
        if (configRepoUrl is not null)
            _env[WorkerConfigProvisioner.ConfigRepoUrlVar] = configRepoUrl;
        if (ghToken is not null)
            _env[WorkerConfigProvisioner.GhTokenVar] = ghToken;

        _onFetch = onFetch;

        Provisioner = new WorkerConfigProvisioner(
            "worker-1",
            (_, _) =>
            {
                Interlocked.Increment(ref _fetchCount);
                _onFetch?.Invoke();
                return Task.FromResult(new GetWorkerConfigResponse());
            },
            Read,
            Write);
    }

    /// <summary>The provisioner under test — never touches the PROCESS environment.</summary>
    internal WorkerConfigProvisioner Provisioner { get; }

    /// <summary>The number of provisioning fetches, i.e. of <c>EnsureProvisionedAsync</c> calls.</summary>
    internal int FetchCount => Volatile.Read(ref _fetchCount);

    private string? Read(string name)
    {
        lock (_gate) return _env.TryGetValue(name, out var value) ? value : null;
    }

    private void Write(string name, string? value)
    {
        lock (_gate)
        {
            if (value is null) _env.Remove(name);
            else _env[name] = value;
        }
    }
}

/// <summary>
/// An <see cref="IAgentRunner"/> whose <c>SendPromptAsync</c> runs a test callback. The callback
/// is the OBSERVATION POINT for state that must hold WHILE the executor runs — most importantly
/// the askpass helper, which the seam only deletes on disposal after the executor is done.
/// </summary>
internal sealed class CallbackAgentRunner(Action? onPrompt = null) : IAgentRunner
{
    private int _promptCount;

    /// <summary>How many times the executor reached the agent prompt.</summary>
    internal int PromptCount => Volatile.Read(ref _promptCount);

    public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
    {
        Interlocked.Increment(ref _promptCount);
        onPrompt?.Invoke();
        return Task.FromResult("done");
    }

    public TestResultReport? LastTestReport => null;
    public WorkerReport? LastWorkerReport => null;
    public void ClearTestReport() { }
    public void ClearWorkerReport() { }
    public void SetToolBridge(IToolCallBridge? bridge) { }
    public void SetCurrentTaskId(string? taskId) { }
    public void SetCurrentGoalId(string? goalId) { }
    public void SetTesterReport(string? report) { }
    public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
    public void SetSession(object? session) { }
    public object? GetSession() => null;
    public void SetMaxContextTokens(int maxTokens) { }
    public int GetContextUsagePercent() => 0;
    public void SetCompactionModel(string? model) { }
    public void SetCompactionMaxTokens(int? maxTokens) { }
    public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
    public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
        => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Feeds scripted orchestrator messages into the real loop.</summary>
internal sealed class ScriptedResponseStream : IAsyncStreamReader<OrchestratorMessage>
{
    private readonly Channel<OrchestratorMessage> _channel = Channel.CreateUnbounded<OrchestratorMessage>();

    public OrchestratorMessage Current { get; private set; } = null!;

    public void Push(OrchestratorMessage message) => _channel.Writer.TryWrite(message);

    public void Complete() => _channel.Writer.TryComplete();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
            return false;

        if (!_channel.Reader.TryRead(out var message))
            return false;

        Current = message;
        return true;
    }
}

/// <summary>
/// Completes <see cref="FirstReady"/> when the worker emits its first <c>WorkerReady</c> — the
/// deterministic "the assignment body has finished" gate.
/// </summary>
internal sealed class ReadyGateRequestStream : IClientStreamWriter<WorkerMessage>
{
    private readonly TaskCompletionSource _firstReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completeCount;

    public Task FirstReady => _firstReady.Task;

    /// <summary>The number of <c>TaskComplete</c> messages written.</summary>
    public int CompleteCount => Volatile.Read(ref _completeCount);

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(WorkerMessage message)
    {
        if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Complete)
            Interlocked.Increment(ref _completeCount);

        if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Ready)
            _firstReady.TrySetResult();

        return Task.CompletedTask;
    }

    Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WriteAsync(message);
    }

    public Task CompleteAsync() => Task.CompletedTask;
}
