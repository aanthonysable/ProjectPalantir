# Software Requirements Specification

## 1. Scope

Project Palantir will provide a multi-user AI collaboration platform for Sable Automation Solutions. It will support individual AI assistants, shared customer communications, routing, reminders, approvals, knowledge search, and connectors to communication systems such as Microsoft Teams, Outlook, email, voice, SMS, and future device-specific integrations.

## 2. User classes

- **Standard employee** - uses personal assistant, receives tasks, reviews drafts, communicates with customers.
- **Project owner** - owns project communication, customer context, and task follow-up.
- **Manager / leadership** - views team workload, unresolved communication, customer status, and audit reports.
- **Administrator** - manages users, roles, connectors, policies, phone queues, and system configuration.
- **Connector agent** - device or service process that exposes capabilities such as sending messages or reading calendars.

## 3. Functional requirements

### Identity and access

- Support organizations, users, teams, roles, devices, sessions, and connector registrations.
- Support standalone Palantir authentication for the pilot, with Microsoft Entra External ID preferred.
- Support later federation and account linking to Sable corporate Entra ID without changing the Palantir user primary key.
- Enforce role-based access control for all records and actions.
- Track user attribution for all messages, approvals, tasks, and connector actions.

### Personal assistant

- Each user has a private assistant context.
- Assistants can summarize messages, draft replies, create reminders, assign tasks, search knowledge, and recommend follow-ups.
- Assistants must ask for approval before external communication unless a user or admin creates an automation rule.
- Assistants must respect user permissions and cannot retrieve restricted content.

### Communications

- Store conversations independent of channel.
- Support email, Teams messages, SMS, voice call records, voicemail transcripts, and internal notes.
- Support central company number routing for voice and SMS using Microsoft Teams Phone or a future provider.
- Support assignment, claiming, transfer, escalation, internal notes, and customer-visible replies.
- Prevent two users from unknowingly replying to the same customer thread.

### Approvals and workflow

- Create approval requests for drafts, sends, task assignments, connector actions, and escalations.
- Record who approved, from which device, at what time, and against which revision.
- Expire stale approvals.
- Prevent duplicate sends through idempotency keys and atomic state transitions.

### Tasks and reminders

- Allow users to create private and shared reminders.
- Allow one user to send a reminder or task to another user.
- Support due dates, assignees, projects, customers, status, priority, and recurrence.
- Notify assigned users across clients.

### Knowledge

- Link communications to customers, contacts, projects, and documents.
- Index approved documents, notes, call transcripts, meeting notes, and project records.
- Provide permission-aware search and AI retrieval.

### Connectors

- Connectors advertise capabilities and online status.
- The platform calls generic capabilities, not vendor-specific methods.
- Initial connectors: Microsoft Teams/Phone, Outlook/Graph, Azure Blob files, desktop notification, and Web UI.
- Future connectors: iMessage via Mac, Android SMS/RCS if feasible, Gmail, Slack, SCADA/project systems, CRM/ERP.

## 4. Non-functional requirements

- Azure-hosted, API-first backend.
- Azure SQL as the system of record.
- Azure SignalR for real-time client updates.
- Azure Blob Storage for attachments and generated files.
- Azure Key Vault for secrets.
- Application Insights for telemetry.
- Encryption in transit and at rest.
- Audit logging for all sensitive operations.
- Modular architecture that can later become a commercial SaaS product.

## 5. MVP definition

The MVP should include:

1. Azure backend with authentication, users, teams, projects, customers, conversations, messages, tasks, approvals, and audit events.
2. Web client and desktop-friendly UI.
3. Microsoft 365/Outlook connector using per-user delegated OAuth for email and calendar basics, initially against test or individually consented accounts.
4. AI drafting and summarization with required approval.
5. Internal reminders and task assignment.
6. Leadership dashboard showing unresolved communication and pending approvals.

## 6. Out of scope for MVP

- Fully custom softphone client.
- Silent iPhone iMessage access.
- Full commercial billing/multi-tenant SaaS operations.
- Autonomous external sends without approval.
- Deep SCADA control actions.

## 7. Identity portability requirements

- The application must generate and retain a provider-neutral `PalantirUserId`.
- External identity records must include provider, issuer/tenant, subject identifier, email, link status, and verification time.
- A user may link multiple login identities and multiple connected service accounts.
- Corporate Entra object IDs must never be used as Palantir's primary user key.
- Administrators must be able to merge or relink duplicate pilot accounts with a fully audited workflow.
- Disabling one login provider must not delete the user's Palantir data.

## 8. Connected-account requirements

- Outlook connections must use OAuth 2.0 delegated permissions through Microsoft Graph.
- Palantir authentication and Outlook authorization must remain separate flows.
- The UI must clearly display granted scopes, tenant, mailbox, connection health, and revocation controls.
- The system must tolerate `admin consent required` and `connection blocked by organization` states.
- Tokens must be encrypted and refreshable; failures must transition the connector to `ReauthorizationRequired` rather than silently losing data.
