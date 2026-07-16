# Microsoft Graph connector (Outlook / calendar)

## Local Connect Outlook checklist

1. App registration Client ID / Tenant ID are in `backend/Palantir.Api/appsettings.json`.
2. Client secret is in user-secrets (`Connectors:MicrosoftGraph:ClientSecret`).
3. Redirect URI (platform **Web**) must include:
   - `http://localhost:5251/oauth/microsoft/callback`
4. Supported account types should allow **personal Microsoft accounts** for `@outlook.com`.
5. Delegated Graph permissions: `openid profile email offline_access User.Read Mail.Read Mail.Send`.
6. After adding `Mail.Send`, disconnect and Connect Outlook again so consent includes send.

Pilot mailbox: `palantir.pilot.aanthony@outlook.com`

Core services call this connector through generic connected-account APIs — vendor-specific Graph logic lives here / in Infrastructure.
