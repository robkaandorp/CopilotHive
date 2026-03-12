# CopilotHive — Docker Compose Deployment

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) 24+
- [Docker Compose](https://docs.docker.com/compose/install/) v2+
- A GitHub Personal Access Token (PAT) with Copilot permissions

## Quick Start

```bash
cd docker
cp .env.example .env
# Edit .env and set your GH_TOKEN
docker compose up --build
```

This starts:

| Service        | Role       | Model              |
|---------------|------------|---------------------|
| `orchestrator` | gRPC server | —                  |
| `coder`        | Code writer | claude-opus-4.6    |
| `reviewer`     | Code review | gpt-5.3-codex     |
| `tester`       | Test runner | claude-sonnet-4.6  |
| `improver`     | Self-improve| claude-sonnet-4.6  |

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                  Docker Network: hive                 │
│                                                      │
│   ┌──────────────┐                                   │
│   │ orchestrator  │◄──── gRPC (port 5000) ────┐      │
│   │  /health      │                           │      │
│   └──────────────┘                           │      │
│          ▲  ▲  ▲  ▲                          │      │
│          │  │  │  │                          │      │
│   ┌──────┘  │  │  └──────┐                   │      │
│   │         │  │         │                   │      │
│ ┌─┴──┐ ┌───┴┐ ┌┴────┐ ┌─┴───────┐           │      │
│ │coder│ │test│ │revw │ │improver │           │      │
│ └────┘ └────┘ └─────┘ └─────────┘           │      │
│                                              │      │
└──────────────────────────────────────────────┘      │
                                                      │
                              Host port 5000 ─────────┘
```

Workers register with the orchestrator over gRPC, receive tasks via bidirectional
streaming, and execute them using the local Copilot CLI running in headless mode.

## Scaling Workers

Scale any role horizontally:

```bash
docker compose up --scale coder=3 --scale tester=2 --build
```

## Docker Swarm Deployment

For production clusters:

```bash
docker stack deploy -c docker-compose.yml copilothive
```

Scale services in Swarm:

```bash
docker service scale copilothive_coder=5 copilothive_tester=3
```

## Environment Variables

### Required

| Variable   | Description                                    |
|-----------|------------------------------------------------|
| `GH_TOKEN` | GitHub PAT with Copilot permissions (required) |

### Worker Configuration

| Variable           | Default            | Description                                |
|-------------------|--------------------|--------------------------------------------|
| `ORCHESTRATOR_URL` | —                  | gRPC URL of the orchestrator               |
| `WORKER_ROLE`      | —                  | Worker role: `coder`, `reviewer`, `tester`, `improver` |
| `COPILOT_MODEL`    | —                  | AI model to use for this worker            |
| `COPILOT_PORT`     | `8000`             | Port for headless Copilot CLI              |
| `COPILOT_LOG_LEVEL`| `info`             | Copilot CLI log level                      |
| `COPILOT_RESUME`   | `false`            | Resume previous Copilot session            |
| `WORKER_ID`        | auto-generated     | Unique identifier for the worker           |
| `WORKER_CAPABILITIES` | —              | Comma-separated list of extra capabilities |

## Volumes

| Volume              | Mount Point    | Description                     |
|--------------------|----------------|---------------------------------|
| `orchestrator-state` | `/app/state`   | SQLite persistence for metrics and state |

## Secrets Management (Production)

For production deployments, avoid `.env` files. Use Docker secrets instead:

```bash
# Create the secret
echo "ghp_your_token" | docker secret create gh_token -

# Reference in docker-compose.yml (Swarm mode):
# services:
#   orchestrator:
#     secrets:
#       - gh_token
#     environment:
#       - GH_TOKEN_FILE=/run/secrets/gh_token
# secrets:
#   gh_token:
#     external: true
```

## Troubleshooting

**Orchestrator health check failing:**
```bash
docker compose logs orchestrator
docker compose exec orchestrator curl http://localhost:5000/health
```

**Worker not connecting:**
```bash
docker compose logs coder
# Verify the orchestrator is reachable from the worker network
docker compose exec coder curl http://orchestrator:5000/health
```

**Rebuild from scratch:**
```bash
docker compose down -v
docker compose build --no-cache
docker compose up
```
