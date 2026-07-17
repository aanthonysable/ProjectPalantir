# Ops Systems Integrations (Plan Branch)

## Status

Active branch from the communications-first roadmap. This does **not** replace Phases 1–4; it adds an **operations intelligence** track so Palantir can unify work across field/maintenance, rental assets, and project boards.

Started: 2026-07-17.

## Why

Sable already runs work in separate tools:

| System | Role | Palantir environments |
|--------|------|------------------------|
| **MaintainX** | CMMS — work orders, assets, preventive maintenance | **Two separate environments** (distinct API keys / orgs) |
| **EZRentOut** | Equipment rental / asset availability | One company subdomain + API token |
| **Monday.com** | Boards, tasks, project tracking | Personal/API token (later OAuth if needed) |
| **SAP** (future) | Limited accounting read | TBD — keep connector-shaped, not hardcoded |
| **Syteline** (future alternative) | ERP if SAP is not chosen | Same abstraction as SAP accounting reads |

Goal: one Palantir surface for **task awareness**, **cross-system insights**, and **staying on top of open work** — with AI summaries that cite source systems.

## Design principles (unchanged)

- Connector-first (ADR 0004): core services call **capabilities**, not vendor SDKs.
- Secrets only in user-secrets / Key Vault — never commit API keys.
- Org-scoped connections: MaintainX env A and env B are two `ConnectedAccount` (or `ConnectorInstance`) rows, not one merged client.
- External writes that affect customers or money still go through **approval** where applicable; read/sync and internal task surfacing can be automatic.
- Do not invent cross-IDs: store provider ids + optional Palantir link table when we learn real join keys (asset #, customer name, board item id).

## Capability set (ops track)

| Capability | MaintainX | EZRentOut | Monday.com | ERP later |
|------------|-----------|-----------|------------|-----------|
| `ListOpenWork` | Work orders | Open rentals / reservations | Board items / tasks | A/R or open invoices (limited) |
| `ListAssets` | Assets | Rentable assets | — | — |
| `SyncTasks` | WO → Palantir tasks | Due returns → tasks | Items → tasks | Follow-ups |
| `HealthCheck` | Yes | Yes | Yes | Yes |
| `SearchContext` | WO + asset text | Asset + order text | Item + updates | GL snippets |

## Phased delivery

### Ops-0 — Spec + secrets plumbing (this session)

- Document connectors and config keys.
- Scaffold connector folders and options classes.
- Accept API keys via `dotnet user-secrets` when provided.

### Ops-1 — Read-only connect + health

- Validate each API key with a minimal “who am I / list first page” call.
- Admin UI: connection status per MaintainX env, EZRentOut, Monday.
- Audit `ConnectorConnected` / `ConnectorHealthFailed`.

### Ops-2 — Unified open-work inbox

- Normalize into a shared `ExternalWorkItem` projection (or map into existing Tasks + tags).
- Filters: source system, environment, assignee, overdue.
- Manual refresh + optional poll interval.

### Ops-3 — Intelligent insights

- Daily/on-demand AI digest focused on **physical work** (OPEN / IN_PROGRESS), overdue returns, Monday blockers, and inventory that risks jobs.
- **ON_HOLD** = physically finished; back office close-out is not a field backlog unless explicitly asked.
- Link related items when join keys are known (Quotes ↔ MaintainX WO#).
- Surface in Overview / Ask ops.

### Ops-4 — Limited write-back (optional, approval-gated)

- MaintainX work-order comments and Monday item updates only.
- Flow: **Open work** → Comment / Update → draft + pending approval → **Approvals** → Approve & post.
- API: `POST /ops/write-back`; execution reuses `POST /approvals/{id}/approve` (dispatches by draft `kind`).
- Draft kinds: `maintainx.comment`, `monday.update`. Idempotent via `WorkflowAction` keys `ops-write:…`.
- EZRentOut write-back is out of scope for this phase.

### Ops-5 — Accounting (SAP or Syteline)

- Narrow read scope (open AR, customer balance, invoice status) behind `IAccountingConnector`.
- Decision point: SAP vs Syteline — implement one adapter first.

## Configuration keys (no secrets in git)

```
Connectors:MaintainX:Environments:0:Name = "MaintainX-A"   # human label
Connectors:MaintainX:Environments:0:ApiKey = <user-secrets>
Connectors:MaintainX:Environments:0:OrganizationId = <optional>
Connectors:MaintainX:Environments:1:Name = "MaintainX-B"
Connectors:MaintainX:Environments:1:ApiKey = <user-secrets>
Connectors:MaintainX:Environments:1:OrganizationId = <optional>
Connectors:MaintainX:BaseUrl = https://api.getmaintainx.com/v1

Connectors:EZRentOut:Subdomain = <company subdomain>
Connectors:EZRentOut:ApiToken = <user-secrets>
Connectors:EZRentOut:BaseUrlTemplate = https://{subdomain}.ezrentout.com

Connectors:Monday:ApiToken = <user-secrets>
Connectors:Monday:ApiUrl = https://api.monday.com/v2
Connectors:Monday:ApiVersion = 2024-10
```

When keys arrive, store with:

```bash
cd backend/Palantir.Api
dotnet user-secrets set "Connectors:MaintainX:Environments:0:ApiKey" "..."
dotnet user-secrets set "Connectors:MaintainX:Environments:1:ApiKey" "..."
dotnet user-secrets set "Connectors:EZRentOut:ApiToken" "..."
dotnet user-secrets set "Connectors:Monday:ApiToken" "..."
```

## Auth patterns (vendor)

- **MaintainX**: REST, `Authorization: Bearer <api-key>`, optional `X-Organization-Id`. Docs: https://api.getmaintainx.com / maintainx.dev
- **EZRentOut**: REST, header `token: <COMPANY_TOKEN>`, HTTPS to `https://<SUBDOMAIN>.ezrentout.com`. Docs: https://ezo.io/ezrentout/developers/
- **Monday.com**: GraphQL POST `https://api.monday.com/v2`, `Authorization: <token>`. Docs: https://developer.monday.com/

## Out of scope for this branch (for now)

- Replacing Outlook / Entra pilot work
- Full ERP migration
- Writing financial postings to SAP/Syteline

## Success criteria for first demo

1. Both MaintainX environments show Connected + open work order counts.
2. EZRentOut shows Connected + sample assets or open orders.
3. Monday shows Connected + sample board items.
4. One combined “Open work” list in the web app with source badges.
5. One AI insight paragraph that references at least two systems.

## Overview / recap (started 2026-07-17)

Web **Overview** nav aggregates inbox, tasks, approvals, and ops connectors into a snapshot plus an optional AI narrative (`GET /overview`, `POST /overview/recap`).

Focus toggles (sources, depth, custom prompt) are **browser-local** for now so each user can shape the recap while the product intent is still forming. Later: sync prefs to the user profile.
