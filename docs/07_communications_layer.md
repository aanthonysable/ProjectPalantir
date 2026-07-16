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
