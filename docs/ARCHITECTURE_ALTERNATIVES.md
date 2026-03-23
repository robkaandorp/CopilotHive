# Architecture Alternatives -- Agent Runtime

CopilotHive uses **SharpCoder** for worker agent orchestration and **direct LLM API calls** (via `IChatClient`) for Brain orchestration. This document evaluates alternatives and explains the selection rationale.

## Current Architecture

**SharpCoder v0.2.0+** -- A .NET autonomous coding agent library that communicates directly with LLM providers (GitHub Copilot API, OpenAI, Ollama, etc.) via `Microsoft.Extensions.AI`.

Key features used:

| Feature | Purpose |
|---------|---------|
| `CodingAgent` | Autonomous agent loop with tool execution for workers |
| `AgentOptions` | Role-specific config (system prompts, tool permissions, work directory) |
| `EnableBash` / `EnableFileWrites` | Sandboxing for reviewer (no writes) and improver (no bash) |
| `AIFunctionFactory.Create()` | Custom tools (report_progress, report_test_results, report_verdict) |
| `IChatClient` | Brain uses direct LLM access for orchestration decisions |
| Token tracking | `AgentResult.Usage` provides InputTokenCount, OutputTokenCount |

## Evaluated Alternatives

### OpenCode (Open Source)

| Aspect | Details |
|--------|---------|
| **License** | Open source |
| **Providers** | 75+ (GitHub Copilot, Anthropic, OpenAI, OpenRouter, local via Ollama) |
| **Server Mode** | `opencode serve --port 4096` (HTTP REST API) |
| **ACP Mode** | `opencode acp` (stdin/stdout JSON-RPC via Agent Client Protocol) |
| **SDK** | TypeScript only (`@opencode-ai/sdk`) -- **no .NET SDK** |
| **Headless** | Full HTTP API with OpenAPI 3.1 spec |
| **Custom Tools** | `.opencode/tools/*.ts` + any language scripts |
| **Agents** | `.opencode/agents/*.md` with permissions |

### Claude Code (Anthropic)

| Aspect | Details |
|--------|---------|
| **License** | Proprietary (MIT license wrapper, proprietary backend) |
| **Providers** | Anthropic Claude only |
| **Server Mode** | None -- TUI only |
| **SDK** | None |
| **Headless** | Unofficial via stdin tricks |

### Aider (Open Source)

| Aspect | Details |
|--------|---------|
| **License** | Apache 2.0 (fully open source) |
| **Providers** | Any LLM (OpenAI, Anthropic, OpenRouter, Ollama, etc.) |
| **Server Mode** | None |
| **SDK** | Python API only |
| **Headless** | `--yes` flag + stdin/stdout |
| **Codebase Map** | Built-in repo map for context awareness |

### Direct LLM API

| Aspect | Details |
|--------|---------|
| **Providers** | Any (Anthropic, OpenAI, Azure, local models) |
| **SDK** | Provider-specific (.NET SDKs available) |
| **Agent Framework** | Must build from scratch |
| **Tool Execution** | Must implement entire framework |
| **Context Management** | Must implement file awareness, LSP integration |

### Agent Client Protocol (ACP)

| Aspect | Details |
|--------|---------|
| **What** | Open standard for agent-client communication (like LSP for language servers) |
| **Transport** | stdin/stdout JSON-RPC |
| **SDKs** | TypeScript, Python, Kotlin, Rust -- **no .NET SDK** |
| **Supported By** | OpenCode, Zed IDE, JetBrains |

## Comparison Matrix

| Feature | SharpCoder | OpenCode | Claude Code | Aider | Direct LLM |
|---------|------------|----------|-------------|-------|------------|
| **Native .NET** | Yes | No | No | No (Python) | Via provider |
| **Open Source** | Yes | Yes | No | Yes | N/A |
| **Multi-provider** | Yes (any IChatClient) | Yes (75+) | No (Claude only) | Yes | Yes |
| **Autonomous Agent** | Yes (CodingAgent) | Yes | Yes | Yes | Build yourself |
| **Custom Tools** | Yes (AIFunction) | Yes (tools dir) | Limited | Yes | Build yourself |
| **Permission Control** | Yes (EnableBash/EnableFileWrites) | Yes (agent config) | Limited | `--yes` | Build yourself |
| **Cost** | Pay-per-use (model cost) | Pay-per-use | $20-200/mo | Pay-per-use | Pay-per-use |

## Why SharpCoder Was Chosen

1. **Native .NET Library** -- Purpose-built .NET library, no subprocesses or HTTP bridges
2. **Multi-provider** -- Works with any `IChatClient` (Copilot API, OpenAI, Ollama, etc.)
3. **Autonomous Agent Loop** -- `CodingAgent` handles the full tool-call cycle autonomously
4. **Role-based Sandboxing** -- `EnableBash` and `EnableFileWrites` flags per role
5. **Custom Tools** -- `AIFunctionFactory.Create()` registers structured tools with validation
6. **Token Tracking** -- Built-in usage tracking for cost and context management
7. **No External Dependencies** -- No Node.js, no CLI binaries, just a NuGet package

### Historical Note

CopilotHive originally used the GitHub Copilot SDK (`GitHub.Copilot.SDK` NuGet) which required the Copilot CLI (`@github/copilot`) running as a Node.js subprocess. This was replaced by SharpCoder in March 2026 to eliminate the Node.js dependency, enable multi-provider support, and reduce container image size.

## Migration Paths (If Ever Needed)

| Target | Approach | Effort | Risk |
|--------|----------|--------|------|
| **OpenCode HTTP** | Build `OpenCodeRunner.cs` with `HttpClient` | 2 weeks | Low |
| **OpenCode ACP** | Build .NET JSON-RPC client over stdio | 3-4 weeks | Medium |
| **Direct LLM** | Already partially done (Brain uses `IChatClient`) | 4-6 weeks | Medium |
| **Aider** | Python subprocess orchestration | 2-3 weeks | Medium |

## References

- [SharpCoder NuGet](https://www.nuget.org/packages/SharpCoder)
- [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions)
- [OpenCode Server Docs](https://opencode.ai/docs/server/)
- [Agent Client Protocol Spec](https://agentclientprotocol.com)
- [CopilotHive Vision](./VISION.md)
