---
name: install-dotnet-sdk
description: How to install the .NET SDK in a fresh environment. Use this when dotnet commands are not available.
---

# Install SDK Skill

## .NET SDK Installation

Check if already installed:

```bash
dotnet --version
```

If not installed:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
```

## C# Language Server

Install `csharp-ls` for code intelligence (go-to-definition, diagnostics):

```bash
dotnet tool install --global csharp-ls
```

## Verify

```bash
dotnet --version
dotnet --list-sdks
csharp-ls --version
```
