###########################
# CallerApi Dockerfile    #
###########################
# Multi-stage build for .NET 9 minimal API
# Target runtime: Azure Container Apps (single HTTP port 8080)

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution & project files first for caching
COPY ManagedIdentityDemo.sln ./
COPY CallerApi/CallerApi.csproj CallerApi/
COPY DownstreamApi/DownstreamApi.csproj DownstreamApi/

RUN dotnet restore CallerApi/CallerApi.csproj

COPY . .

RUN dotnet publish CallerApi/CallerApi.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CallerApi.dll"]
