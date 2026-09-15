# ------------------------------------------------------------------------------
# PHARMCO API container — multi-stage .NET 8 build.
# Build:  docker build -t pharmco/api:latest -f Pharmco.Api/Dockerfile .
# (referenced by deploy/docker-compose.yml as build context ../server)
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /build
COPY Pharmco.Api/Pharmco.Api.csproj Pharmco.Api/
RUN dotnet restore Pharmco.Api/Pharmco.Api.csproj
COPY Pharmco.Api/ Pharmco.Api/
RUN dotnet publish Pharmco.Api/Pharmco.Api.csproj -c Release -o /out --no-restore

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