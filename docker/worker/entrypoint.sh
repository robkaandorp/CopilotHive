#!/usr/bin/env bash
set -euo pipefail

# --- Graceful shutdown ---
shutdown() {
    echo "[entrypoint] Received shutdown signal, stopping Copilot..."
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

# --- Wait for Copilot to exit ---
wait "$COPILOT_PID"
EXIT_CODE=$?
echo "[entrypoint] Copilot exited with code ${EXIT_CODE}"
exit "$EXIT_CODE"
