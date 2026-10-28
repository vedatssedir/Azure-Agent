FROM node:22 AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AzureAgent.Api -c Release -o /app

# Coding CLIs for CLI agents (Agent → Runtime). Claude Code is installed by default; the version is
# the one the platform's command line was verified against. Cursor CLI is opt-in.
FROM node:22-bookworm-slim AS cli
ARG CLAUDE_CODE_VERSION=2.1.312
RUN npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} && npm cache clean --force

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG INSTALL_CURSOR_CLI=false
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates curl && rm -rf /var/lib/apt/lists/*
COPY --from=cli /usr/local/bin/node /usr/local/bin/node
COPY --from=cli /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/@anthropic-ai/claude-code/cli.js /usr/local/bin/claude \
 && if [ "$INSTALL_CURSOR_CLI" = "true" ]; then curl -fsS https://cursor.com/install | HOME=/opt/cursor bash \
      && ln -s "$(ls -d /opt/cursor/.local/bin/cursor-agent)" /usr/local/bin/cursor-agent; fi
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080 AgentRuntime__WorkspaceRoot=/var/lib/azure-agent/workspaces
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "AzureAgent.Api.dll"]
