# Copilot CLI & SDK: Tools, Permissions, and Per-Role Access Control

## Executive Summary

GitHub Copilot CLI and the Copilot SDK provide **three complementary mechanisms** for controlling which tools a worker can use:

1. **CLI-level `--allow-tool` / `--deny-tool` flags** — Pattern-based rules passed when launching the CLI process. These are the most powerful and granular option, supporting shell subcommand matching (e.g. block `git push` but allow `git diff`). Deny rules always take precedence over allow rules.

2. **SDK `PermissionRequestHandler` callback** — A programmatic handler invoked for every tool call, receiving the full `PermissionRequest` object with detailed metadata (command text, file paths, tool names). CopilotHive already uses this for role-based filtering.

3. **`CustomAgentConfig.Tools` whitelist** — A list of tool names the agent can use. `null` means all tools; an empty list means no tools. Currently all CopilotHive roles use `null`.

Additionally, **Hooks** (`.github/hooks/*.json`) provide `preToolUse` events that can allow, deny, or modify tool calls — but these are file-based and less suitable for dynamic per-role control in a Docker container.

For CopilotHive's per-role deny/allow rules, the **recommended approach** is a combination of CLI flags in the entrypoint (for shell subcommand blocking like `git push`) and the SDK `PermissionRequestHandler` (for fine-grained runtime decisions based on the full request context).

---

## Key Repositories

| Repository | Description | Key Files |
|---|---|---|
| [github/copilot-cli](https://github.com/github/copilot-cli) | The Copilot CLI binary (closed source, docs only) | `README.md`, `changelog.md` |
| [github/copilot-sdk](https://github.com/github/copilot-sdk) | Multi-platform SDK for integrating Copilot CLI into apps | `dotnet/src/Types.cs`, `dotnet/src/PermissionHandlers.cs`, `dotnet/src/Generated/SessionEvents.cs` |

---

## 1. Complete List of Built-in Tools

These are the tool names recognized by the CLI permission system[^1]:

| Tool Name | Description | Permission Kind |
|---|---|---|
| `bash` | Execute shell commands (Unix) | `shell` |
| `powershell` | Execute shell commands (Windows) | `shell` |
| `view` | Read file contents | `read` |
| `edit` | Modify file contents | `write` |
| `create` | Create new files | `write` |
| `glob` | Find files by pattern | (no permission required) |
| `grep` | Search file contents (ripgrep) | (no permission required) |
| `web_fetch` | Fetch web pages | `url` |
| `task` | Run subagent tasks | (no permission required) |
| `ask_user` | Ask the user a question | (no permission required) |
| `store_memory` | Store facts to agent memory | `memory` |

In addition, **MCP server tools** use the `mcp` permission kind with server-name-based matching, and **custom tools** registered via `AIFunction` use the `custom-tool` permission kind[^2].

### Git Support

Git is accessed via the **`shell` permission kind** — there is no dedicated "git" tool. When the model runs `git diff`, the CLI fires a `PermissionRequestShell` with:
- `FullCommandText`: the complete command string (e.g. `"git diff origin/develop...HEAD"`)
- `Commands[]`: parsed identifiers (e.g. `[{Identifier: "git", ReadOnly: true}]`)

This means **git subcommands can be blocked at the CLI level** using the `shell(git push)` pattern syntax[^3].

---

## 2. CLI-Level Permission Flags

### `--allow-tool` and `--deny-tool`

These accept patterns in the format `Kind(argument)`[^3]:

| Kind | Description | Example Patterns |
|---|---|---|
| `shell` | Shell command execution | `shell(git push)`, `shell(git:*)`, `shell` |
| `write` | File creation or modification | `write`, `write(src/*.ts)` |
| `read` | File or directory reads | `read`, `read(.env)` |
| `SERVER-NAME` | MCP server tool invocation | `MyMCP(create_issue)`, `MyMCP` |
| `url` | URL access via web-fetch or shell | `url(github.com)`, `url(https://*.api.com)` |
| `memory` | Storing facts to agent memory | `memory` |

**Key rules:**
- **Deny always wins** — even when `--allow-all` is set[^3]
- The `:*` suffix matches the command stem followed by a space: `shell(git:*)` matches `git push` and `git pull` but NOT `gitea`[^3]
- Multiple patterns use comma-separated, quoted lists: `--deny-tool='shell(git push),shell(git checkout)'`

### `--available-tools` and `--excluded-tools`

These control which tools are **offered to the model** at all (separate from permission)[^4]:
- `--available-tools=TOOL ...` — Only these tools will be available
- `--excluded-tools=TOOL ...` — These tools will not be available

### Examples for CopilotHive Roles

```bash
# Coder: can do everything EXCEPT git push/checkout/branch/switch
copilot --headless --port 8000 \
  --allow-all-tools \
  --deny-tool='shell(git push),shell(git checkout),shell(git branch),shell(git switch)'

# Reviewer: read-only (no file writes, no dangerous shell commands)
copilot --headless --port 8000 \
  --allow-tool='shell(git:*),read' \
  --deny-tool='write,shell(rm:*),shell(mv:*)'

# Improver: no shell commands at all, only file read/write
copilot --headless --port 8000 \
  --allow-tool='read,write' \
  --deny-tool='shell'

# DocWriter: read + write .md files only, no shell
copilot --headless --port 8000 \
  --allow-tool='read,write(*.md)' \
  --deny-tool='shell'
```

---

## 3. SDK `PermissionRequestHandler` (Runtime Callback)

### The PermissionRequest Type Hierarchy

The SDK exposes a polymorphic `PermissionRequest` with these variants[^2]:

```
PermissionRequest (base)
├── PermissionRequestShell     (kind: "shell")
│   ├── FullCommandText: string     — complete command text
│   ├── Intention: string           — human-readable description
│   ├── Commands[]: {Identifier, ReadOnly}  — parsed command names
│   ├── PossiblePaths[]: string     — file paths that may be accessed
│   ├── PossibleUrls[]: {Url}       — URLs that may be accessed
│   └── HasWriteFileRedirection: bool
├── PermissionRequestWrite     (kind: "write")
│   ├── FileName: string            — path being written
│   ├── Diff: string                — unified diff of changes
│   └── NewFileContents: string?    — for new files
├── PermissionRequestRead      (kind: "read")
│   └── Path: string                — file/directory being read
├── PermissionRequestMcp       (kind: "mcp")
│   ├── ServerName: string          — MCP server name
│   ├── ToolName: string            — tool name within the server
│   └── Args: object?               — tool arguments
├── PermissionRequestUrl       (kind: "url")
│   └── Url: string                 — URL being fetched
├── PermissionRequestMemory    (kind: "memory")
│   ├── Subject: string
│   ├── Fact: string
│   └── Citations: string
├── PermissionRequestCustomTool (kind: "custom-tool")
│   ├── ToolName: string
│   └── Args: object?
└── PermissionRequestHook      (kind: "hook")
    ├── ToolName: string            — tool being gated by the hook
    └── ToolArgs: object?
```

### PermissionRequestResult Options

```csharp
public readonly struct PermissionRequestResultKind
{
    public static Approved { get; }                              // Allow the operation
    public static DeniedByRules { get; }                         // Denied by configured rules
    public static DeniedInteractivelyByUser { get; }             // User explicitly denied
    public static DeniedCouldNotRequestFromUser { get; }         // No rule matched, no user
    public static NoResult { get; }                              // Leave unanswered
}
```

### Current CopilotHive Implementation

The existing handler in `CopilotRunner.cs` is simple[^5]:

```csharp
internal static PermissionRequestHandler GetPermissionHandlerForRole(string? role) => role switch
{
    "improver" => ImproverPermissions,  // deny shell, allow read/write
    "reviewer" => ReviewerPermissions,  // deny write, allow read/shell
    _ => PermissionHandler.ApproveAll,  // coder, tester, docwriter: approve all
};
```

This can be made much more granular using the rich `PermissionRequest` subtypes.

### Enhanced Handler Design (Proposed)

```csharp
internal static PermissionRequestHandler GetPermissionHandlerForRole(string? role) => role switch
{
    "coder" => CoderPermissions,
    "tester" => TesterPermissions,
    "reviewer" => ReviewerPermissions,
    "docwriter" => DocWriterPermissions,
    "improver" => ImproverPermissions,
    _ => throw new InvalidOperationException($"Unhandled role: {role}"),
};

private static readonly PermissionRequestHandler CoderPermissions = (request, _) =>
{
    // Allow everything EXCEPT destructive git commands
    if (request is PermissionRequestShell shell)
    {
        var cmd = shell.FullCommandText;
        if (cmd.StartsWith("git push") || cmd.StartsWith("git checkout") ||
            cmd.StartsWith("git branch") || cmd.StartsWith("git switch"))
            return Task.FromResult(Denied("Infrastructure handles git operations"));
    }
    return Approved();
};

private static readonly PermissionRequestHandler TesterPermissions = (request, _) =>
{
    // Same as coder — build, test, shell all allowed; no git push
    if (request is PermissionRequestShell shell)
    {
        var cmd = shell.FullCommandText;
        if (cmd.StartsWith("git push") || cmd.StartsWith("git checkout") ||
            cmd.StartsWith("git branch") || cmd.StartsWith("git switch"))
            return Task.FromResult(Denied("Infrastructure handles git operations"));
    }
    return Approved();
};

private static readonly PermissionRequestHandler ReviewerPermissions = (request, _) =>
{
    // Read-only: can run git diff and read files, but cannot write or run other commands
    if (request is PermissionRequestWrite)
        return Task.FromResult(Denied("Reviewers cannot modify files"));
    if (request is PermissionRequestShell shell)
    {
        // Only allow git read-only commands
        var cmd = shell.FullCommandText;
        if (!cmd.StartsWith("git "))
            return Task.FromResult(Denied("Reviewers can only run git commands"));
        if (cmd.StartsWith("git push") || cmd.StartsWith("git checkout") ||
            cmd.StartsWith("git branch") || cmd.StartsWith("git switch") ||
            cmd.StartsWith("git merge") || cmd.StartsWith("git rebase") ||
            cmd.StartsWith("git reset") || cmd.StartsWith("git clean") ||
            cmd.StartsWith("git rm"))
            return Task.FromResult(Denied("Reviewers can only run read-only git commands"));
    }
    return Approved();
};

private static readonly PermissionRequestHandler DocWriterPermissions = (request, _) =>
{
    // Can read any file, write only .md files, git add + commit only
    if (request is PermissionRequestWrite write)
    {
        if (!write.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Denied("DocWriter can only write .md files"));
    }
    if (request is PermissionRequestShell shell)
    {
        var cmd = shell.FullCommandText;
        if (cmd.StartsWith("git add") || cmd.StartsWith("git commit") ||
            cmd.StartsWith("git diff") || cmd.StartsWith("git log") ||
            cmd.StartsWith("git status") || cmd.StartsWith("git show"))
            return Approved();
        return Task.FromResult(Denied("DocWriter can only run git add/commit/diff/log/status"));
    }
    return Approved();
};

private static readonly PermissionRequestHandler ImproverPermissions = (request, _) =>
{
    // Can read/write *.agents.md files only, no shell, no url
    if (request is PermissionRequestShell)
        return Task.FromResult(Denied("Improver cannot execute shell commands"));
    if (request is PermissionRequestWrite write)
    {
        if (!write.FileName.EndsWith(".agents.md", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Denied("Improver can only write *.agents.md files"));
    }
    if (request is PermissionRequestUrl)
        return Task.FromResult(Denied("Improver cannot access URLs"));
    return Approved();
};
```

---

## 4. CLI Flags vs SDK Handler vs Hooks: Comparison

| Feature | CLI Flags (`--allow/deny-tool`) | SDK `PermissionRequestHandler` | Hooks (`.github/hooks/`) |
|---|---|---|---|
| **Granularity** | Pattern-based (kind + argument) | Full request object with all metadata | Tool name matching + custom script |
| **Shell subcommands** | ✅ `shell(git push)` syntax | ✅ `FullCommandText` inspection | ✅ Script can inspect args |
| **File path filtering** | ✅ `write(*.md)` | ✅ `FileName` property | ❌ Not directly |
| **Dynamic per-role** | ⚠️ Must be set at CLI launch | ✅ Callback set per session | ❌ File-based, same for all |
| **Configuration** | Entrypoint.sh / CopilotClientOptions.CliArgs | C# code in CopilotRunner | JSON files in `.github/hooks/` |
| **Override precedence** | Deny > Allow > Default | Full control in handler | Deny from any hook blocks |
| **Can modify args** | ❌ | ❌ | ✅ `modifiedArgs` field |
| **Applies to** | CLI process lifetime | Per-session | Per-repository |

### Recommendation for CopilotHive

**Use both approaches together:**

1. **CLI flags in `entrypoint.sh`** for baseline security that applies before any SDK code runs:
   ```bash
   COPILOT_ARGS+=(
     --deny-tool='shell(git push)'
     --deny-tool='shell(git checkout)'
     --deny-tool='shell(git branch)'
     --deny-tool='shell(git switch)'
   )
   ```
   This ensures NO role can ever run `git push` — the infrastructure handles that.

2. **SDK `PermissionRequestHandler`** for role-specific fine-grained control:
   - Coder: allow shell except destructive git
   - Tester: same as coder
   - Reviewer: read-only (no writes, only git read commands)
   - DocWriter: write only .md files, limited git commands
   - Improver: no shell, write only *.agents.md

3. **Hooks** are NOT recommended for CopilotHive because:
   - They're file-based (same for all roles in a repo)
   - The same Docker container serves multiple roles dynamically
   - The SDK handler is more flexible and testable

---

## 5. Passing CLI Flags via the SDK

The `CopilotClientOptions.CliArgs` property accepts additional CLI arguments[^6]:

```csharp
var options = new CopilotClientOptions
{
    CliArgs = [
        "--deny-tool=shell(git push)",
        "--deny-tool=shell(git checkout)",
        "--deny-tool=shell(git branch)",
        "--deny-tool=shell(git switch)",
    ],
    // ... other options
};
```

**However**, CopilotHive launches the CLI process via `entrypoint.sh` (not via SDK auto-start), then connects to it. So CLI flags must be added to the entrypoint, not `CopilotClientOptions`. The `CliUrl` connection approach bypasses `CliArgs`[^7].

This means **the entrypoint.sh is where CLI deny rules go**, and **the SDK handler is where per-role logic goes**.

---

## 6. Hooks Deep Dive (For Reference)

Hooks provide `preToolUse` events that can dynamically allow/deny tool calls[^8]:

```json
{
  "version": 1,
  "hooks": {
    "preToolUse": [
      {
        "type": "command",
        "bash": "echo '{\"permissionDecision\": \"deny\", \"permissionDecisionReason\": \"git push not allowed\"}'"
      }
    ]
  }
}
```

The hook script receives tool context via environment variables and can output:
- `{"permissionDecision": "allow"}` — permit the tool call
- `{"permissionDecision": "deny", "permissionDecisionReason": "reason"}` — block it
- `{"permissionDecision": "ask"}` — fall through to normal permission handling
- `{"modifiedArgs": {...}}` — substitute the tool arguments

Hook matching uses the tool names listed in section 1 (`bash`, `powershell`, `view`, `edit`, `create`, `glob`, `grep`, `web_fetch`, `task`)[^1].

---

## Confidence Assessment

| Claim | Confidence |
|---|---|
| Tool names (`bash`, `view`, `edit`, `create`, `glob`, `grep`, `web_fetch`, `task`) | ✅ High — from official docs |
| `--allow-tool` / `--deny-tool` syntax and precedence | ✅ High — from official CLI reference |
| `PermissionRequest` type hierarchy with all 8 subtypes | ✅ High — from SDK source code |
| `PermissionRequestShell.FullCommandText` for git subcommand inspection | ✅ High — from SDK generated types |
| `CopilotClientOptions.CliArgs` bypassed when using `CliUrl` | ✅ High — CopilotHive uses `CliUrl` to connect to pre-started CLI |
| Hooks `preToolUse` can allow/deny/modify | ✅ High — from official docs |
| `CustomAgentConfig.Tools` whitelist mechanism | ✅ High — from SDK source code |
| Proposed enhanced handler patterns | ⚠️ Medium — based on SDK types but untested; needs `FullCommandText` format verification |

---

## Footnotes

[^1]: [GitHub Docs: CLI Command Reference — Tool names for hook matching](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference#tool-names-for-hook-matching)

[^2]: `dotnet/src/Generated/SessionEvents.cs:3108-3121` in [github/copilot-sdk](https://github.com/github/copilot-sdk) — `PermissionRequest` polymorphic type with 8 derived types: `shell`, `write`, `read`, `mcp`, `url`, `memory`, `custom-tool`, `hook`

[^3]: [GitHub Docs: CLI Command Reference — Tool permission patterns](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference#tool-permission-patterns)

[^4]: [GitHub Docs: CLI Command Reference — `--available-tools` and `--excluded-tools`](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference#command-line-options)

[^5]: `src/CopilotHive.Worker/CopilotRunner.cs:146-181` in CopilotHive — current `GetPermissionHandlerForRole` and `GetToolsForRole` implementations

[^6]: `dotnet/src/Types.cs:77-79` in [github/copilot-sdk](https://github.com/github/copilot-sdk) — `CopilotClientOptions.CliArgs` property

[^7]: `src/CopilotHive.Worker/CopilotRunner.cs:71-74` in CopilotHive — `CopilotClientOptionsWithTelemetry` uses `CliUrl` to connect to pre-started CLI process, bypassing `CliArgs`

[^8]: [GitHub Docs: CLI Command Reference — preToolUse decision control](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference#pretooluse-decision-control)
