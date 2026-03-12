---
name: install-zig
description: Install the Zig compiler
---

# Install Zig

## Check If Already Installed

```bash
zig version
```

If this prints a version number, Zig is already installed.

## Install Zig

Download the latest release, extract it, and symlink the binary:

```bash
ZIG_VERSION="0.14.1"
ZIG_ARCH="$(uname -m)"
if [ "$ZIG_ARCH" = "x86_64" ]; then ZIG_ARCH="x86_64"; elif [ "$ZIG_ARCH" = "aarch64" ]; then ZIG_ARCH="aarch64"; fi

curl -fsSL "https://ziglang.org/download/${ZIG_VERSION}/zig-linux-${ZIG_ARCH}-${ZIG_VERSION}.tar.xz" \
    | tar -xJ -C /usr/local

ln -sf "/usr/local/zig-linux-${ZIG_ARCH}-${ZIG_VERSION}/zig" /usr/local/bin/zig
```

## Verify

```bash
zig version
```
