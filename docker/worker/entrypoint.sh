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
COPILOT_LOG_LEVEL="${COPILOT_LOG_LEVEL:-info}"
COPILOT_RESUME="${COPILOT_RESUME:-false}"
COPILOT_MODEL="${COPILOT_MODEL:-}"

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

# --- Build command ---
COPILOT_ARGS=(
    --add-dir /copilot-home
    --headless
    --port "$COPILOT_PORT"
)

if [[ "${COPILOT_RESUME}" == "true" ]]; then
    COPILOT_ARGS+=(--resume)
fi

echo "[entrypoint] Starting: copilot ${COPILOT_ARGS[*]}"

# --- Launch Copilot in the background ---
COPILOT_LOG_LEVEL="${COPILOT_LOG_LEVEL}" copilot "${COPILOT_ARGS[@]}" &
COPILOT_PID=$!

echo "[entrypoint] Copilot started with PID ${COPILOT_PID}"

# --- Tail logs to stdout if the log directory exists ---
(
    sleep 2
    LOG_DIR="${HOME}/.copilot/logs"
    if [[ -d "${LOG_DIR}" ]]; then
        echo "[entrypoint] Tailing Copilot logs from ${LOG_DIR}"
        tail -F "${LOG_DIR}"/*.log 2>/dev/null || true
    fi
) &

# --- Start CopilotHive Worker client (connects to orchestrator) ---
ORCHESTRATOR_URL="${ORCHESTRATOR_URL:-}"
WORKER_ROLE="${WORKER_ROLE:-}"

if [[ -n "${ORCHESTRATOR_URL}" && -n "${WORKER_ROLE}" ]]; then
    echo "[entrypoint] Waiting for Copilot to be ready on port ${COPILOT_PORT}..."
    for i in $(seq 1 30); do
        if curl -sf "http://localhost:${COPILOT_PORT}" >/dev/null 2>&1; then
            echo "[entrypoint] Copilot is ready."
            break
        fi
        if ! kill -0 "$COPILOT_PID" 2>/dev/null; then
            echo "[entrypoint] Copilot exited before becoming ready."
            exit 1
        fi
        sleep 1
    done

    echo "[entrypoint] Starting CopilotHive Worker (role=${WORKER_ROLE}, orchestrator=${ORCHESTRATOR_URL})"
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
