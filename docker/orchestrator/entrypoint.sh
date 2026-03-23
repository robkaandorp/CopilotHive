#!/usr/bin/env bash
set -euo pipefail

# --- Graceful shutdown ---
shutdown() {
    echo "[entrypoint] Received shutdown signal, stopping orchestrator..."
    if [[ -n "${APP_PID:-}" ]]; then
        kill -TERM "$APP_PID" 2>/dev/null || true
        wait "$APP_PID" 2>/dev/null || true
    fi
    echo "[entrypoint] Shutdown complete."
    exit 0
}
trap shutdown SIGTERM SIGINT

# --- Startup info ---
echo "============================================"
echo " CopilotHive Orchestrator"
echo "============================================"
echo " gRPC port:    ${GRPC_PORT:-5000}"
echo " Health port:  $(( ${GRPC_PORT:-5000} + 1 ))"
echo " Brain model:  ${BRAIN_MODEL:-not set}"
echo "============================================"

# --- Launch the .NET orchestrator ---
echo "[entrypoint] Starting CopilotHive Orchestrator"

dotnet CopilotHive.dll --port="${GRPC_PORT:-5000}" "$@" &
APP_PID=$!
echo "[entrypoint] Orchestrator started with PID ${APP_PID}"

# --- Wait for process to exit ---
wait "$APP_PID"
EXIT_CODE=$?

echo "[entrypoint] Orchestrator exited with code ${EXIT_CODE}"
exit "$EXIT_CODE"
