# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build

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
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl git && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/publish .
COPY agents/ /app/agents/

EXPOSE 5000

HEALTHCHECK --interval=10s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "CopilotHive.dll", "--serve", "--port=5000"]
