# ── Build stage ───────────────────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG RID=${TARGETARCH/amd64/x64}

WORKDIR /src

# Copy solution and project files first for layer caching
COPY CopilotHive.slnx ./
COPY Directory.Build.props ./
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
    -r linux-${RID} \
    --self-contained \
    -o /app/publish

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install git and curl (for health checks and git operations)
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl git ca-certificates && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/publish .
COPY agents/ /app/agents/
COPY docker/orchestrator/entrypoint.sh /app/entrypoint.sh
RUN sed -i 's/\r$//' /app/entrypoint.sh && chmod +x /app/entrypoint.sh

EXPOSE 5000 5001

HEALTHCHECK --interval=10s --timeout=5s --retries=5 --start-period=60s \
    CMD curl -f http://localhost:5001/health || exit 1

ENTRYPOINT ["/app/entrypoint.sh"]
