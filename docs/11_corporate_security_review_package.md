# Corporate Security Review Package (Draft)

Status: **working draft** for the Palantir pilot. Fill gaps before requesting corporate Outlook / Entra access.

## 1. Purpose

Demonstrate Palantir on non-production mailboxes with approval-gated outbound email, then seek limited corporate pilot approval.

## 2. Architecture & data flow (pilot)

1. User signs into **Palantir** (local pilot JWT today; Entra External ID later).
2. User connects **Outlook** via Microsoft Graph delegated OAuth (separate from login).
3. Palantir syncs inbound mail into conversations; replies require **human approval** before Graph `sendMail`.
4. Audit events record claim, draft, approve, send, and connector actions.

```text
Browser → Palantir API (JWT) → SQLite (local) / Azure SQL (later)
                ↘ Graph OAuth tokens (Data Protection) → Microsoft Graph
```

## 3. Microsoft Graph delegated scopes (pilot)

| Scope | Use |
|-------|-----|
| `openid` `profile` `email` `offline_access` | Sign-in / refresh for connector |
| `User.Read` | Mailbox identity |
| `Mail.Read` | Inbox sync |
| `Mail.Send` | Approval-gated outbound only |

Pilot mailbox: `palantir.pilot.aanthony@outlook.com` (non-corporate).

## 4. Identity model

- `User.Id` is Palantir-owned UUID (never corporate OID as primary key).
- `ExternalIdentity` links login providers.
- `ConnectedAccount` / `OAuthGrant` hold mailbox connector state only.
- Login ≠ Outlook connection.

## 5. Token & key management

- Pilot JWT: symmetric signing key in config / user-secrets (dev only).
- Graph tokens: ASP.NET Data Protection protected store.
- OpenAI / AI keys: deferred; not required for mail pilot.

## 6. Data retention & deletion (to confirm)

- Local SQLite for development; production target Azure SQL.
- Retention windows, deletion on disconnect, and backup policy: **TBD with IT**.

## 7. Threat model highlights

| Risk | Mitigation |
|------|------------|
| Unauthorized send | Approval required before Graph send; audit trail |
| Token theft | Encrypted token store; short pilot JWT lifetime |
| Prompt injection / AI overreach | AI features deferred; when enabled drafts still approval-gated |
| Corporate data exposure | Non-prod mailbox until security approval |

## 8. Pilot success metrics

- Connect Outlook, sync mail, draft reply, approve, send — end to end.
- Clear attribution of actor on audit events.
- Ability to disconnect and revoke connector.

## 9. Open items for IT / security

- [ ] Entra External ID (or approved IdP) for production-like login
- [ ] Corporate Graph app registration & admin consent path
- [ ] Retention / DLP / eDiscovery alignment
- [ ] Incident response contacts
- [ ] Pilot user list and exit criteria
