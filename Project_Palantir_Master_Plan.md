# Project Palantir Export Package

**Codename:** Project Palantir  
**Owner:** Sable Automation Solutions  
**Date:** 2026-07-16  
**Status:** Concept / Product Definition / Architecture Planning  
**Intended use:** Internal leadership review, product planning, and Cursor development kickoff.

Project Palantir is an internal codename for an AI-powered collaboration and communications platform. It gives every employee a secure digital assistant while unifying company communications, knowledge, customer context, projects, reminders, approvals, and workflows.

## Contents

- `leadership/` - executive pitch materials
- `docs/` - product, architecture, AI, connector, security, UI, and roadmap documents
- `api/` - initial OpenAPI draft and real-time event catalog
- `database/` - relational schema draft and ERD notes
- `diagrams/` - architecture diagrams in Mermaid format
- `backlog/` - phased engineering backlog
- `cursor/` - Cursor kickoff prompt and engineering working instructions
- `infra/` - Azure deployment planning notes

## Recommended first use

1. Share the leadership pitch deck and executive brief with Sable leadership.
2. Put the full folder in Teams for posterity.
3. Add the `cursor/` folder and core docs to a new Cursor repository.
4. Use the backlog to begin planning MVP implementation.

## Naming note

Project Palantir is intended as an internal codename. If this system is later commercialized or marketed externally, the name should be revisited with legal review due to potential confusion with existing companies/products.
# Project Palantir - Leadership Brief

## One-sentence summary

Project Palantir is an internal AI collaboration and communications platform that gives every employee a secure digital assistant while centralizing customer communications, project knowledge, reminders, approvals, and workflow coordination.

## Why now

Customer communications are spread across personal phones, email, Teams, individual calendars, and project files. This creates missed follow-ups, poor visibility, duplicated effort, and knowledge loss when employees are unavailable or leave.

## What Palantir changes

- Customers contact Sable through central company channels instead of personal numbers.
- Communications are routed to the right employee or team.
- Employees receive AI-generated summaries, suggested replies, and project context.
- External sends require approval and are attributed to the responsible employee.
- Tasks and reminders can be assigned across the team.
- Project and customer knowledge becomes searchable and reusable.

## Expected benefits

- Faster response to customers.
- Better handoffs between employees.
- Reduced administrative burden.
- Reduced dependence on tribal knowledge.
- Improved customer communication history.
- Better leadership visibility into open issues.
- Foundation for future automation.

## Proposed MVP

1. Azure-hosted backend.
2. Microsoft Entra authentication.
3. Unified inbox and conversation history.
4. Tasks, reminders, and approvals.
5. Outlook and Teams integration.
6. AI summaries and draft replies.
7. Leadership dashboard for unresolved communication.

## Strategic value

Palantir can begin as an internal tool but should be architected as if it may later become a product. Sable operates in industrial automation, where teams often bridge engineering, field service, operations, customer support, and business communications. This creates a product opportunity beyond generic enterprise AI tools.
# Project Palantir - Product Vision

## Mission

Project Palantir is an AI-powered collaboration and communications platform that provides every employee with a secure digital assistant while unifying organizational knowledge, communications, workflows, reminders, customer context, and automation into one intelligent operating layer.

## Problem statement

Sable's work is spread across email, Teams, phone calls, customer texts, personal devices, engineering documents, calendars, project folders, and tribal knowledge. Employees often know what needs to happen, but the information needed to act quickly is fragmented.

The result is avoidable friction:

- Customers contact employees directly on personal numbers.
- Customer communication history is scattered and difficult to audit.
- Important follow-ups depend on memory or manual reminders.
- Project context lives in inboxes, chats, files, and individual employees' heads.
- New employees cannot easily inherit the communication history of a customer or project.
- Leadership lacks a unified view of customer response status, unresolved requests, and operational load.

## Product vision

Palantir becomes the intelligent coordination layer between employees and the systems they already use.

Instead of asking employees to check six systems, Palantir lets them ask for an outcome:

- "Find the latest drawing revision for this customer."
- "Draft a reply to this customer and ask me before sending."
- "Remind Mike tomorrow morning to review the hydraulic schematic."
- "Show every unanswered customer message from this week."
- "Summarize what happened on Project 1785 since Monday."
- "Route this call to the project owner, then escalate if unanswered."

## Design principles

1. **Human first** - AI augments employees; it does not replace accountability.
2. **Approval first** - external actions require approval unless an explicit automation policy allows them.
3. **Identity first** - every action is attributable to a user, device, connector, and approval event.
4. **Connector first** - Outlook, Teams, SMS, phone, iMessage, Android SMS, files, calendars, and future systems are connectors behind common capabilities.
5. **Platform independent** - Windows, macOS, Web, iOS, and Android are first-class clients.
6. **Organization memory** - knowledge should survive employee turnover and device changes.
7. **Security by default** - least privilege, encryption, audit trails, and role-based access control are baseline features.

## Strategic outcome

The short-term goal is an internal Sable tool that reduces communication friction and improves customer response. The long-term opportunity is a productized AI collaboration platform for operational teams that do not fit neatly into one software ecosystem.
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
- Integrate with Microsoft Entra ID for authentication.
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
3. Microsoft 365/Outlook connector for email and calendar basics.
4. AI drafting and summarization with required approval.
5. Internal reminders and task assignment.
6. Leadership dashboard showing unresolved communication and pending approvals.

## 6. Out of scope for MVP

- Fully custom softphone client.
- Silent iPhone iMessage access.
- Full commercial billing/multi-tenant SaaS operations.
- Autonomous external sends without approval.
- Deep SCADA control actions.
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
# Domain Model

## Core entities

### Organization
The tenant boundary. For initial internal deployment, Sable is the only organization.

### User
A human employee with identity, preferences, permissions, devices, connectors, assistant memory, and audit history.

### Team
A group of users, such as Engineering, Field Service, Controls, Leadership, Sales, or Support.

### Device
A registered computer or mobile device. Devices can host UI clients and may optionally host connector agents.

### Connector
An integration with a service or device capability. Examples: Outlook, Teams, Teams Phone, Mac Messages, Android SMS, SharePoint, SCADA systems.

### Capability
A generic action a connector can perform, such as SendMessage, ReadMessages, CreateMeeting, GetPresence, SearchFiles, or NotifyUser.

### Customer
A company or person outside Sable.

### Contact
An individual associated with a customer or internal team.

### Project
A work context such as a job, site, service engagement, drawing package, software release, or customer initiative.

### Conversation
A channel-independent communication thread. Conversations can include emails, SMS, Teams messages, calls, voicemails, internal notes, documents, tasks, and approvals.

### Message
An individual communication event within a conversation.

### Draft
A proposed outbound message generated by a user or AI.

### ApprovalRequest
A request for a user to approve, edit, reject, or delegate a proposed action.

### Action
An executable operation such as sending a message, creating a reminder, assigning a task, routing a call, or updating a record.

### Task / Reminder
Work item assigned to one or more users.

### AuditEvent
Immutable record of what happened, who did it, and why.

## Relationships

- Organization has many users, teams, customers, projects, connectors, and conversations.
- User has many devices, preferences, tasks, approvals, and audit events.
- Project has many conversations, files, tasks, contacts, and knowledge items.
- Conversation has many messages, participants, assignments, drafts, tasks, and notes.
- Draft may create an approval request.
- Approval request may create an action.
- Action is claimed and executed by a connector or service worker.
# Security Architecture

## Goals

- Protect customer, employee, and company communication.
- Prevent unauthorized data access.
- Prevent unauthorized external sends.
- Preserve user attribution for every sensitive action.
- Provide auditability suitable for leadership review and future compliance needs.

## Authentication

Use Microsoft Entra ID for employee authentication. The platform should support single sign-on and conditional access where available.

## Authorization

Use layered authorization:

1. Organization boundary.
2. Role-based access control.
3. Team/project membership.
4. Connector permissions.
5. Record-level permissions.
6. Action-specific approval policies.

## Approval policy

Default policy: all external sends require human approval.

External actions include:

- Sending email.
- Sending SMS or Teams messages to external parties.
- Placing outbound calls.
- Sharing files externally.
- Creating customer-visible notes.
- Updating customer-facing project status.

Automation exceptions must be explicit, scoped, auditable, and reversible.

## Secrets

- Store connector credentials in Azure Key Vault.
- Never store raw secrets in source code.
- Rotate credentials and webhook secrets.
- Use managed identities wherever possible.

## Data protection

- Encrypt in transit with TLS.
- Use Azure SQL encryption at rest.
- Encrypt highly sensitive fields if required.
- Store attachments in private Blob containers.
- Use short-lived signed URLs for attachment access.
- Maintain retention policies for message content and transcripts.

## Audit logging

Audit the following:

- Login and logout.
- User/device registration.
- Connector authorization changes.
- Message reads and sends.
- Draft creation and edits.
- Approval decisions.
- Assignment changes.
- Task creation and completion.
- Admin configuration changes.
- Failed authorization attempts.
- AI tool call proposals and executed actions.

## AI safety boundaries

- AI cannot directly execute external actions.
- AI outputs are treated as suggestions until converted into structured approval requests.
- Tool calls are validated by application logic.
- Prompts must include user permissions and available capabilities.
- Retrieval must be permission-aware.
- AI-generated summaries should include confidence and source links when practical.
# AI Orchestration Design

## Purpose

The AI Orchestrator transforms user intent and incoming communication into structured suggestions, summaries, drafts, tasks, and approval requests.

## Core pattern

```text
Input event or user request
        ↓
Normalize context
        ↓
Retrieve relevant knowledge
        ↓
Call AI model
        ↓
Validate structured output
        ↓
Create draft/task/approval/action
        ↓
Notify affected users
```

## Context sources

- Current user profile and permissions.
- Conversation history.
- Customer and contact records.
- Project records.
- Recent messages and tasks.
- Calendar availability.
- Approved organizational knowledge.
- User writing preferences.
- Connector capabilities.

## Memory layers

- **Organization memory** - standard processes, product information, company knowledge.
- **Team memory** - team-specific procedures and responsibilities.
- **Project memory** - project decisions, files, history, stakeholders.
- **Customer memory** - contacts, preferences, history, open issues.
- **User memory** - private preferences and writing style.
- **Session memory** - temporary conversation state.

## Tool/capability model

AI should propose structured operations such as:

- DraftMessage
- CreateTask
- CreateReminder
- SummarizeConversation
- SearchKnowledge
- RequestApproval
- RecommendRoute

The application decides whether the operation is allowed and which connector can perform it.

## Approval-aware actions

The orchestrator may create drafts and approval requests, but it cannot send messages directly. Sending is performed only by workflow/action execution after approval and authorization checks.

## Prompting standards

System prompts should include:

- Current user's role.
- Current organization's policy.
- Available tools/capabilities.
- Approval requirements.
- Data handling rules.
- Instruction to avoid unsupported claims.
- Instruction to ask for missing critical information when required.

## Evaluation

AI behavior should be tested against scenarios:

- Customer asks for urgent support.
- User asks to send a sensitive reply.
- User asks to see a restricted project.
- Two users attempt to reply to same SMS thread.
- AI lacks enough context.
- AI recommends routing to unavailable employee.
# Connector Framework

## Purpose

Connectors allow Palantir to interact with external systems without hardcoding vendor-specific behavior into the core platform.

## Connector types

- **Cloud connector** - runs in Azure and connects to APIs such as Microsoft Graph.
- **Desktop connector** - runs on Windows or macOS to access device-specific capabilities.
- **Mobile connector** - runs on iOS or Android for mobile-specific approvals, notifications, and permitted messaging functions.
- **Webhook connector** - receives events from services such as Teams Phone, email providers, or future phone systems.

## Capability advertisement

Each connector registers capabilities:

```json
{
  "connectorType": "MicrosoftGraph",
  "capabilities": [
    "ReadEmail",
    "SendEmail",
    "ReadCalendar",
    "CreateMeeting",
    "GetPresence"
  ]
}
```

## Initial capability set

- SendMessage
- ReadMessages
- ReadCalendar
- CreateMeeting
- GetPresence
- SearchFiles
- ReadFile
- UploadFile
- NotifyUser
- PlaceCall
- RouteCall
- ReadVoicemail
- SendSMS

## Required connector behavior

- Register with backend.
- Report heartbeat and health.
- Advertise capabilities.
- Accept only signed/authorized commands.
- Validate idempotency keys.
- Report status updates.
- Store no unnecessary data locally.
- Log execution results.

## Microsoft connector focus

Initial priority should be Microsoft 365 because it can provide:

- Entra identity alignment.
- Outlook email.
- Calendar.
- Teams context.
- Teams Phone/voice/SMS possibilities.
- SharePoint/OneDrive file access.

## Device-specific connectors

### Mac connector
Optional future connector for Apple Messages and local Mac resources. It should be treated as a capability provider, not as the platform backend.

### Android connector
Potential future connector for SMS/RCS depending on Android permissions, enterprise deployment method, and user consent.

## Failure handling

Commands must support:

- Pending
- Claimed
- Running
- Completed
- Failed
- Expired
- Cancelled
- Retried
- Dead-lettered
# Communications Layer

## Goal

Create a unified communication record across email, Teams, phone calls, SMS, voicemail, customer texts, internal notes, tasks, and AI drafts.

## Central number strategy

Customers should call and text a central Sable number rather than employee personal numbers. Palantir should integrate with Microsoft Teams Phone or another provider to route communication while preserving individual attribution.

## Conversation model

All communication becomes a conversation:

- Channel-independent thread.
- Linked to contacts, customer, project, assigned users, and tasks.
- Contains external messages, internal notes, drafts, attachments, calls, voicemails, and summaries.

## Routing

Routing should combine deterministic rules and AI recommendations.

Deterministic rules:

- Business hours.
- Department/queue.
- Project owner.
- Customer owner.
- On-call rotation.
- Escalation timeout.
- Presence/availability.

AI recommendations:

- Intent classification.
- Project/customer identification.
- Urgency estimate.
- Suggested assignee.
- Drafted reply.
- Summary of related history.

## Shared inbox behavior

- Conversations can be unassigned, assigned, claimed, escalated, or closed.
- Users can add internal notes invisible to customers.
- A conversation lock or active draft should prevent duplicate replies.
- Ownership and handoff history should be visible.

## Example flow

1. Customer texts central number.
2. Provider webhook posts message to Palantir.
3. Palantir matches phone number to contact/customer.
4. AI classifies intent and links possible project.
5. Routing engine assigns conversation to project owner or queue.
6. User sees customer context and suggested reply.
7. User approves/edits response.
8. Message sends through provider connector.
9. Audit record logs who sent it and why.
# UI/UX Design

## Product surfaces

- Web application.
- Windows desktop application.
- macOS desktop application.
- iPhone application.
- Android application.
- Admin console.

## Primary navigation

- Today
- Inbox
- Assistant
- Tasks
- Projects
- Customers
- Calls
- Knowledge
- Approvals
- Admin

## Today screen

Shows:

- Pending approvals.
- Unanswered customer messages.
- Today's meetings.
- Assigned tasks.
- Customer callbacks.
- AI recommendations.
- Overdue reminders.

## Inbox screen

Unified communications list with filters:

- Assigned to me.
- Team queue.
- Unassigned.
- Awaiting approval.
- Waiting on customer.
- Overdue.
- High urgency.

## Conversation screen

Includes:

- Timeline of messages/calls/notes/tasks.
- Customer and project sidebar.
- AI summary.
- Suggested reply.
- Approval actions.
- Internal note box.
- Assignment and escalation controls.

## Assistant screen

Chat-like interface for asking Palantir to search, summarize, draft, schedule, assign, or remind.

## Admin screen

Includes:

- Users and roles.
- Teams.
- Connectors.
- Phone queues.
- Routing rules.
- Automation policies.
- Audit logs.
- Retention settings.

## Visual direction

The interface should feel operational, clean, and trustworthy. Avoid playful chatbot aesthetics. Emphasize clear status, attribution, timestamps, and action ownership.
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
- Knowledge retrieval.
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

# Version 0.2 Addendum - Standalone Pilot Identity and Outlook Connectivity

## Decision

Palantir will launch with an independent identity plane rather than requiring the corporate Microsoft Entra tenant. Every person receives a permanent `PalantirUserId` stored in Azure SQL. Login identities and connected service accounts are linked records, not primary keys.

## Initial authentication

- Palantir-controlled identity tenant, preferably Microsoft Entra External ID.
- Email/password or email one-time passcode for the pilot.
- Optional personal Microsoft account login where useful.
- Organization membership, roles, teams, and audit attribution remain inside Palantir.

## Future corporate integration

When Sable approval is obtained, corporate Entra SSO is added as another external identity provider. Existing users are linked by an administrator-assisted account-linking process. No project, task, communication, preference, or audit data is migrated.

## Outlook connection strategy

Users may select **Connect Outlook**, complete Microsoft OAuth, and grant delegated Microsoft Graph permissions. This is separate from Palantir login. Start with least-privileged scopes and add capabilities incrementally:

1. Identity/linking: `openid profile email offline_access User.Read`
2. Read pilot: `Mail.Read`, optionally `Calendars.Read` and `Contacts.Read`
3. Approved sends: `Mail.Send`

Corporate policy may still require administrator consent. The proof of concept must not depend on bypassing that policy. Use a test Microsoft 365 tenant or non-production accounts until approval is granted.

## Prohibited shortcuts

Do not collect Microsoft passwords, browser cookies, raw Outlook tokens, or legacy IMAP credentials. OAuth tokens must be encrypted, scoped, revocable, and stored through the connector credential service with Azure Key Vault protection.
