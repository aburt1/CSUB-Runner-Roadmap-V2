# Backend container: build + publish the ASP.NET Core API only (no SPA bundled).
# Build context is ./Api.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Api.csproj ./
RUN dotnet restore Api.csproj
COPY . ./
RUN dotnet publish Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# curl is installed (as root, before dropping privileges) only for the container
# HEALTHCHECK below; it also helps when debugging the api from inside the container.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Liveness probe: the api answers /api/health/live with 200 once the process is up.
# Use 127.0.0.1 explicitly to avoid resolving "localhost" to ::1.
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/health/live || exit 1
# Run as the image's built-in non-root user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Api.dll"]
