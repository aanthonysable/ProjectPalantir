# SignalR Event Catalog

## Client subscriptions

Clients subscribe to organization, user, team, project, and conversation channels based on authorization.

## Events

| Event | Description |
|---|---|
| conversation.created | New conversation created |
| conversation.updated | Assignment, status, participant, or metadata changed |
| message.created | New message, call note, voicemail transcript, or internal note |
| draft.created | AI/user draft created |
| draft.updated | Draft text or metadata changed |
| approval.requested | Approval required from a user |
| approval.completed | Approval accepted, rejected, expired, or delegated |
| action.created | Executable action queued |
| action.claimed | Connector or worker claimed action |
| action.completed | Action completed successfully |
| action.failed | Action failed |
| task.created | Task/reminder created |
| task.updated | Task/reminder changed |
| notification.created | User notification created |
| connector.online | Connector became available |
| connector.offline | Connector became unavailable |
