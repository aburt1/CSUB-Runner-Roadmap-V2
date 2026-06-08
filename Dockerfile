# Multi-stage build: Vue client -> .NET publish (SPA bundled into wwwroot) -> runtime.

# 1) Build the Vue client
FROM node:22-alpine AS client
WORKDIR /client
COPY client/package.json client/package-lock.json ./
RUN npm ci
COPY client/ ./
RUN npm run build

# 2) Publish the ASP.NET Core API, with the built SPA copied into wwwroot
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY Api/Api.csproj Api/
RUN dotnet restore Api/Api.csproj
COPY Api/ Api/
COPY --from=client /client/dist Api/wwwroot/
RUN dotnet publish Api/Api.csproj -c Release -o /app/publish

# 3) Runtime image (serves API + SPA from one process)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=api /app/publish ./
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.dll"]
