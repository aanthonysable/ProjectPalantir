# Cursor Kickoff Prompt

You are Developer #2 for Project Palantir, an internal Sable Automation Solutions AI collaboration and communications platform.

Read the repository documentation before writing code. Treat the docs as the source of truth. Do not make major architectural changes without creating an ADR.

## Current objective

Build the Phase 1 foundation:

- Azure-ready ASP.NET Core Web API.
- Azure SQL schema/migrations.
- Entra ID authentication placeholder.
- Organizations, users, teams, devices, customers, projects, conversations, messages, drafts, approvals, tasks, connectors, and audit events.
- REST API skeleton.
- SignalR hub skeleton.
- Basic web client shell.

## Critical rules

- Every external action must require approval.
- Every action must have user attribution.
- Use connector capabilities, not vendor-specific logic in core services.
- Azure SQL is the system of record.
- Keep business logic out of controllers.
- Write tests for workflow state transitions.
- Use idempotency keys for send/action execution.
- Log audit events for sensitive operations.

## Suggested repository structure

```
project-palantir/
  backend/
    Palantir.Api/
    Palantir.Application/
    Palantir.Domain/
    Palantir.Infrastructure/
    Palantir.Tests/
  web/
  desktop/
  mobile/
  connectors/
    MicrosoftGraph/
    TeamsPhone/
  docs/
  database/
  infra/
```

## First implementation tasks

1. Create solution and project structure.
2. Define domain entities.
3. Create EF Core DbContext.
4. Add initial migrations.
5. Implement health endpoint.
6. Implement audit event writer.
7. Implement conversations CRUD.
8. Implement approval workflow state machine.
9. Implement SignalR hub for notifications.
10. Create web client shell with inbox placeholder.

## Mandatory identity architecture update (v0.2)

Implement provider-neutral identity from the first migration:

- `User.Id` is a Palantir-generated UUID.
- Login identities are stored in `ExternalIdentities` using provider + issuer/tenant + subject.
- Service authorizations are stored separately in `ConnectedAccounts` and `OAuthGrants`.
- Use standalone pilot authentication first; do not assume access to Sable corporate Entra.
- Implement Microsoft Graph delegated OAuth as a connector flow, not the primary login flow.
- Model admin-consent-required, policy-blocked, revoked, and reauthorization-required states.
- Never store passwords or basic-auth mailbox credentials.
- Keep tenant IDs, client IDs, issuers, scopes, and redirect URIs environment-configurable.
