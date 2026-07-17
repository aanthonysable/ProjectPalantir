# Product Roadmap

## Phase 0 - Definition

Deliverables:

- Leadership pitch package.
- Software requirements.
- Architecture design.
- Database model.
- API draft.
- Connector model.
- Security model.
- Initial backlog.

## Phase 1 - Foundation MVP

- Azure environment.
- Entra authentication.
- Azure SQL schema.
- API service.
- SignalR real-time updates.
- Web client shell.
- Users, teams, projects, customers.
- Audit logging.

## Phase 2 - Communications MVP

- Conversation model.
- Internal notes.
- Assignment/claiming.
- Tasks/reminders.
- Basic notifications.
- Manual message entry/import.

## Phase 3 - Microsoft 365 Integration

- Outlook email connector.
- Calendar connector.
- Teams presence/context.
- Teams notification connector.
- SharePoint/OneDrive document search.

## Phase 4 - AI Assistant

- Summaries.
- Draft replies.
- Approval workflow.
- Knowledge retrieval (MVP: Admin upload → Azure Blob `knowledge` + SQL chunks; Overview recap/Ask retrieve excerpts).
- User writing preferences.
- Project/customer context.

## Phase 5 - Central Communications

- Teams Phone evaluation/integration.
- Central number routing.
- Call logs.
- Voicemail transcription.
- SMS if supported by licensing/region.
- Queue dashboard.

## Phase 6 - Mobile and Desktop

- iOS approval app.
- Android approval app.
- Windows desktop app.
- macOS app.
- Push notifications.

## Phase 7 - Advanced Automation

- Rules engine.
- Escalation policies.
- Scheduled digests.
- Cross-user reminders.
- Project status automation.
- Additional connectors.

## Phase 8 - Productization

- Tenant isolation.
- Commercial branding review.
- Billing readiness if needed.
- Connector SDK.
- Customer deployment model.
- Support tooling.

## Phase 0A - approval-independent pilot foundation

- Create Palantir Entra External ID tenant.
- Implement provider-neutral users and external identity linking.
- Create a Palantir-owned Microsoft 365 test tenant and test mailboxes.
- Register a multitenant Microsoft Graph application using delegated OAuth and PKCE.
- Implement `Connect Outlook`, consent-state handling, encrypted token storage, disconnect, and reauthorization.
- Demonstrate read, summarize, draft, approve, and send using non-production mailboxes.
- Prepare a corporate security review package containing scopes, data flow, retention, threat model, and pilot controls.

## Corporate onboarding milestone

- Obtain corporate Entra application approval for a limited pilot group.
- Add corporate SSO as a linked identity provider.
- Map approved corporate groups to Palantir roles where desired.
- Link existing pilot users rather than creating replacements.
- Move selected users from test mailboxes to corporate Outlook connections.

## Parallel track - Ops systems intelligence (started 2026-07-17)

Branch from the communications plan to unify operational work tools. Detail: `docs/13_ops_systems_integrations.md`, ADR 0005.

- MaintainX connector — **two environments** (separate credentials).
- EZRentOut connector — assets / rentals.
- Monday.com connector — **Sable Operations workspace only** (other workspaces deprecated).
- MaintainX status semantics: OPEN / IN_PROGRESS = physical work; ON_HOLD = physically finished (back office close-out); DONE = billed/closed.
- Unified open-work view + AI insights across sources.
- Future: limited accounting reads via SAP **or** Syteline (`IAccountingConnector`).
