FROM ubuntu:24.04

LABEL maintainer="CopilotHive" \
      description="Slim worker container for CopilotHive agents"

# Prevent interactive prompts during package installation
ENV DEBIAN_FRONTEND=noninteractive

# Install all apt packages (core, search/utils, GitHub CLI, Node.js) in a single layer
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        git \
        curl \
        wget \
        jq \
        openssh-client \
        ca-certificates \
        gnupg \
        ripgrep \
        fd-find \
        tree \
        file \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
       | gpg --dearmor -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
       > /etc/apt/sources.list.d/github-cli.list \
    && curl -fsSL https://deb.nodesource.com/setup_lts.x | bash - \
    && apt-get install -y --no-install-recommends gh nodejs \
    && npm install -g @github/copilot \
    && npm cache clean --force \
    && apt-get purge -y --auto-remove gnupg \
    && rm -rf /var/lib/apt/lists/* /tmp/* /root/.npm /root/.cache

# Create workspace and config directories
RUN mkdir -p /copilot-home /opt/copilot-env/.github/skills

# Copy agent configuration
COPY worker-agents.md /opt/copilot-env/AGENTS.md
COPY skills/ /opt/copilot-env/.github/skills/

# Copy and set entrypoint (sed strips Windows line endings)
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN sed -i 's/\r$//' /usr/local/bin/entrypoint.sh && chmod +x /usr/local/bin/entrypoint.sh

# Environment variables
ENV COPILOT_MODE=headless \
    COPILOT_PORT=8000 \
    COPILOT_LOG_LEVEL=info \
    COPILOT_CUSTOM_INSTRUCTIONS_DIRS=/opt/copilot-env

WORKDIR /copilot-home

EXPOSE 8000

ENTRYPOINT ["entrypoint.sh"]
