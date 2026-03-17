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

Verify:

```bash
dotnet --version
dotnet --list-sdks
```
