---
name: install-rust
description: Install the Rust toolchain via rustup
---

# Install Rust

## Check If Already Installed

```bash
rustc --version
cargo --version
```

If both commands succeed, Rust is already installed.

## Install Rust Toolchain

```bash
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
```

## Set Environment Variables

```bash
export PATH="$HOME/.cargo/bin:$PATH"
```

Or source the environment file:

```bash
source "$HOME/.cargo/env"
```

## Verify

```bash
rustc --version
cargo --version
```

## Install Additional Targets

```bash
rustup target add <target>
rustup component add clippy rustfmt
```
