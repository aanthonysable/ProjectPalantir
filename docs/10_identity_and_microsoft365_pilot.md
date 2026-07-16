# Identity and Microsoft 365 Pilot Strategy

## Purpose

Enable Project Palantir development and demonstration before Sable grants access to its corporate Microsoft Entra and Microsoft 365 tenant.

## Core rule

Palantir owns the application user record. External systems only provide login assertions or connector authorization.

## Domain model

- `User`: permanent Palantir person record.
- `ExternalIdentity`: a login identity such as pilot email login, personal Microsoft account, or future corporate Entra identity.
- `ConnectedAccount`: an authorized service endpoint such as a specific Outlook mailbox.
- `OAuthGrant`: encrypted/revocable authorization material referenced through a secure credential store.
- `OrganizationMembership`: Sable membership, roles, teams, and status.

## Pilot flows

### Sign in

1. User authenticates through the Palantir-controlled identity tenant.
2. Palantir maps issuer and subject to `ExternalIdentity`.
3. The identity resolves to a permanent `PalantirUserId`.
4. Palantir authorization uses organization membership and roles.

### Connect Outlook

1. Authenticated user selects Connect Outlook.
2. Palantir starts Microsoft OAuth with PKCE and minimum scopes.
3. Microsoft authenticates the mailbox owner and evaluates tenant consent policy.
4. On success, Palantir stores mailbox metadata and an encrypted token reference.
5. On admin-consent requirement or policy block, Palantir records the state and presents clear guidance.

### Corporate transition

1. Corporate Entra application access is approved.
2. User signs into the existing Palantir account.
3. User or administrator links the corporate identity.
4. Existing projects, messages, tasks, preferences, and audit history remain attached to the same user ID.
5. Standalone login can remain as recovery during transition, then be disabled by policy.

## Proof-of-concept environment

Use a Palantir-owned Microsoft 365 test tenant with synthetic or approved test data. Do not ingest confidential corporate mail until corporate security and privacy approval is complete.

## Corporate approval package

Prepare:

- Architecture and data-flow diagram.
- Exact Microsoft Graph delegated scopes.
- Data retention and deletion policy.
- Token and key management design.
- Threat model and incident response plan.
- Pilot user list and success metrics.
- Demonstration of approval-gated sends and audit attribution.
