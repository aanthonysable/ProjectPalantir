# System Architecture

## High-level architecture

```text
Clients: Windows, macOS, Web, iOS, Android
        |
        | HTTPS + SignalR + Push Notifications
        v
Azure API Layer / Palantir Core
        |
        +-- Identity and Access Service
        +-- Communication Service
        +-- AI Orchestrator
        +-- Workflow Service
        +-- Knowledge Service
        +-- Notification Service
        +-- Connector Registry
        +-- Audit Service
        |
        +-- Azure SQL Database
        +-- Azure Blob Storage
        +-- Azure Key Vault
        +-- Azure SignalR Service
        +-- Application Insights
        +-- Azure AI / OpenAI
```

## Core services

### Identity Service
Manages organizations, users, teams, roles, devices, sessions, permissions, and access policies.

### Communication Service
Stores conversations, messages, calls, voicemail transcripts, SMS, emails, Teams interactions, assignments, and channel metadata.

### AI Orchestrator
Builds context, retrieves knowledge, calls AI models, creates summaries and drafts, proposes tool calls, and converts model outputs into approval requests.

### Workflow Service
Manages tasks, reminders, approvals, action queues, escalations, retries, and idempotency.

### Knowledge Service
Indexes documents, communications, project notes, and approved files. Provides permission-aware retrieval to the AI orchestrator and users.

### Notification Service
Sends real-time events, push notifications, email notifications, Teams notifications, and desktop alerts.

### Connector Registry
Tracks connectors, capabilities, online state, credentials, routes, and health checks.

### Audit Service
Creates immutable audit records for all reads, writes, approvals, sends, assignments, configuration changes, and failed security checks.

## Deployment model

Recommended Azure services:

- Azure App Service or Azure Container Apps for APIs and workers.
- Azure SQL Database for system of record.
- Azure Blob Storage for attachments.
- Azure SignalR Service for real-time UI updates.
- Azure Key Vault for secrets.
- Azure Service Bus for reliable background actions once scale requires it.
- Application Insights and Log Analytics for observability.
- Entra ID for employee authentication.

## Client model

Each client is a UI shell using the same API:

- Web app for immediate deployment and leadership visibility.
- Windows desktop wrapper or native client for power users.
- macOS desktop client for Mac users and future Apple-specific connectors.
- iOS and Android mobile apps for push notifications, approvals, reminders, and message review.

## Connector model

Connectors expose capabilities. A connector may be cloud-hosted, desktop-hosted, or mobile-hosted.

Examples:

- Microsoft Graph connector: email, calendar, Teams context.
- Teams Phone connector: calls, voicemail, SMS if available/licensed.
- Mac connector: Apple Messages/iMessage capability for users who opt in.
- Android connector: SMS/RCS capability if platform policies and permissions allow.
- File connector: SharePoint, OneDrive, Blob Storage.

## Critical design decision

The backend should not be dependent on any single user's Mac. Since the platform is intended for the whole team, Azure must be the permanent system of record and orchestration layer. Device-specific connectors should be optional capability providers.

## Standalone pilot identity architecture

```text
Palantir Login                         External Services
----------------                      -----------------
Entra External ID                     Corporate Microsoft 365
or pilot local identity               Personal/test Microsoft account
        |                                      |
        v                                      v
  PalantirUserId <---- identity link ---- ExternalIdentity
        |
        +---- organization roles / teams / permissions
        +---- ConnectedAccount ---- OAuth grant ---- Microsoft Graph
```

The login identity proves who is using Palantir. A connected account authorizes a specific connector to access an external service. These are intentionally independent.

### Recommended services for the pilot

- Microsoft Entra External ID for standalone application authentication.
- Azure SQL for `Users`, `ExternalIdentities`, `ConnectedAccounts`, `OAuthGrants`, and connector state.
- Azure Key Vault plus envelope encryption for refresh tokens and connector secrets.
- Microsoft Graph delegated OAuth for Outlook mailbox access.
- Palantir-owned Microsoft 365 test tenant for development and demonstrations.

### Corporate Entra transition

Corporate SSO is introduced by adding a trusted issuer and linking its subject identifier to the existing `PalantirUserId`. Authorization continues to use Palantir organization membership and roles until leadership chooses to map corporate groups into Palantir roles.
