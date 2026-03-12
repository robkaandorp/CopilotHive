# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy solution and project files first for layer caching
COPY CopilotHive.slnx ./
COPY src/CopilotHive/CopilotHive.csproj src/CopilotHive/
COPY src/CopilotHive.Shared/CopilotHive.Shared.csproj src/CopilotHive.Shared/
COPY src/CopilotHive.Worker/CopilotHive.Worker.csproj src/CopilotHive.Worker/
COPY tests/CopilotHive.Tests/CopilotHive.Tests.csproj tests/CopilotHive.Tests/

RUN dotnet restore CopilotHive.slnx

# Copy remaining source
COPY src/ src/
COPY agents/ agents/

RUN dotnet publish src/CopilotHive/CopilotHive.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -o /app/publish

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install git, curl, Node.js 24, and Copilot CLI for the Brain
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl git ca-certificates gnupg && \
    mkdir -p /etc/apt/keyrings && \
    curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
      | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg && \
    echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_24.x nodistro main" \
      > /etc/apt/sources.list.d/nodesource.list && \
    apt-get update && \
    apt-get install -y nodejs build-essential python3 libicu-dev && \
    npm install -g @github/copilot && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/publish .
COPY agents/ /app/agents/
COPY docker/orchestrator/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

EXPOSE 5000 5001 8100

HEALTHCHECK --interval=10s --timeout=5s --retries=5 --start-period=60s \
    CMD curl -f http://localhost:5001/health || exit 1

ENTRYPOINT ["/app/entrypoint.sh"]
