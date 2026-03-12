---
name: install-go
description: Install the Go toolchain
---

# Install Go

## Check If Already Installed

```bash
go version
```

If this prints a version number, Go is already installed.

## Install Go

Download the latest release from go.dev:

```bash
GO_VERSION="1.24.4"
GO_ARCH="$(dpkg --print-architecture)"

curl -fsSL "https://go.dev/dl/go${GO_VERSION}.linux-${GO_ARCH}.tar.gz" \
    | tar -xz -C /usr/local
```

## Set Environment Variables

```bash
export GOROOT="/usr/local/go"
export GOPATH="$HOME/go"
export PATH="$GOROOT/bin:$GOPATH/bin:$PATH"
```

## Verify

```bash
go version
go env GOROOT GOPATH
```
