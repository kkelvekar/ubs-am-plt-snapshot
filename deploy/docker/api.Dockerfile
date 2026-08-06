# Build context is the repository root:
#   docker build -f deploy/docker/api.Dockerfile -t ubs-snapshot-api:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/ src/

RUN dotnet publish src/Clients/UBS.AM.PLT.Snapshot.Api/UBS.AM.PLT.Snapshot.Api.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

USER $APP_UID

ENTRYPOINT ["dotnet", "UBS.AM.PLT.Snapshot.Api.dll"]
