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

## Pilot without immediate corporate tenant access

Palantir can be demonstrated without changing Sable's corporate Microsoft configuration. The pilot will use a standalone Palantir login system and non-production Microsoft 365 accounts. This allows the team to validate the user experience, AI drafting, approvals, audit trail, and Outlook integration before requesting corporate access.

When leadership and IT approve the project, existing pilot users can link their corporate Entra identities and Outlook mailboxes. The design avoids recreating accounts or migrating Palantir data.
