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
    /p:UseAppHost=false

FROM debian:12-slim AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        wget \
    && wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends aspnetcore-runtime-10.0 \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://0.0.0.0:5000
ENV AGENTDECK_WORKSPACE=/workspace

EXPOSE 5000
VOLUME ["/workspace"]

COPY --from=build /app/publish .

USER root
ENTRYPOINT ["dotnet", "AgentDeck.Runner.dll"]
