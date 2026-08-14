# Build context is the repository root:
#   docker build -f deploy/docker/consumer.Dockerfile -t ubs-snapshot-consumer:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/ src/

RUN dotnet publish src/Clients/UBS.AM.PLT.Snapshot.Worker/UBS.AM.PLT.Snapshot.Worker.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

USER $APP_UID

ENTRYPOINT ["dotnet", "UBS.AM.PLT.Snapshot.Worker.dll"]
