#!/usr/bin/env bash
set -euo pipefail

# --- Graceful shutdown ---
shutdown() {
    echo "[entrypoint] Received shutdown signal, stopping worker..."
    if [[ -n "${WORKER_PID:-}" ]]; then
        kill -TERM "$WORKER_PID" 2>/dev/null || true
        wait "$WORKER_PID" 2>/dev/null || true
    fi
    echo "[entrypoint] Shutdown complete."
    exit 0
}
trap shutdown SIGTERM SIGINT

# --- Configuration ---
VERBOSE_LOGGING="${VERBOSE_LOGGING:-false}"

# --- Startup info ---
echo "============================================"
echo " CopilotHive Worker Container"
echo "============================================"
echo " Mode:      SharpCoder (direct LLM)"
echo " Provider:  ${LLM_PROVIDER:-copilot}"
echo "============================================"

# --- Config repo clone (needed for improver tasks on any generic worker) ---
CONFIG_REPO_DIR="/config-repo"

if [[ -n "${CONFIG_REPO_URL:-}" ]]; then
    CONFIG_CLONE_URL="${CONFIG_REPO_URL}"
    if [[ -n "${GH_TOKEN:-}" && "${CONFIG_CLONE_URL}" == https://github.com/* ]]; then
        CONFIG_CLONE_URL="${CONFIG_CLONE_URL/https:\/\/github.com\//https://x-access-token:${GH_TOKEN}@github.com/}"
    fi
    if [[ -d "${CONFIG_REPO_DIR}/.git" ]]; then
        echo "[entrypoint] Config repo already exists, pulling latest..."
        git -C "${CONFIG_REPO_DIR}" fetch --all --prune
        git -C "${CONFIG_REPO_DIR}" reset --hard origin/$(git -C "${CONFIG_REPO_DIR}" rev-parse --abbrev-ref HEAD)
    else
        rm -rf "${CONFIG_REPO_DIR}"
        echo "[entrypoint] Cloning config repo..."
        git clone "${CONFIG_CLONE_URL}" "${CONFIG_REPO_DIR}"
    fi
    git -C "${CONFIG_REPO_DIR}" config user.email "copilothive-worker@local"
    git -C "${CONFIG_REPO_DIR}" config user.name "CopilotHive Worker"
    echo "[entrypoint] Config repo ready at ${CONFIG_REPO_DIR}"
fi

# --- Start CopilotHive Worker client (connects to orchestrator) ---
ORCHESTRATOR_URL="${ORCHESTRATOR_URL:-}"

if [[ -n "${ORCHESTRATOR_URL}" ]]; then
    echo "[entrypoint] Starting CopilotHive Worker (orchestrator=${ORCHESTRATOR_URL})"
    /opt/worker/CopilotHive.Worker &
    WORKER_PID=$!
    echo "[entrypoint] Worker started with PID ${WORKER_PID}"
else
    echo "[entrypoint] ERROR: ORCHESTRATOR_URL is required"
    exit 1
fi

# --- Wait for process to exit ---
wait "$WORKER_PID"
EXIT_CODE=$?

echo "[entrypoint] Worker exited with code ${EXIT_CODE}"
exit "$EXIT_CODE"
