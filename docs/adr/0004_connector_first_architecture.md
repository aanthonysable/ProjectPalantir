# ADR 0004 - Connector-First Architecture

## Status
Accepted

## Context
Sable employees use different operating systems and communication tools. Not every device can access the same capabilities, especially iMessage or Android SMS.

## Decision
Define generic capabilities and let connectors implement them. The AI and workflow engine call capabilities rather than vendor-specific APIs.

## Consequences
- Easier to add Teams, Outlook, iMessage, Android SMS, Gmail, and future systems.
- Core platform stays independent from channel details.
- Device limitations are represented as capability availability.
