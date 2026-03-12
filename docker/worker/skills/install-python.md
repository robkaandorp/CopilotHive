---
name: install-python
description: Install Python 3 and the uv package manager
---

# Install Python + uv

## Check If Already Installed

```bash
python3 --version
uv --version
```

If both commands succeed, Python and uv are already available.

## Install Python 3

Python 3 may already be available from Ubuntu. If not:

```bash
apt-get update && apt-get install -y --no-install-recommends python3 python3-venv python3-pip
rm -rf /var/lib/apt/lists/*
```

## Install uv (Fast Python Package Manager)

```bash
curl -LsSf https://astral.sh/uv/install.sh | sh
export PATH="$HOME/.local/bin:$PATH"
```

## Verify

```bash
python3 --version
uv --version
```

## Usage

Create a virtual environment and install packages:

```bash
uv venv .venv
source .venv/bin/activate
uv pip install <package>
```
