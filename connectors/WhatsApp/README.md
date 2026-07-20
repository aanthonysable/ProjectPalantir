# WhatsApp bridge (internal groups)

Pilot path to **read existing WhatsApp groups** (internal status updates) into Palantir Inbox and cross-check MaintainX / Monday / EZRentOut — **without changing how the team uses WhatsApp**.

Official Meta Cloud API cannot join `chat.whatsapp.com/…` invite groups. This bridge uses **[WAHA](https://waha.devlike.pro/)** (WhatsApp HTTP API over WhatsApp Web / multi-device). Treat it as a **temporary** connector: Meta ToS risk, session can drop and need re-QR.

## What you get

1. A dedicated WhatsApp session (spare phone or secondary device) joins the same internal groups.
2. WAHA posts new messages to Palantir `POST /webhooks/whatsapp`.
3. Threads land in Inbox as `Channel = WhatsApp`.
4. `GET /whatsapp/gaps` matches WO # / quote # / customer-ish tokens against open ops work.

Outbound reply from Palantir is **out of scope** for this pilot.

## Prerequisites

- Docker
- A **dedicated** WhatsApp account (do not hijack someone’s daily phone long-term)
- Palantir API running (`http://localhost:5251`)
- Shared webhook secret in user-secrets

## Secrets

```bash
cd backend/Palantir.Api
dotnet user-secrets set "Connectors:WhatsApp:Enabled" "true"
dotnet user-secrets set "Connectors:WhatsApp:WebhookSecret" "<long-random-secret>"
# Optional — defaults to seeded demo org in Development
# dotnet user-secrets set "Connectors:WhatsApp:OrganizationId" "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
```

## Run WAHA

From repo root:

```bash
export PALANTIR_WA_WEBHOOK_SECRET='<same-as-user-secrets>'
# or: set -a; source .env.whatsapp-bridge; set +a
docker compose -f docker-compose.whatsapp.yml up -d
```

On Apple Silicon the compose file uses `devlikeapro/waha:arm` (not `:latest`). Sessions persist in a Docker volume so you usually don’t re-scan QR after a reboot.

Open **http://localhost:3000/dashboard**

Login (from `.env.whatsapp-bridge`):

- Username: `WAHA_DASHBOARD_USERNAME` (default `admin`)
- Password: `WAHA_DASHBOARD_PASSWORD`

Then in the dashboard **server / connection** settings, paste:

- **API key:** `WAHA_API_KEY` from the same file

Without the API key you’ll see “Server connection failed / set right API key”.

If `docker compose` says unknown command, open **Docker Desktop** first, then:

```bash
mkdir -p ~/.docker/cli-plugins
ln -sfn "/Applications/Docker.app/Contents/Resources/cli-plugins/docker-compose" ~/.docker/cli-plugins/docker-compose
```

Webhook target (Mac Docker → host API):

`http://host.docker.internal:5251/webhooks/whatsapp?secret=<secret>`

(Also accepts header `X-Palantir-Webhook-Secret`.)

Events: `message.any` only (covers inbound + your own messages). Do **not** also subscribe `message` or attach the same URL as a session webhook — that triple-fires each chat line.


## Verify

1. Admin → WhatsApp bridge → should show **Configured** when Enabled + secret set.
2. Post a test message in a joined group.
3. Inbox → filter/search for WhatsApp threads.
4. Admin → **Check gaps** (or `GET /whatsapp/gaps`) for ops cross-refs.

## Security notes

- Keep invite links private; rotate if shared broadly.
- Webhook secret must stay out of git.
- Prefer a dedicated number so a ban/session wipe does not hit personal chats.
