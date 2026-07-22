# Entra External ID setup (Palantir pilot login)

**Primary login** for users is **Sign in with Microsoft** (DNOW work email via Entra External ID).
**Local password login** stays enabled as a developer backdoor only (collapsed on the login screen)
in case Microsoft sign-in is unavailable during development.

This is **Palantir login**, not mailbox connect. Work/personal email sync is configured separately under Admin.

## Azure steps (you / cloud admin)

1. Create a **Microsoft Entra External ID** (CIAM) tenant, or use a dedicated workforce tenant for early pilot.
2. Register a **Single-page application**:
   - Redirect URI: `http://127.0.0.1:5173` (and `http://localhost:5173` if needed)
   - Enable ID tokens (implicit/hybrid as required by MSAL SPA)
3. Register an **API** application (or expose an API on the SPA app):
   - Application ID URI, e.g. `api://{api-client-id}`
   - Scope e.g. `access_as_user`
   - Authorize the SPA to request that scope
4. Create a test user in the External ID tenant (or invite yourself).

## Palantir config

Set via `appsettings.Development.json` or user-secrets (do not commit secrets):

```json
"Authentication": {
  "EntraExternalId": {
    "Enabled": true,
    "Authority": "https://{your-tenant}.ciamlogin.com/{tenant-id}/v2.0",
    "ClientId": "{spa-client-id}",
    "Audience": "api://{api-client-id}",
    "TenantId": "{tenant-id}",
    "Scopes": [ "openid", "profile", "email", "api://{api-client-id}/access_as_user" ]
  }
}
```

Workforce tenant early pilot alternative:

```text
Authority = https://login.microsoftonline.com/{tenant-id}/v2.0
```

Restart the API after changing config.

## Runtime flow

1. Browser loads `GET /auth/providers` — if Entra is enabled, show Microsoft button.
2. MSAL signs the user in and obtains an ID/access token.
3. `POST /auth/entra/exchange` validates the token and:
   - links `ExternalIdentity` to an existing Palantir user when email matches (e.g. `alec.anthony@dnow.com`), or
   - creates a new Palantir `User` + `ExternalIdentity`
4. Returns a Palantir pilot JWT — rest of the app unchanged.

## Verify

```bash
curl -s http://localhost:5251/auth/providers
```

When disabled: `"entraExternalId": null`.  
When enabled: authority/clientId present; Microsoft button appears on the login screen.
