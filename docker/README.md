# CopilotHive — Docker Compose Deployment

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) 24+
- [Docker Compose](https://docs.docker.com/compose/install/) v2+
- A GitHub Personal Access Token (PAT) or LLM provider API key

## Quick Start

```bash
cd docker
cp .env.example .env
# Edit .env and set your GH_TOKEN
docker compose up --build
```

This starts:

| Service        | Description              | Replicas |
|---------------|--------------------------|----------|
| `orchestrator` | gRPC server + health API | 1        |
| `worker`       | Generic pool worker      | 4 (configurable via `WORKER_REPLICAS`) |

Workers register without a fixed role and accept any role (coder, tester, reviewer, improver) per task.

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
streaming, and execute them using SharpCoder, which communicates directly with LLM providers.

## Scaling Workers

Adjust the number of generic workers:

```bash
WORKER_REPLICAS=6 docker compose up --build
```

Or use `--scale`:

```bash
docker compose up --scale worker=6 --build
```

## Docker Swarm Deployment

For production clusters:

```bash
docker stack deploy -c docker-compose.yml copilothive
```

Scale services in Swarm:

```bash
docker service scale copilothive_worker=8
```

## Multi-Architecture Builds

Both Dockerfiles support multi-architecture builds for `amd64` and `arm64` platforms. The build uses Docker's `TARGETARCH` build argument to select the correct .NET runtime identifier.

**Build for a specific platform:**

```bash
# Build for ARM64 (e.g., Apple Silicon, AWS Graviton)
docker build --platform linux/arm64 -f docker/orchestrator/Dockerfile -t copilothive-orchestrator:arm64 .
docker build --platform linux/arm64 -f docker/worker/Dockerfile -t copilothive-worker:arm64 .
```

**Build and push multi-arch images (requires BuildKit):**

```bash
# Create a multi-arch builder (once)
docker buildx create --use --name multiarch

# Build and push both architectures
docker buildx build --platform linux/amd64,linux/arm64 \
  -f docker/orchestrator/Dockerfile \
  -t ghcr.io/your-org/copilothive-orchestrator:latest \
  --push .

docker buildx build --platform linux/amd64,linux/arm64 \
  -f docker/worker/Dockerfile \
  -t ghcr.io/your-org/copilothive-worker:latest \
  --push .
```

Without explicit `--platform`, Docker defaults to the host architecture.

## Environment Variables

### Required

| Variable   | Description                                    |
|-----------|------------------------------------------------|
| `GH_TOKEN` | GitHub PAT with Copilot permissions (required) |

### Worker Configuration

| Variable           | Default            | Description                                |
|-------------------|--------------------|--------------------------------------------|
| `ORCHESTRATOR_URL` | —                  | gRPC URL of the orchestrator               |
| `LLM_PROVIDER`     | —                  | LLM provider (e.g. `copilot`, `openai`, `ollama`) |
| `CONFIG_REPO_URL`  | —                  | Config repo URL (needed for improver tasks) |
| `WORKER_ID`        | auto-generated     | Unique identifier for the worker           |
| `WORKER_CAPABILITIES` | —              | Comma-separated list of extra capabilities |

### Orchestrator Configuration

| Variable                | Default   | Description                                       |
|------------------------|-----------|---------------------------------------------------|
| `BRAIN_MODEL`          | —         | LLM model for the orchestrator Brain              |
| `BRAIN_CONTEXT_WINDOW` | `100000`  | Max context window in tokens for the Brain        |

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
docker compose logs worker
# Verify the orchestrator is reachable from the worker network
docker compose exec worker curl http://orchestrator:5000/health
```

**Rebuild from scratch:**
```bash
docker compose down -v
docker compose build --no-cache
docker compose up
```
