---
name: smoke-test
description: Full reset, rebuild, and launch a smoke test with specified goals.
---

# Smoke Test Skill

Performs a complete environment reset and launches a smoke test. **Order and timing matter** — follow these steps exactly.

## Prerequisites

- Docker must be running
- CopilotHive repo at `C:\Projects\Personal\CopilotHive`
- CopilotHive-Config repo at `C:\Projects\Personal\CopilotHive-Config`

## Steps (execute in order)

### 1. Commit any pending changes

```powershell
cd C:\Projects\Personal\CopilotHive
git status --short
# If there are changes, stage and commit them with a descriptive message
```

### 2. Stop and remove containers (preserve volumes)

```powershell
cd C:\Projects\Personal\CopilotHive\docker
docker compose down
```

Wait for full removal before proceeding.

> **Do NOT use `-v`** — persistent volumes contain `goals.db`, Brain/Composer session state,
> and pipeline history that must survive restarts.

### 3. Sync CopilotHive repo

```powershell
cd C:\Projects\Personal\CopilotHive
git pull --no-rebase origin develop
git push origin develop
```

If push is rejected, pull first then push. Accept default merge messages.

### 4. Delete old smoke test branches

Remove any `copilothive/*` remote branches for goals being re-queued:

```powershell
git push origin --delete copilothive/<goal-id>
```

### 5. Sync config repo and set goals

```powershell
cd C:\Projects\Personal\CopilotHive-Config
git pull
```

Edit `goals.yaml`:
- Remove all completed/failed goals (or keep for history)
- Add new goals with `status: pending`
- Commit and push

### 6. Build the project

```powershell
cd C:\Projects\Personal\CopilotHive
dotnet build CopilotHive.slnx
```

Must succeed with 0 errors before proceeding.

### 7. Rebuild containers

```powershell
cd C:\Projects\Personal\CopilotHive\docker
docker compose build
```

This ensures containers have the latest code.

### 8. Start containers

```powershell
cd C:\Projects\Personal\CopilotHive\docker
docker compose up -d
```

### 9. Verify

```powershell
docker ps --format "table {{.Names}}\t{{.Status}}"
```

All containers should show "Up" status. The orchestrator should show "(healthy)".

### 10. Monitor

Check orchestrator logs for goal pickup:

```powershell
docker logs docker-orchestrator-1 2>&1 | Select-String -Pattern "goal|Goal" | Select-Object -Last 10
```

## Notes

- Goals are stored in SQLite (`goals.db`) on a persistent volume — they survive container restarts
- On first startup, goals are imported from `goals.yaml` into SQLite (one-time bootstrap)
- Workers register with the orchestrator via gRPC and receive task assignments
- Rate limits can cause failures — if hit, wait the indicated time and re-run
- The `seq` container (logging) persists across resets — no need to restart it
- To fully reset state (goals, sessions, pipelines), use `docker compose down -v` then re-create
