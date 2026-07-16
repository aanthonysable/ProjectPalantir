# Corporate Security Review Package (Draft)

Status: **working draft** for the Palantir pilot. Fill gaps before requesting corporate Outlook / Entra access.

Repo: https://github.com/aanthonysable/ProjectPalantir  
Pilot app (local): http://127.0.0.1:5173 · API http://localhost:5251

## 1. Purpose

Demonstrate Palantir on **non-production** mailboxes with approval-gated outbound email, then seek limited corporate pilot approval for Graph + optional SSO.

Out of scope for this pilot package: corporate mailbox ingestion, production AI, Teams Phone, SMS.

## 2. Architecture & data flow (pilot)

1. User signs into **Palantir** with a Palantir-owned identity (local email/password JWT today; Entra External ID later).
2. Authenticated user selects **Connect Outlook** — Microsoft Graph delegated OAuth + PKCE (separate from login).
3. Palantir stores encrypted refresh tokens and syncs inbound mail into conversations.
4. Outbound replies create a **draft + approval request**; Graph `sendMail` runs only after human approve.
5. Audit events attribute actor, entity, and action.

```text
Browser (Vite)
  → Palantir API (JWT Bearer)
      → SQLite (local pilot) / Azure SQL (target SoR)
      → Data Protection protected Graph tokens
          → Microsoft Graph (Mail.Read / Mail.Send)
```

**Separation rule:** login identity ≠ mailbox connector. Corporate Entra can later link as an `ExternalIdentity` without changing `User.Id`.

## 3. Microsoft Graph application (pilot)

| Field | Value |
|-------|--------|
| Client ID | `1f220ed2-cc7d-4551-8715-4054aaf0f9e6` |
| Tenant (app registration) | `e8c3156c-9762-469d-b329-0e408f5b2fcb` |
| Authority for OAuth | `common` (personal + work test accounts) |
| Redirect URI | `http://localhost:5251/oauth/microsoft/callback` (Web) |
| Expected pilot mailbox | `palantir.pilot.aanthony@outlook.com` |

### Delegated scopes

| Scope | Use |
|-------|-----|
| `openid` `profile` `email` `offline_access` | Connector session / refresh |
| `User.Read` | Mailbox identity |
| `Mail.Read` | Inbox sync into Palantir conversations |
| `Mail.Send` | **Only** after approval workflow completes |

No application (app-only) mail permissions in this pilot.

## 4. Identity model

| Concept | Role |
|---------|------|
| `User` | Permanent Palantir person (`Guid`); never corporate OID as PK |
| `ExternalIdentity` | Login link (pilot local / future Entra) |
| `LocalPilotCredential` | Dev/pilot password hash only |
| `ConnectedAccount` | Outlook mailbox endpoint metadata |
| `OAuthGrant` | Reference to encrypted Graph tokens |

Demo pilot user (local): `demo@palantir.local` (password rotated for shared envs).

## 5. Token & key management

| Secret | Storage | Notes |
|--------|---------|--------|
| Pilot JWT signing key | `Authentication:PilotJwt:SigningKey` (config / user-secrets) | Dev default; replace for shared hosts; 12h lifetime |
| Graph client secret | `dotnet user-secrets` `Connectors:MicrosoftGraph:ClientSecret` | Not in git |
| Graph access/refresh | Data Protection protected store via `OAuthGrant` | Revoked on disconnect |
| OpenAI / AI | Deferred | Not required for mail pilot |

## 6. Approval & send controls

- Email replies use `Draft` + `ApprovalRequest` (`Pending` → `Approved` / `Rejected`).
- Send is idempotent via `WorkflowAction` key `outlook-send:{approvalId}:{revision}`.
- Failed sends mark draft `SendFailed` and leave an audit trail for retry/investigation.
- AI draft/summarize code exists but is **UI-disabled** until company AI billing/connector is decided; any future AI draft still goes through the same approval gate.

## 7. Audit events (current)

Representative `AuditEvent.EventType` values:

- Conversation: claim / assign / release / message
- Connector: Outlook connect / disconnect / `outlook.mail_synced`
- Outbound: `outlook.reply_draft_created`, `outlook.reply_sent`
- Approvals: created / approved / rejected
- Tasks: created / completed
- AI (when enabled): `ai.summary_created`, `ai.draft_reply_created`

Each event stores organization, actor user id, entity type/id, and optional JSON detail.

## 8. Data retention & deletion (to confirm with IT)

| Topic | Pilot today | Corporate target |
|-------|-------------|------------------|
| System of record | Local SQLite (`palantir.dev.db`) | Azure SQL |
| Disconnect Outlook | Removes connected account + stored grants | Same + confirm Graph revoke |
| Mail copies in Palantir | Conversation/message rows remain until deleted | Retention policy TBD |
| Backups / eDiscovery | N/A locally | Align with Sable policy |
| Right to deletion | Manual DB wipe in pilot | Documented process TBD |

## 9. Threat model highlights

| Risk | Mitigation |
|------|------------|
| Unauthorized external send | Human approval required; Mail.Send unused until approve |
| Stolen Graph token | Data Protection; disconnect revokes local grant; short JWT for API |
| Confused deputy (login vs mailbox) | Separate OAuth apps/flows; org + user checks on connector APIs |
| Prompt injection / AI overreach | AI UI disabled; drafts remain approval-gated when re-enabled |
| Corporate data exposure | Non-prod mailbox only until this package is accepted |
| Secret leakage in git | Client ID OK in config; secrets via user-secrets only |

## 10. Pilot success metrics

- [x] Sign in as Palantir user (not hard-coded headers)
- [x] Connect Outlook for test mailbox
- [x] Sync inbound mail into Inbox
- [x] Draft reply → Approvals → Approve & send
- [x] Disconnect Outlook
- [ ] Multi-user pilot with distinct logins
- [ ] Security review sign-off for corporate Graph app

## 11. Incident response (draft)

1. Disconnect affected Outlook account in Admin.
2. Rotate Graph client secret and pilot JWT signing key.
3. Preserve `AuditEvents` / `WorkflowActions` for forensics.
4. Notify: **TBD** (security owner / IT contact).

## 12. Open items for IT / security

- [ ] Entra External ID (or approved IdP) replacing local passwords
- [ ] Corporate Graph app registration & admin-consent path
- [ ] Retention / DLP / eDiscovery alignment
- [ ] Incident response contacts and severity matrix
- [ ] Named pilot user list and exit criteria
- [ ] Decision on Azure OpenAI vs deferred AI
