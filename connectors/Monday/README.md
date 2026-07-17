# Monday.com connector

Project boards and tasks via monday GraphQL API.

## Workspace scope

**Only `Sable Operations` (workspace id `11721129`) is active.** Other workspaces are deprecated.

## Quotes focus

Primary board for Palantir: **Quotes** (`18242475298`).

Recaps emphasize:
- Quotes open a long time (default ≥ 14 days in Sent/Draft)
- Draft opportunities
- Quotes with MaintainX WO links (and whether that WO is still open)

## Write-back (Ops-4)

Approval-gated item updates via GraphQL `create_update`. Queued from Open work → Approvals → Approve & post.

## Auth

- Endpoint: `POST https://api.monday.com/v2`
- Header: `Authorization: <api-token>`
- Header: `Content-Type: application/json`
- Recommended: `API-Version: 2024-10`

## Config

```json
"Monday": {
  "WorkspaceId": "11721129",
  "WorkspaceName": "Sable Operations",
  "IncludedBoardIds": [ "18242475298" ],
  "IncludedBoardNames": [ "Quotes" ],
  "ExcludedBoardNames": [ "Truck Inventory" ],
  "QuoteAgingDays": 14
}
```

## Excluded boards

| Board | Why |
|-------|-----|
| Truck Inventory | Who-has-which-truck sheet, not actionable work |
