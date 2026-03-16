#!/usr/bin/env bash
set -euo pipefail

# --- Graceful shutdown ---
shutdown() {
    echo "[entrypoint] Received shutdown signal, stopping services..."
    if [[ -n "${APP_PID:-}" ]]; then
        kill -TERM "$APP_PID" 2>/dev/null || true
        wait "$APP_PID" 2>/dev/null || true
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
COPILOT_PORT="${COPILOT_PORT:-8100}"
COPILOT_LOG_LEVEL="${COPILOT_LOG_LEVEL:-info}"
COPILOT_MODEL="${COPILOT_MODEL:-claude-sonnet-4.6}"

# --- Startup info ---
echo "============================================"
echo " CopilotHive Orchestrator + Brain"
echo "============================================"
echo " gRPC port:    ${GRPC_PORT:-5000}"
echo " Health port:  $(( ${GRPC_PORT:-5000} + 1 ))"
echo " Brain port:   ${COPILOT_PORT}"
echo " Brain model:  ${COPILOT_MODEL}"
echo "============================================"

# --- Launch Copilot headless for the Brain ---
COPILOT_ARGS=(
    --add-dir /tmp/brain-workspace
    --headless
    --port "$COPILOT_PORT"
    --model "$COPILOT_MODEL"
)

# Create an empty workspace so Copilot has nothing to explore
mkdir -p /tmp/brain-workspace

echo "[entrypoint] Starting: copilot ${COPILOT_ARGS[*]}"

COPILOT_LOG_LEVEL="${COPILOT_LOG_LEVEL}" copilot "${COPILOT_ARGS[@]}" &
COPILOT_PID=$!

echo "[entrypoint] Copilot Brain started with PID ${COPILOT_PID}"

# --- Wait for Copilot to be ready ---
echo "[entrypoint] Waiting for Copilot Brain to be ready on port ${COPILOT_PORT}..."
for i in $(seq 1 60); do
    if bash -c "echo > /dev/tcp/localhost/${COPILOT_PORT}" 2>/dev/null; then
        echo "[entrypoint] Copilot Brain is ready."
        break
    fi
    if ! kill -0 "$COPILOT_PID" 2>/dev/null; then
        echo "[entrypoint] Copilot exited before becoming ready."
        exit 1
    fi
    sleep 1
done

# --- Launch the .NET orchestrator ---
echo "[entrypoint] Starting CopilotHive Orchestrator"

# Pass the Brain port as env var so the .NET app can connect
export BRAIN_COPILOT_PORT="${COPILOT_PORT}"

dotnet CopilotHive.dll --port="${GRPC_PORT:-5000}" "$@" &
APP_PID=$!
echo "[entrypoint] Orchestrator started with PID ${APP_PID}"

# --- Wait for either process to exit ---
wait -n "$COPILOT_PID" "$APP_PID" 2>/dev/null || true
EXIT_CODE=$?

echo "[entrypoint] Process exited with code ${EXIT_CODE}"
exit "$EXIT_CODE"
