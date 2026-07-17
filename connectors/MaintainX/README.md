# MaintainX connector

CMMS connector for work orders, assets, and maintenance context.

## Work order status meanings (Sable)

| Status | Meaning |
|--------|---------|
| `OPEN` | Not started yet |
| `IN_PROGRESS` | Being worked on |
| `ON_HOLD` | Physical work is finished; back office can close whenever they get to it |
| `DONE` | Back office has processed for billing; ticket fully closed |

**Ops focus:** digests and Overview prioritize `OPEN` / `IN_PROGRESS` (physical work). `ON_HOLD` is treated as effectively done for field/shop attention.

Open-work API still returns OPEN / IN_PROGRESS / ON_HOLD and excludes DONE. The Open work UI defaults to **physical only**.

Recaps also pull **work order comments** (`GET /workorders/{id}/comments`) for selected tickets — empty/attachment-only comments are skipped; text comments are used for narrative context.

## Write-back (Ops-4)

Approval-gated comments: `POST /workorders/{id}/comments` with `{ "content": "..." }`. Queued from Open work → Approvals → Approve & post.

## Inventory (parts)

`GET /parts` is scanned for restock risk:

| Severity | Rule |
|----------|------|
| **Out** | `availableQuantity <= 0` and `minimumQuantity > 0`, or negative quantity |
| **Low** | `0 < availableQuantity <= minimumQuantity` |

Parts with qty 0 and no minimum are treated as **untracked** and omitted (avoids flooding Northern/empty-tracking catalogs).

## Environments

Sable runs **two MaintainX organizations** (Permian Ops and Northern Ops). They may share one REST API key but must use distinct `OrganizationId` values (`X-Organization-Id`). Configure each as its own environment instance. Do not merge data in the HTTP client.

## Auth

- REST base: `https://api.getmaintainx.com/v1`
- Header: `Authorization: Bearer <api-key>`
- Optional: `X-Organization-Id`

Generate keys in MaintainX: Settings → Integrations.

## Secrets

```bash
cd backend/Palantir.Api
dotnet user-secrets set "Connectors:MaintainX:Environments:0:Name" "MaintainX-A"
dotnet user-secrets set "Connectors:MaintainX:Environments:0:ApiKey" "<key>"
dotnet user-secrets set "Connectors:MaintainX:Environments:1:Name" "MaintainX-B"
dotnet user-secrets set "Connectors:MaintainX:Environments:1:ApiKey" "<key>"
```

## Capabilities (target)

- `ListOpenWork` — open / overdue work orders
- `ListAssets`
- `HealthCheck`
- `SearchContext`

Vendor-specific HTTP lives under `backend/Palantir.Infrastructure/Connectors/`; core calls capabilities only.
