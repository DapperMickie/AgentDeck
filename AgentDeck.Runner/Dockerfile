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
        wget \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://0.0.0.0:5000
ENV AGENTDECK_WORKSPACE=/workspace

EXPOSE 5000
VOLUME ["/workspace"]

COPY --from=build /app/publish .

USER root
ENTRYPOINT ["./AgentDeck.Runner"]
