---
name: install-dotnet-sdk
description: Install the .NET SDK and C# language server into the worker container
---

# Install .NET SDK

## Check If Already Installed

```bash
dotnet --version
```

If this prints a version number, .NET is already installed. Skip the install steps.

## Install .NET SDK (Channel 10.0)

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
```

## Set Environment Variables

After installation, set the following so `dotnet` is available:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
```

## Install C# Language Server

After the SDK is installed, install `csharp-ls` for code intelligence:

```bash
dotnet tool install --global csharp-ls
```

## Verify

```bash
dotnet --version
dotnet --list-sdks
csharp-ls --version
```
