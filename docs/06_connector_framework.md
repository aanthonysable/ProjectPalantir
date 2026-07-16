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

## Microsoft Outlook connector - pilot mode

The Outlook connector is user-scoped and uses delegated Microsoft Graph permissions. It must support these lifecycle states:

- `NotConnected`
- `AuthorizationPending`
- `Connected`
- `AdminConsentRequired`
- `ReauthorizationRequired`
- `Revoked`
- `PolicyBlocked`
- `Error`

The connector must expose the authenticated mailbox, source tenant, granted capabilities, token health, and last successful synchronization. Capability availability is derived from granted scopes rather than assumed from connector type.

The connector must not be considered a login identity. One Palantir user may connect multiple mailboxes, subject to organization policy.
