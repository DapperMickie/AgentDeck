FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["AgentDeck.Shared/AgentDeck.Shared.csproj", "AgentDeck.Shared/"]
COPY ["AgentDeck.Runner/AgentDeck.Runner.csproj", "AgentDeck.Runner/"]

RUN dotnet restore "AgentDeck.Runner/AgentDeck.Runner.csproj"

COPY . .

RUN dotnet publish "AgentDeck.Runner/AgentDeck.Runner.csproj" \
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
        sudo \
        wget \
    && useradd --create-home --shell /bin/sh agentdeck \
    && echo "agentdeck ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/agentdeck \
    && chmod 0440 /etc/sudoers.d/agentdeck \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://0.0.0.0:5000
ENV AGENTDECK_WORKSPACE=/workspace

EXPOSE 5000
VOLUME ["/workspace"]

COPY --from=build --chown=agentdeck:agentdeck /app/publish .

USER agentdeck
ENTRYPOINT ["./AgentDeck.Runner"]
