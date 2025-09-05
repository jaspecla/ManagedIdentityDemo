#############################
# DownstreamApi Dockerfile  #
#############################
# Multi-stage build for .NET 9 minimal API
# Target runtime: Azure Container Apps (single HTTP port 8080)

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
# Azure Container Apps will route inbound traffic; we expose 8080 inside the container
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution & project files first for better layer caching
COPY ManagedIdentityDemo.sln ./
COPY DownstreamApi/DownstreamApi.csproj DownstreamApi/
COPY CallerApi/CallerApi.csproj CallerApi/

RUN dotnet restore DownstreamApi/DownstreamApi.csproj

# Copy the rest of the source
COPY . .

# Publish (no app host -> smaller image, portable)
RUN dotnet publish DownstreamApi/DownstreamApi.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DownstreamApi.dll"]
