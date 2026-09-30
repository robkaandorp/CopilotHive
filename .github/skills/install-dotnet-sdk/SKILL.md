---
name: install-dotnet-sdk
description: How to install the .NET SDK in a fresh environment. Use this when `dotnet` is not found (exit 127) before building or testing.
---

# Install .NET SDK Skill

## Check

Run:

```bash
command -v dotnet && dotnet --list-sdks
```

If an SDK for channel 10.0 is listed, stop — the prerequisite is satisfied.

Also check `"$HOME/.dotnet/dotnet" --list-sdks`: an SDK may exist on disk but not be on
`PATH`, in which case only the environment step below is needed.

```bash
"$HOME/.dotnet/dotnet" --list-sdks
```

## Install Once Per Container

Download the installer to a file with retries, then run it with an explicit install dir.
This way a failed download is reported instead of silently piping nothing into bash:

```bash
curl -fsSL --retry 5 --retry-delay 2 -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
```

Never use `curl | bash`.

## Environment

CopilotHive worker images already set `DOTNET_ROOT`/`PATH` for `/root/.dotnet`, so no
export is needed there. Each `execute_bash_command` call is a fresh shell, so in any other
environment exports do not carry over — there, prefix every `dotnet` command within the
same call with:

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH";
```

## Verify

Run in a new shell call:

```bash
dotnet --version
dotnet --list-sdks
```
