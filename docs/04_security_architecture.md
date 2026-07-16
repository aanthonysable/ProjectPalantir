# Security Architecture

## Goals

- Protect customer, employee, and company communication.
- Prevent unauthorized data access.
- Prevent unauthorized external sends.
- Preserve user attribution for every sensitive action.
- Provide auditability suitable for leadership review and future compliance needs.

## Authentication

Use a Palantir-controlled identity provider for the pilot, preferably Microsoft Entra External ID. The platform must later support corporate Microsoft Entra SSO, conditional access, and account linking without replacing internal user records.

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

## OAuth and connected-account security

- Microsoft 365 access must use delegated OAuth 2.0 and Microsoft Graph.
- Request the minimum scopes required for each pilot stage.
- Store refresh tokens only in encrypted connector credential records; protect encryption keys in Azure Key Vault.
- Never request or retain employee Microsoft passwords, app passwords, browser cookies, or legacy basic-auth credentials.
- Provide user-visible disconnect and revoke controls.
- Treat tenant-admin denial as an expected connector state, not an error to bypass.
- Record consent time, tenant ID, mailbox ID, scopes, token version, and revocation events in the audit log.
- Apply strict redirect URI allowlists, PKCE, state/nonce validation, and anti-CSRF protections.

## Identity-linking controls

Linking a corporate Entra identity to an existing pilot user must require recent authentication to both identities or administrator-assisted verification. Account merge actions must be reversible during a defined safety window and fully audited.
