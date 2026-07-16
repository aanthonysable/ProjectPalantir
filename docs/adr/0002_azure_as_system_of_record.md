# ADR 0002 - Azure as System of Record

## Status
Accepted

## Context
The project moved from a personal assistant concept to a company-wide collaboration platform used by Windows, macOS, iOS, Android, and Web users.

## Decision
Use Azure-hosted services as the permanent backend and system of record. Device-specific connectors may perform specialized actions, but the core platform remains in Azure.

## Consequences
- Supports team-wide usage.
- Provides centralized identity, data, audit, and availability.
- Avoids depending on a single Mac as backend.
