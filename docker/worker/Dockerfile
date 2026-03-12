# ── Build stage: compile CopilotHive.Worker ───────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY CopilotHive.slnx ./
COPY src/CopilotHive/CopilotHive.csproj src/CopilotHive/
COPY src/CopilotHive.Shared/CopilotHive.Shared.csproj src/CopilotHive.Shared/
COPY src/CopilotHive.Worker/CopilotHive.Worker.csproj src/CopilotHive.Worker/
COPY tests/CopilotHive.Tests/CopilotHive.Tests.csproj tests/CopilotHive.Tests/

RUN dotnet restore src/CopilotHive.Worker/CopilotHive.Worker.csproj

COPY src/ src/

RUN dotnet publish src/CopilotHive.Worker/CopilotHive.Worker.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -o /worker/publish

# ── Runtime stage: slim worker with Copilot CLI + Worker binary ───────────────
FROM ubuntu:24.04

LABEL maintainer="CopilotHive" \
      description="Slim worker container for CopilotHive agents"

# Prevent interactive prompts during package installation
ENV DEBIAN_FRONTEND=noninteractive

# Install system packages, GitHub CLI, Node.js, and Copilot CLI
RUN apt-get update \
    && apt-get install -y \
        git curl wget jq openssh-client ca-certificates gnupg \
        ripgrep fd-find tree file \
        build-essential python3 \
        libicu-dev \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
       | gpg --dearmor -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
       > /etc/apt/sources.list.d/github-cli.list \
    && curl -fsSL https://deb.nodesource.com/setup_lts.x | bash - \
    && apt-get install -y gh nodejs \
    && npm install -g @github/copilot \
    && rm -rf /var/lib/apt/lists/* /tmp/*

# Create workspace and config directories
RUN mkdir -p /copilot-home /opt/copilot-env/.github/skills

# Copy Worker binary from build stage
COPY --from=build /worker/publish /opt/worker/

# Copy agent configuration
COPY docker/worker/worker-agents.md /opt/copilot-env/AGENTS.md
COPY docker/worker/skills/ /opt/copilot-env/.github/skills/

# Copy and set entrypoint (sed strips Windows line endings)
COPY docker/worker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN sed -i 's/\r$//' /usr/local/bin/entrypoint.sh && chmod +x /usr/local/bin/entrypoint.sh

# Environment variables
ENV COPILOT_MODE=headless \
    COPILOT_PORT=8000 \
    COPILOT_LOG_LEVEL=info \
    COPILOT_CUSTOM_INSTRUCTIONS_DIRS=/opt/copilot-env

WORKDIR /copilot-home

EXPOSE 8000

ENTRYPOINT ["entrypoint.sh"]
