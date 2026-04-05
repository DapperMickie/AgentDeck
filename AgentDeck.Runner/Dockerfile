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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:5000
ENV AGENTDECK_WORKSPACE=/workspace

EXPOSE 5000
VOLUME ["/workspace"]

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "AgentDeck.Runner.dll"]
