# ------------------------------------------------------------------------------
# PHARMCO API container — multi-stage .NET 8 build.
# Build:  docker build -t pharmco/api:latest -f server/Pharmco.Api/Dockerfile .
# The repository root is the context so the Core project and embedded SQL
# migrations under db/ are available to MSBuild.
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /build
COPY Directory.Build.props .
COPY server/Directory.Build.props server/Directory.Build.props
COPY server/Pharmco.Api/Pharmco.Api.csproj server/Pharmco.Api/
COPY server/Pharmco.Core/Pharmco.Core.csproj server/Pharmco.Core/
RUN dotnet restore server/Pharmco.Api/Pharmco.Api.csproj
COPY server/Pharmco.Api/ server/Pharmco.Api/
COPY server/Pharmco.Core/ server/Pharmco.Core/
COPY db/ db/
RUN dotnet publish server/Pharmco.Api/Pharmco.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# the aspnet image has no curl/wget by default; the compose healthcheck needs
# an HTTP probe inside the container
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# drop to the non-root app user the base image provides
USER $APP_UID
ENTRYPOINT ["dotnet", "Pharmco.Api.dll"]
