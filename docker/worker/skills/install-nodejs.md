---
name: install-nodejs
description: Install additional Node.js package managers (pnpm, yarn, bun)
---

# Node.js Package Managers

Node.js and npm are **already pre-installed** in this container.

## Check Current Versions

```bash
node --version
npm --version
```

## Install pnpm

```bash
npm install -g pnpm
pnpm --version
```

## Install yarn

```bash
npm install -g yarn
yarn --version
```

## Install bun

```bash
curl -fsSL https://bun.sh/install | bash
export PATH="$HOME/.bun/bin:$PATH"
bun --version
```
