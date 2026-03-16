#!/usr/bin/env bash
set -euo pipefail

# --- Graceful shutdown ---
shutdown() {
    echo "[entrypoint] Received shutdown signal, stopping services..."
    if [[ -n "${WORKER_PID:-}" ]]; then
        kill -TERM "$WORKER_PID" 2>/dev/null || true
        wait "$WORKER_PID" 2>/dev/null || true
    fi
    if [[ -n "${COPILOT_PID:-}" ]]; then
        kill -TERM "$COPILOT_PID" 2>/dev/null || true
        wait "$COPILOT_PID" 2>/dev/null || true
    fi
    echo "[entrypoint] Shutdown complete."
    exit 0
}
trap shutdown SIGTERM SIGINT

# --- Configuration ---
COPILOT_PORT="${COPILOT_PORT:-8000}"
COPILOT_LOG_LEVEL="${COPILOT_LOG_LEVEL:-warn}"
COPILOT_RESUME="${COPILOT_RESUME:-false}"
COPILOT_MODEL="${COPILOT_MODEL:-}"
VERBOSE_LOGGING="${VERBOSE_LOGGING:-false}"

# If verbose logging is enabled, default copilot to info level
if [[ "${VERBOSE_LOGGING}" == "true" && "${COPILOT_LOG_LEVEL}" == "warn" ]]; then
    COPILOT_LOG_LEVEL="info"
fi

# --- Startup info ---
echo "============================================"
echo " CopilotHive Worker Container"
echo "============================================"
echo " Mode:      headless (JSON-RPC)"
echo " Port:      ${COPILOT_PORT}"
echo " Log level: ${COPILOT_LOG_LEVEL}"
echo " Resume:    ${COPILOT_RESUME}"
if [[ -n "${COPILOT_MODEL}" ]]; then
    echo " Model:     ${COPILOT_MODEL}"
fi
echo "============================================"

# --- Config repo clone (needed for improver tasks on any generic worker) ---
CONFIG_REPO_DIR="/config-repo"
CONFIG_AGENTS_DIR="${CONFIG_REPO_DIR}/agents"

if [[ -n "${CONFIG_REPO_URL:-}" ]]; then
    echo "[entrypoint] Cloning config repo..."
    CONFIG_CLONE_URL="${CONFIG_REPO_URL}"
    if [[ -n "${GH_TOKEN:-}" && "${CONFIG_CLONE_URL}" == https://github.com/* ]]; then
        CONFIG_CLONE_URL="${CONFIG_CLONE_URL/https:\/\/github.com\//https://x-access-token:${GH_TOKEN}@github.com/}"
    fi
    git clone "${CONFIG_CLONE_URL}" "${CONFIG_REPO_DIR}"
    git -C "${CONFIG_REPO_DIR}" config user.email "copilothive-worker@local"
    git -C "${CONFIG_REPO_DIR}" config user.name "CopilotHive Worker"
    echo "[entrypoint] Config repo cloned to ${CONFIG_REPO_DIR}"
fi

# Build command (after model is printed)
COPILOT_ARGS=(
    --add-dir /copilot-home
    --headless
    --port "$COPILOT_PORT"
)

# Add config repo agents subfolder so Copilot can edit *.agents.md when acting as improver
if [[ -d "${CONFIG_AGENTS_DIR}" ]]; then
    COPILOT_ARGS+=(--add-dir "${CONFIG_AGENTS_DIR}")
    echo "[entrypoint] Added --add-dir ${CONFIG_AGENTS_DIR}"
fi

if [[ "${COPILOT_RESUME}" == "true" ]]; then
    COPILOT_ARGS+=(--resume)
fi

if [[ -n "${COPILOT_MODEL}" ]]; then
    COPILOT_ARGS+=(--model "$COPILOT_MODEL")
fi

echo "[entrypoint] Starting: copilot ${COPILOT_ARGS[*]}"

# --- Launch Copilot in the background ---
COPILOT_LOG_LEVEL="${COPILOT_LOG_LEVEL}" copilot "${COPILOT_ARGS[@]}" &
COPILOT_PID=$!

echo "[entrypoint] Copilot started with PID ${COPILOT_PID}"

# --- Tail logs to stdout only in verbose mode ---
if [[ "${VERBOSE_LOGGING}" == "true" ]]; then
    (
        sleep 2
        LOG_DIR="${HOME}/.copilot/logs"
        if [[ -d "${LOG_DIR}" ]]; then
            echo "[entrypoint] Tailing Copilot logs from ${LOG_DIR}"
            tail -F "${LOG_DIR}"/*.log 2>/dev/null || true
        fi
    ) &
fi

# --- Start CopilotHive Worker client (connects to orchestrator) ---
ORCHESTRATOR_URL="${ORCHESTRATOR_URL:-}"
WORKER_ROLE="${WORKER_ROLE:-}"

if [[ -n "${ORCHESTRATOR_URL}" ]]; then
    echo "[entrypoint] Waiting for Copilot to be ready on port ${COPILOT_PORT}..."
    for i in $(seq 1 30); do
        if bash -c "echo > /dev/tcp/localhost/${COPILOT_PORT}" 2>/dev/null; then
            echo "[entrypoint] Copilot is ready."
            break
        fi
        if ! kill -0 "$COPILOT_PID" 2>/dev/null; then
            echo "[entrypoint] Copilot exited before becoming ready."
            exit 1
        fi
        sleep 1
    done

    WORKER_MODE="${WORKER_ROLE:-generic}"
    echo "[entrypoint] Starting CopilotHive Worker (mode=${WORKER_MODE}, orchestrator=${ORCHESTRATOR_URL})"
    /opt/worker/CopilotHive.Worker &
    WORKER_PID=$!
    echo "[entrypoint] Worker started with PID ${WORKER_PID}"
fi

# --- Wait for primary process to exit ---
if [[ -n "${WORKER_PID:-}" ]]; then
    wait -n "$COPILOT_PID" "$WORKER_PID" 2>/dev/null || true
    EXIT_CODE=$?
else
    wait "$COPILOT_PID"
    EXIT_CODE=$?
fi

echo "[entrypoint] Process exited with code ${EXIT_CODE}"
exit "$EXIT_CODE"
