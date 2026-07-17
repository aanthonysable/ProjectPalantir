# Azure Deployment Notes

## Recommended initial resources

- Azure Resource Group: rg-palantir-dev
- Azure SQL Server and Database
- Azure App Service or Azure Container Apps
- Azure SignalR Service
- Azure Blob Storage Account
- Azure Key Vault
- Application Insights
- Log Analytics Workspace
- Entra ID app registration

## Provisioned (dev — 2026-07-17)

| Resource | Name |
|----------|------|
| Resource group | `rg-palantir-dev` |
| SQL server | `trojansql` (existing) |
| SQL database | `palantier-dev-sql` |
| Key Vault | `kv-palantir-dev` |
| Storage account | `stpalantirdev` |
| Blob container | `knowledge` (private) |

### Key Vault secrets

| Secret name | Maps to config |
|-------------|----------------|
| `ConnectionStrings--Palantir` | `ConnectionStrings:Palantir` |

Add more with `--` for nested keys (e.g. `Ai--Providers--gemini--ApiKey`).

### Local development

Until the API loads Key Vault at startup, keep using **dotnet user-secrets** for the SQL connection string and storage key.

```bash
cd backend/Palantir.Api
dotnet user-secrets set "Database:Provider" "SqlServer"
dotnet user-secrets set "ConnectionStrings:Palantir" "<ado.net string>"
dotnet user-secrets set "Azure:KeyVault:Uri" "https://kv-palantir-dev.vault.azure.net/"
dotnet user-secrets set "Azure:Storage:ConnectionString" "<storage connection string>"
dotnet user-secrets set "Azure:Storage:KnowledgeContainer" "knowledge"
```

Storage connection string: portal → `stpalantirdev` → **Access keys** → **Connection string**.

### Next wiring (app)

- Optional: `Azure.Extensions.AspNetCore.Configuration.Secrets` to pull Key Vault into config when `Azure:KeyVault:Uri` is set (DefaultAzureCredential / `az login`).
### Blob: use `Azure:Storage:*` for knowledge/doc uploads when the AI reference layer lands.

Knowledge MVP (2026-07-17):

- Upload via Admin → Knowledge (`POST /knowledge/upload`)
- Blob path: `{orgId}/{docId}/{fileName}` in container `knowledge`
- SQL tables: `KnowledgeDocuments`, `KnowledgeChunks`
- Lexical retrieval injects **KNOWLEDGE EXCERPTS** into Overview recap + Ask
- Indexable today: `.txt`, `.md`, `.csv`, `.json`, `.html` (8 MB limit)
- AI capture: Overview Ask → **Save to knowledge** → Approvals → `POST /knowledge/capture` / approve writes a markdown note into the same store

## Environment separation

- dev
- test
- prod

## Configuration principles

- No secrets in code.
- Use managed identities when possible.
- Use Key Vault references for connection strings and API keys.
- Use least privilege for service accounts.
- Enable diagnostic logs for all major services.

## Future infrastructure-as-code

Use Bicep or Terraform after the architecture stabilizes. The first prototype can be manually provisioned, but production should be reproducible from source control.

## Pilot identity resources

- Dedicated Palantir Entra External ID tenant or equivalent standalone customer identity tenant.
- Separate application registrations for Palantir login and Microsoft Graph connector authorization.
- Distinct redirect URIs for local development, test, and production.
- Azure Key Vault keys/secrets for connector token encryption.
- Test Microsoft 365 tenant for development mailboxes and Graph integration.

Do not couple the Azure subscription tenant, Palantir login tenant, and corporate Microsoft 365 tenant in code. Treat tenant IDs and issuers as configuration.
