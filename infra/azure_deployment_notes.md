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
