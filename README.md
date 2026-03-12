[![Build](https://github.com/robkaandorp/CopilotHive/actions/workflows/build.yml/badge.svg)](https://github.com/robkaandorp/CopilotHive/actions/workflows/build.yml)

# CopilotHive

CopilotHive is an orchestration platform for GitHub Copilot agents. It manages a pool of worker agents, distributes goals across them, and coordinates multi-repository tasks using a hive-style architecture.

## Features

- **Goal management** – Load goals from files or an API and queue them for processing.
- **Worker pool** – Spin up and manage Docker-based Copilot worker agents.
- **Multi-repo support** – Assign goals that span multiple repositories to dedicated workers.
- **gRPC communication** – Orchestrator and workers communicate over gRPC.

## Getting Started

See [`docker/README.md`](docker/README.md) for instructions on running the stack with Docker Compose.
