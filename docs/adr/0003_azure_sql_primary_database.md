# ADR 0003 - Azure SQL as Primary Database

## Status
Accepted

## Context
The system requires strong relationships, approvals, duplicate-send prevention, audit trails, user attribution, tasks, projects, and conversations.

## Decision
Use Azure SQL Database as the primary system of record. Use JSON columns for provider metadata where flexibility is needed. Use Blob Storage for large attachments.

## Consequences
- Strong transactional consistency.
- Easier reporting and audit.
- Better fit for workflow state than pure document storage.
