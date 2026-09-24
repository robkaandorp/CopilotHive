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

The worker image already sets `DOTNET_ROOT=/root/.dotnet` and puts `/root/.dotnet` and
`/root/.dotnet/tools` on `PATH`. The SDK installer writes to `$HOME/.dotnet`, which is
`/root/.dotnet` for the root user the container runs as, so a freshly installed SDK is on
`PATH` in every new shell. No exports are needed on current images.

Each `execute_bash_command` call starts a fresh shell, so the exports only last for the
current shell and are never inherited by later calls. If `dotnet` is still not found (an
older worker image without the image-level variables), run the following in the same shell
call as any `dotnet` command that needs it:

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
