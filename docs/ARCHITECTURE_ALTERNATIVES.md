# Architecture Alternatives ΓÇö Agent Runtime

CopilotHive uses GitHub Copilot SDK for worker agent orchestration. This document evaluates alternatives and explains the selection rationale.

## Current Architecture

**GitHub.Copilot.SDK v0.1.32** ΓÇö Native .NET SDK connecting to Copilot CLI in headless mode via TCP/stdio.

Key features used:

| Feature | Purpose |
|---------|---------|
| `CustomAgentConfig` | Role-specific prompts (coder, tester, reviewer, improver) |
| `PermissionRequestHandler` | Sandboxing for improver/reviewer roles |
| `AIFunction.Create()` | Custom tools (report_progress, report_test_results, report_verdict) |
| Event streaming | `AssistantMessageEvent`, `SessionIdleEvent`, `SessionErrorEvent` |
| `OnUserInputRequest` | Bridge to orchestrator for ask_user tool |
| `SessionHooks` | Pre/post tool execution callbacks |

## Evaluated Alternatives

### OpenCode (Open Source)

| Aspect | Details |
|--------|---------|
| **License** | Open source |
| **Providers** | 75+ (GitHub Copilot, Anthropic, OpenAI, OpenRouter, local via Ollama) |
| **Server Mode** | `opencode serve --port 4096` (HTTP REST API) |
| **ACP Mode** | `opencode acp` (stdin/stdout JSON-RPC via Agent Client Protocol) |
| **SDK** | TypeScript only (`@opencode-ai/sdk`) ΓÇö **no .NET SDK** |
| **Headless** | Full HTTP API with OpenAPI 3.1 spec |
| **Custom Tools** | `.opencode/tools/*.ts` + any language scripts |
| **Agents** | `.opencode/agents/*.md` with permissions |

### Claude Code (Anthropic)

| Aspect | Details |
|--------|---------|
| **License** | Proprietary (MIT license wrapper, proprietary backend) |
| **Providers** | Anthropic Claude only |
| **Server Mode** | None ΓÇö TUI only |
| **SDK** | None |
| **Headless** | Unofficial via stdin tricks |
| **Stars** | 79.8k GitHub stars |

### Aider (Open Source)

| Aspect | Details |
|--------|---------|
| **License** | Apache 2.0 (fully open source) |
| **Providers** | Any LLM (OpenAI, Anthropic, OpenRouter, Ollama, etc.) |
| **Server Mode** | None |
| **SDK** | Python API only |
| **Headless** | `--yes` flag + stdin/stdout |
| **Stars** | 42.1k GitHub stars |
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
| **SDKs** | TypeScript, Python, Kotlin, Rust ΓÇö **no .NET SDK** |
| **Supported By** | OpenCode, Zed IDE, JetBrains |

## Comparison Matrix

| Feature | Copilot SDK | OpenCode | Claude Code | Aider | Direct LLM |
|---------|-------------|----------|-------------|-------|------------|
| **Native .NET SDK** | Γ£à Yes | Γ¥î No | Γ¥î No | Γ¥î Python | Γ£à Via provider |
| **Open Source** | Γ¥î No | Γ£à Yes | Γ¥î No | Γ£à Yes | N/A |
| **Multi-provider** | Γ¥î Copilot only | Γ£à 75+ | Γ¥î Claude only | Γ£à Any | Γ£à Any |
| **Server Mode** | Γ£à SDK native | Γ£à HTTP | Γ¥î No | Γ¥î No | N/A |
| **ACP Protocol** | Γ¥î No | Γ£à Yes | Γ¥î No | Γ¥î No | N/A |
| **Headless API** | Γ£à Native | Γ£à HTTP/ACP | Γ¥î No | ΓÜá∩╕Å Unofficial | Γ£à Direct |
| **Custom Tools** | Γ£à `AIFunction` | Γ£à Tools dir | ΓÜá∩╕Å Limited | Γ£à Yes | Γ£à Build yourself |
| **Permission Hooks** | Γ£à Native | Γ£à Agent config | ΓÜá∩╕Å Limited | ΓÜá∩╕Å `--yes` | Γ£à Build yourself |
| **Cost** | $10-40/mo sub | Model cost | $20-200/mo | Model cost | Pay-per-use |

## Provider Flexibility Analysis

### Cost Comparison

| Approach | Cost Model | Notes |
|----------|------------|-------|
| **Copilot SDK** | $10-40/mo subscription | Predictable per-user cost |
| **OpenCode** | Pay-per-use (model cost) | Varies by provider choice |
| **Direct Anthropic** | Pay-per-token | Higher for Sonnet/Opus |
| **Direct OpenAI** | Pay-per-token | GPT-5 pricing tiers |

### Model Lock-In

| Approach | Lock-In Risk | Mitigation |
|----------|--------------|------------|
| **Copilot SDK** | Locked to Copilot subscription | None ΓÇö vendor dependency |
| **OpenCode** | None ΓÇö swap providers via config | Change `model` in config |
| **Direct LLM** | None ΓÇö provider-specific API | Build abstraction layer |

### Platform Dependency

| Approach | Dependency | Risk |
|----------|------------|------|
| **Copilot SDK** | GitHub CLI + SDK | Breaking changes, deprecation |
| **OpenCode** | OpenCode binary | Community-maintained, lower risk |
| **Direct LLM** | Provider API | API versioning, breaking changes |

## Why Copilot SDK Was Chosen

1. **Native .NET Support** ΓÇö Purpose-built SDK, no wrappers or HTTP bridges needed
2. **Headless Design** ΓÇö `CopilotClient` designed for programmatic control
3. **Permission System** ΓÇö `PermissionRequestHandler` enables role-based sandboxing
4. **Already Integrated** ΓÇö 530 lines of working `CopilotRunner.cs` with event streaming
5. **Custom Tools** ΓÇö `AIFunction.Create()` registers `report_progress`, `report_verdict`, etc.
6. **Enterprise Support** ΓÇö GitHub maintains the SDK and CLI

## What We'd Lose Switching Away

If Copilot SDK were replaced:

| Feature | Replacement Effort |
|---------|-------------------|
| `CustomAgentConfig` | Reimplement as `.opencode/agents/*.md` or prompt templates |
| `PermissionRequestHandler` | Build HTTP middleware or ACP permission callbacks |
| `OnUserInputRequest` | Implement `ask_user` tool bridging to orchestrator |
| `SessionHooks` | Build pre/post tool execution pipeline |
| Event streaming | Replace with HTTP polling or SSE parsing |
| Native .NET types | Generate from OpenAPI spec or hand-write client |

## Migration Paths (If Ever Needed)

| Target | Approach | Effort | Risk |
|--------|----------|--------|------|
| **OpenCode HTTP** | Build `OpenCodeRunner.cs` with `HttpClient` calling `POST /session/:id/message` | 2 weeks | Low ΓÇö stable HTTP API |
| **OpenCode ACP** | Build .NET JSON-RPC client over stdio (no SDK) | 3-4 weeks | Medium ΓÇö protocol complexity |
| **Direct Anthropic** | `Microsoft.Extensions.AI` + build tool framework | 8-14 weeks | High ΓÇö full agent implementation |
| **Direct OpenAI** | Same as Anthropic | 8-14 weeks | High ΓÇö same effort |
| **Aider** | Python subprocess orchestration | 2-3 weeks | Medium ΓÇö subprocess management |

### Migration Decision Criteria

Consider migrating if:

- GitHub deprecates Copilot CLI or SDK
- Cost outweighs convenience (high-volume usage)
- Multi-provider support becomes critical
- Open-source requirement mandates change

## References

- [OpenCode Server Docs](https://opencode.ai/docs/server/)
- [OpenCode ACP Mode](https://opencode.ai/docs/acp/)
- [OpenCode Custom Tools](https://opencode.ai/docs/custom-tools/)
- [OpenCode Agents](https://opencode.ai/docs/agents/)
- [Agent Client Protocol Spec](https://agentclientprotocol.com)
- [ACP TypeScript SDK](https://agentclientprotocol.com/libraries/typescript.md)
- [GitHub Copilot SDK NuGet](https://www.nuget.org/packages/GitHub.Copilot.SDK)
- [GitHub Copilot CLI](https://github.com/github/copilot-cli)
- [Claude Code](https://github.com/anthropics/claude-code)
- [Aider](https://github.com/Aider-AI/aider)
- [CopilotHive Vision](./VISION.md)
