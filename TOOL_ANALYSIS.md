# CopilotHive Tool Handling & Custom Tool Registration Analysis

## Executive Summary

CopilotHive uses the **GitHub Copilot SDK** (v0.1.32) to communicate with Copilot CLI running in headless mode. The SDK provides:
- **Session-based conversation** with event-driven architecture
- **Permission handler callbacks** for tool/command execution (interception point!)
- **CustomAgentConfig** with Tool whitelisting support
- Only three observable events: AssistantMessageEvent, SessionIdleEvent, SessionErrorEvent

**Current limitation:** The SDK does NOT expose tool call interception events (like ToolCallEvent or ToolRequestEvent). However, the **permission handler is a viable interception point** for custom tool logic.

---

## 1. CopilotRunner Configuration & Tool Registration

**File:** C:\Projects\Personal\CopilotHive\src\CopilotHive.Worker\CopilotRunner.cs

### Configuration Objects

\\\csharp
private CustomAgentConfig? _customAgent;
private string? _role;

private SessionConfig BuildSessionConfig() => new()
{
    Streaming = false,
    OnPermissionRequest = GetPermissionHandlerForRole(_role),
    CustomAgents = _customAgent is not null ? [_customAgent] : [],
};
\\\

### Custom Agent Setup (Lines 41-54)

\\\csharp
public void SetCustomAgent(string role, string agentsMdContent)
{
    _role = role;
    _customAgent = new CustomAgentConfig
    {
        Name = role,
        DisplayName = \CopilotHive \\\,
        Description = \CopilotHive worker agent for the \ role\,
        Prompt = agentsMdContent,
        Tools = GetToolsForRole(role),  // ← Tool whitelist/definition
    };
}
\\\

### Tool Whitelist per Role (Lines 63-68)

\\\csharp
internal static List<string>? GetToolsForRole(string? role) => role switch
{
    "improver" => [],       // NO tools — text-only
    _ => null,              // coder, tester, reviewer get ALL tools
};
\\\

**Key insight:** 
- Tool list can be 
ull (approve all) or an empty/filtered list
- Currently uses **system tools only** (shell commands, file I/O)
- No custom tool definitions yet

---

## 2. SDK Types & Tool References

### Package References
- **GitHub.Copilot.SDK** v0.1.32 (NuGet)
- **Types from GitHub.Copilot.SDK namespace:**
  - CopilotClient — main SDK client
  - CopilotSession — conversation session
  - CustomAgentConfig — agent definition (includes Tools property)
  - SessionConfig — session configuration (includes OnPermissionRequest)
  - MessageOptions — send message (just Prompt property)
  - PermissionRequestHandler — permission callback
  - PermissionRequestResult / PermissionRequestResultKind — permission decision

### What's Missing from Observable SDK

The SDK exposes only **3 event types** at runtime:

1. **AssistantMessageEvent** — Model produced a response
2. **SessionIdleEvent** — Session is ready (task complete)
3. **SessionErrorEvent** — Error occurred

There is **NO ToolCallEvent or ToolRequestEvent** visible to the caller. The SDK may handle tool calls internally, but it doesn't expose them as observable events.

---

## 3. Event Handling Pattern

**File:** C:\Projects\Personal\CopilotHive\src\CopilotHive.Worker\CopilotRunner.cs (Lines 161-175)

\\\csharp
var done = new TaskCompletionSource<string>();
var response = "";

using var subscription = _session.On(evt =>
{
    switch (evt)
    {
        case AssistantMessageEvent msg:
            response = msg.Data.Content;
            break;
        case SessionIdleEvent:
            done.TrySetResult(response);
            break;
        case SessionErrorEvent err:
            done.TrySetException(new CopilotRunnerException(err.Data.Message));
            break;
    }
});

await _session.SendAsync(new MessageOptions { Prompt = prompt });
return await done.Task;
\\\

### Event Flow

1. **Subscribe** to events via \session.On(callback)\
2. **Send** prompt via \session.SendAsync(new MessageOptions { Prompt = ... })\
3. **Listen** for:
   - Model response → capture in \AssistantMessageEvent.Data.Content\
   - Session idle → task complete (resolve TaskCompletionSource)
   - Error → propagate exception

**Limitation:** No opportunity to intercept tool calls between SendAsync and the final AssistantMessageEvent.

---

## 4. Permission Handler Pattern (Interception Point ✓)

**File:** C:\Projects\Personal\CopilotHive\src\CopilotHive.Worker\CopilotRunner.cs (Lines 70-91)

This **IS an interception point** for tool calls!

\\\csharp
internal static PermissionRequestHandler GetPermissionHandlerForRole(string? role) => role switch
{
    "improver" => DenyAllPermissions,
    "reviewer" => ReviewerPermissions,
    _ => PermissionHandler.ApproveAll,  // coder, tester
};

private static readonly PermissionRequestHandler DenyAllPermissions =
    (_, _) => Task.FromResult(new PermissionRequestResult
    {
        Kind = PermissionRequestResultKind.DeniedByRules,
    });

private static readonly PermissionRequestHandler ReviewerPermissions =
    (request, _) => Task.FromResult(new PermissionRequestResult
    {
        Kind = request.Kind == "write"
            ? PermissionRequestResultKind.DeniedByRules
            : PermissionRequestResultKind.Approved,
    });
\\\

### How Permission Handler Works

1. **Signature:** \(PermissionRequest request, context?) → Task<PermissionRequestResult>\
2. **Request object** contains:
   - \equest.Kind\ — e.g., "read", "write", "shell", etc.
   - (Possibly tool name, but not verified in current code)
3. **Response options:**
   - \PermissionRequestResultKind.Approved\ → allow tool call
   - \PermissionRequestResultKind.DeniedByRules\ → block tool call
   - Other kinds (e.g., RequiresApproval, etc.)

**Usable for custom tools?** 
- YES, but only for **filtering/denying** existing system tools
- Cannot directly define new tools via permission handler
- Could use this to detect tool call patterns and route to custom logic

---

## 5. gRPC Protocol for Bidirectional Communication

**File:** C:\Projects\Personal\CopilotHive\src\CopilotHive.Shared\Protos\hive.proto

### Current Messages

The worker-orchestrator gRPC stream supports:

**Worker → Orchestrator:**
- \TaskProgress\ — status update (message, percent complete)
- \TaskComplete\ — final result with git status & metrics
- \WorkerReady\ — ready for next task

**Orchestrator → Worker:**
- \TaskAssignment\ — new task
- \CancelTask\ — cancel running task
- \UpdateAgents\ — update agent AGENTS.md

### For Custom "ask_orchestrator" Tool

**Protocol requirement:** Add new message types to support mid-task questions:

\\\proto
message WorkerMessage {
  oneof payload {
    TaskProgress progress = 2;
    TaskComplete complete = 3;
    WorkerReady ready = 4;
    ToolCall tool_call = 5;        // ← NEW: model wants to call a tool
  }
}

message OrchestratorMessage {
  oneof payload {
    TaskAssignment assignment = 2;
    CancelTask cancel = 3;
    UpdateAgents update_agents = 4;
    ToolResult tool_result = 5;    // ← NEW: orchestrator provides tool result
  }
}

message ToolCall {
  string tool_name = 1;     // e.g., "ask_orchestrator"
  string args_json = 2;     // JSON-encoded arguments
  string call_id = 3;       // Correlation ID
}

message ToolResult {
  string call_id = 1;       // Match the ToolCall
  string result_json = 2;   // JSON-encoded result
  bool success = 3;
  string error = 4;         // If !success
}
\\\

### Worker-Side Flow

1. Model wants to call \sk_orchestrator(question="...")\
2. Permission handler intercepts or model emits tool call
3. Worker sends \WorkerMessage { ToolCall }\ to orchestrator
4. **Worker waits** for \OrchestratorMessage { ToolResult }\
5. Worker re-injects result into Copilot session (or resumes model)
6. Model continues with the answer
7. Final \TaskComplete\ is sent

---

## 6. Existing Tool Infrastructure

**Current Status:** MINIMAL

### System Tools Only
- File read/write
- Shell commands (git, dotnet, etc.)
- All managed via \PermissionRequestHandler\

### No Custom Tool Definitions Found
- No MCP server references
- No \@tool\ decorators
- No \FunctionDefinition\ types used
- No existing \sk_orchestrator\ or similar custom tools

### Tool Whitelisting Exists
- \CustomAgentConfig.Tools\ property can filter tool names
- Currently set to \[] (improver) or \
ull (all others)

---

## Summary: Can We Register Custom Tools?

### Current SDK Capabilities ✓

| Capability | Status | How |
|---|---|---|
| Whitelist/deny system tools | ✓ Yes | \PermissionRequestHandler\ |
| Define custom agent with instructions | ✓ Yes | \CustomAgentConfig.Prompt\ |
| Intercept tool permissions | ✓ Yes | \PermissionRequestHandler\ callback |
| Capture model response | ✓ Yes | \AssistantMessageEvent\ |
| Filter tools per role | ✓ Yes | \CustomAgentConfig.Tools\ list |

### Current SDK Limitations ✗

| Limitation | Impact | Workaround |
|---|---|---|
| No tool call events exposed | Can't see when model wants to use a tool | Use permission handler to detect/log |
| No custom tool definitions in SDK | Can't register new tool schemas | Would need SDK feature or wrapper |
| \MessageOptions\ is prompt-only | Can't send tool results back | Use gRPC messages instead |
| No streaming tool use-cases | Can't do mid-conversation tool calls with feedback | Must wait for final response |

### Design Path Forward ✓

To implement \sk_orchestrator\ custom tool:

1. **Permission Handler Enhancement:**
   - Detect tool name/pattern in permission request
   - Return special response to signal custom tool
   - Or deny and capture call details

2. **Copilot Session Wrapper:**
   - Extend event subscription to catch custom tool patterns
   - Parse tool call from error message or response metadata
   - Halt the model's response

3. **gRPC Bridge:**
   - New message types: \ToolCall\, \ToolResult\
   - Worker sends call via gRPC to orchestrator
   - Orchestrator processes and sends back result
   - Worker re-injects result into model

4. **Model Instruction Injection:**
   - Update AGENTS.md with tool definition/examples
   - Model learns to call \sk_orchestrator(question)\
   - (SDK doesn't validate tool existence)

---

## Code Locations Reference

| Component | File | Lines |
|---|---|---|
| Custom agent config | CopilotRunner.cs | 40-54 |
| Session config | CopilotRunner.cs | 56-61 |
| Tool whitelist | CopilotRunner.cs | 63-68 |
| Permission handlers | CopilotRunner.cs | 70-91 |
| Event subscription | CopilotRunner.cs | 161-175 |
| SendAsync pattern | CopilotRunner.cs | 179 |
| — | — | — |
| Permission in Orchestrator | DistributedBrain.cs | 46-50, 128-130 |
| SendToSessionAsync | DistributedBrain.cs | 592-619 |
| Event handling examples | DistributedBrain.cs | 597-611 |
| — | — | — |
| gRPC message types | hive.proto | 46-62 |
| Tool filtering config | hive.proto | 75-76 |

---

## Recommendation

**The GitHub Copilot SDK is designed for:**
- Interactive conversations with models
- Permission-based tool filtering (not custom tool definitions)
- Session management and event listening

**For custom sk_orchestrator tool:**
1. Use **permission handler to detect** the tool call (e.g., block "unknown_tool" and log details)
2. Extend **gRPC protocol** with ToolCall/ToolResult messages
3. **Instruct the model** (via AGENTS.md) on tool format and usage
4. Build a **worker-side tool dispatcher** that intercepts and bridges to orchestrator

The SDK doesn't prevent this — it just doesn't provide a native "register custom tool" API. You'll build it on top of the existing permission + event + gRPC foundations.
