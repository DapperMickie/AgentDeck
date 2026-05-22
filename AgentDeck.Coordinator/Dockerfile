FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["AgentDeck.Shared/AgentDeck.Shared.csproj", "AgentDeck.Shared/"]
COPY ["AgentDeck.Coordinator/AgentDeck.Coordinator.csproj", "AgentDeck.Coordinator/"]

RUN dotnet restore "AgentDeck.Coordinator/AgentDeck.Coordinator.csproj"

COPY . .

RUN dotnet publish "AgentDeck.Coordinator/AgentDeck.Coordinator.csproj" \
    -c Release \
    -o /app/publish \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true

FROM debian:12-slim AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        libicu72 \
    && useradd --create-home --shell /bin/sh agentdeck \
    && mkdir -p /app/artifacts \
    && chown -R agentdeck:agentdeck /app \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_ENVIRONMENT=Production
ENV AGENTDECK_COORDINATOR_BIND_ADDRESS=0.0.0.0

EXPOSE 5001
VOLUME ["/app/artifacts"]

COPY --from=build --chown=agentdeck:agentdeck /app/publish .

USER agentdeck
ENTRYPOINT ["./AgentDeck.Coordinator"]
