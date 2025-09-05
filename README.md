# Managed Identity Demo (CallerApi -> DownstreamApi)

This repo contains two ASP.NET Core 9.0 Web API projects:

- **DownstreamApi**: Protected API that expects Azure AD (Entra ID) issued tokens.
- **CallerApi**: Calls the `DownstreamApi` using a User Assigned Managed Identity (UAMI) (or system-assigned) to acquire a token via `DefaultAzureCredential`.

## Local Development Flow

Locally you typically don't have a Managed Identity. `DefaultAzureCredential` falls back to developer credentials (Azure CLI / Visual Studio / VS Code sign-in). Ensure you're logged in:

```pwsh
az login
```

Run both APIs (different ports):

```pwsh
# Terminal 1
dotnet run --project .\DownstreamApi
# Terminal 2
dotnet run --project .\CallerApi
```

Test proxy endpoint (Caller -> Downstream):
```pwsh
curl https://localhost:5001/proxyweather --insecure
```

To disable auth locally (for initial wiring) leave `AzureAd:Authority` empty in `DownstreamApi/appsettings.json`.

---
## Azure Deployment (Container Apps)

This section replaces earlier App Service guidance with Azure Container Apps (ACA) instructions.

### Overview of Flow in ACA
1. Build two container images (Caller, Downstream) and push to Azure Container Registry (ACR).
2. Create a Container Apps Environment.
3. Create a User Assigned Managed Identity (or use system-assigned) for Caller.
4. Create / configure Entra ID App Registration for the Downstream API (expose API URI `api://<downstream-client-id>`).
5. Assign the Managed Identity access (App Role assignment or scope depending on how you configure the API).
6. Deploy both container apps and configure environment variables.
7. Caller obtains token using Managed Identity -> calls Downstream.

### 1. Variables
```pwsh
$RG="rg-managed-identity-demo"
$LOC="eastus"
$ACR="midemoacr$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
$ENV="aca-midemo-env"
$DOWNSTREAM_APP="downstream-api-ca"
$CALLER_APP="caller-api-ca"
$UAMI_NAME="uami-caller-api"
```

### 2. Resource Group
```pwsh
az group create -n $RG -l $LOC
```

### 3. ACR (Container Registry)
```pwsh
az acr create -n $ACR -g $RG --sku Basic --admin-enabled false
az acr login -n $ACR
```

### 4. Add Dockerfiles (Example Multi-Stage)
If not already present, you can create `DownstreamApi/Dockerfile` and `CallerApi/Dockerfile`:
```Dockerfile
# (Example) DownstreamApi/Dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish DownstreamApi/DownstreamApi.csproj -c Release -o /out
FROM base AS final
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet","DownstreamApi.dll"]
```
(CallerApi Dockerfile is analogous, pointing to `CallerApi.csproj` and `CallerApi.dll`).

### 5. Build & Push Images
```pwsh
az acr build -t $ACR.azurecr.io/downstreamapi:latest -r $ACR .
az acr build -t $ACR.azurecr.io/callerapi:latest -r $ACR .
```
> Using `az acr build` avoids needing local Docker.

### 6. Create Managed Identity (User Assigned)
```pwsh
az identity create -g $RG -n $UAMI_NAME
$UAMI_CLIENTID = az identity show -g $RG -n $UAMI_NAME --query clientId -o tsv
$UAMI_PRINCIPALID = az identity show -g $RG -n $UAMI_NAME --query principalId -o tsv
$UAMI_ID = az identity show -g $RG -n $UAMI_NAME --query id -o tsv  # resource id
```

### 7. Entra ID App Registration (Downstream API)
Create or reuse an app registration:
- Name: `downstream-api-demo`
- Note: Application (client) ID => `$DOWNSTREAM_CLIENTID`
- Expose an API: Set Application ID URI to `api://$DOWNSTREAM_CLIENTID` (or custom) and (optionally) define an App Role (e.g. `Downstream.Access`).

Capture values:
```pwsh
$TENANT_ID = az account show --query tenantId -o tsv
$DOWNSTREAM_CLIENTID = "<guid-of-downstream-app-registration>"
```

### 8. (Optional) Create an App Role & Assign (If using application permission style)
Portal: Entra ID -> App registrations -> downstream-api-demo -> App roles -> Add.
Then assign the Managed Identity:
- Entra ID -> Enterprise applications -> downstream-api-demo (service principal) -> Users & groups -> Add assignment -> choose the UAMI.

If you rely on scopes + delegated flow for local dev, keep a delegated scope (e.g. `user_impersonation`) but for Managed Identity in ACA you typically use application role + `/.default`.

### 9. Create Container Apps Environment
```pwsh
az containerapp env create -g $RG -n $ENV -l $LOC
```

### 10. Grant ACR Pull Permission to the Environment’s Managed Identity
Retrieve the environment managed identity principal id:
```pwsh
$ENV_MI=az containerapp env show -g $RG -n $ENV --query identity.principalId -o tsv
az role assignment create --assignee $ENV_MI --scope $(az acr show -n $ACR -g $RG --query id -o tsv) --role "AcrPull"
```

### 11. Deploy Downstream Container App
```pwsh
az containerapp create -g $RG -n $DOWNSTREAM_APP \
  --environment $ENV \
  --image $ACR.azurecr.io/downstreamapi:latest \
  --target-port 8080 \
  --ingress external \
  --registry-server $ACR.azurecr.io \
  --min-replicas 1 --max-replicas 2 \
  --env-vars \
    AzureAd__Authority="https://login.microsoftonline.com/$TENANT_ID/v2.0" \
    AzureAd__ClientId=$DOWNSTREAM_CLIENTID \
    AzureAd__Audience="api://$DOWNSTREAM_CLIENTID"
```
Get its FQDN:
```pwsh
$DOWNSTREAM_FQDN = az containerapp show -g $RG -n $DOWNSTREAM_APP --query properties.configuration.ingress.fqdn -o tsv
```

### 12. Deploy Caller Container App (Assign Identity)
```pwsh
az containerapp create -g $RG -n $CALLER_APP \
  --environment $ENV \
  --image $ACR.azurecr.io/callerapi:latest \
  --target-port 8080 \
  --ingress external \
  --registry-server $ACR.azurecr.io \
  --min-replicas 1 --max-replicas 2 \
  --user-assigned $UAMI_ID \
  --env-vars \
    ManagedIdentity__ClientId=$UAMI_CLIENTID \
    Downstream__BaseUrl="https://$DOWNSTREAM_FQDN/" \
    Downstream__ResourceId="api://$DOWNSTREAM_CLIENTID" \
    Downstream__Scope="api://$DOWNSTREAM_CLIENTID/.default"
```

### 13. Test End-to-End
```pwsh
$CALLER_FQDN = az containerapp show -g $RG -n $CALLER_APP --query properties.configuration.ingress.fqdn -o tsv
curl https://$CALLER_FQDN/proxyweather
```
The Caller Container App uses the UAMI to acquire a token for `api://$DOWNSTREAM_CLIENTID/.default` and forwards the call to Downstream.

### 14. Optional: System-Assigned Identity
Instead of user-assigned:
```pwsh
az containerapp identity assign -g $RG -n $CALLER_APP --system-assigned
```
Then remove `ManagedIdentity__ClientId` env var; `DefaultAzureCredential` will use the system identity.

---
## Key Config Points
| Setting | Project | Purpose |
|---------|---------|---------|
| ManagedIdentity:ClientId | CallerApi | User Assigned Managed Identity client ID (omit for system identity). |
| Downstream:BaseUrl | CallerApi | URL of DownstreamApi (Container App FQDN). |
| Downstream:ResourceId / Scope | CallerApi | App ID URI used to request token. |
| AzureAd:Authority | DownstreamApi | Tenant authority. |
| AzureAd:Audience | DownstreamApi | Valid audience (App ID URI). |

---
## Troubleshooting (Container Apps)
- 401 Unauthorized (Downstream): Audience mismatch or token not issued for API. Check `aud` in JWT (`jwt.ms`).
- 403 Forbidden: Managed Identity not assigned an App Role or not authorized. Ensure role assignment present in Enterprise Applications -> downstream-api -> Users & groups.
- Token acquisition failure: Confirm identity assigned (`az containerapp show ... --query identity`) and `ManagedIdentity__ClientId` matches for user-assigned.
- ACR pull failures: Ensure AcrPull role to environment identity (step 10) OR specify `--registry-identity` if using a separate identity.
- Slow cold start: Consider min replicas > 0.
- Local dev vs cloud: Locally you'll see `scp` claim (delegated). In ACA with Managed Identity you see `roles` claim (app role) if using application permission path.
