# EZRentOut connector

Equipment rental / asset availability connector (EZO).

## Auth

- Base: `https://<SUBDOMAIN>.ezrentout.com`
- Header on every request: `token: <COMPANY_TOKEN>`
- HTTPS required

Enable API in EZRentOut: Settings → Integrations → API Integration (account owner).

Docs: https://ezo.io/ezrentout/developers/

## Secrets

```bash
cd backend/Palantir.Api
dotnet user-secrets set "Connectors:EZRentOut:Subdomain" "<subdomain>"
dotnet user-secrets set "Connectors:EZRentOut:ApiToken" "<token>"
```

## Capabilities (target)

- `ListOpenWork` — open orders / overdue returns
- `ListAssets` — rentable inventory
- `HealthCheck`
- `SearchContext`
