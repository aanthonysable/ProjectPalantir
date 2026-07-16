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
2. Put this repository in Teams for posterity.
3. Use `cursor/CURSOR_KICKOFF_PROMPT.md` when starting implementation work in Cursor.
4. Use the backlog to begin planning MVP implementation.
5. Run the Phase 1 foundation locally — see `backend/README.md`.

**Repository:** https://github.com/aanthonysable/ProjectPalantir

## Local quick start

```bash
# API (SQLite by default — Azure SQL not required yet)
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
cd backend && dotnet run --project Palantir.Api

# Web (separate terminal)
cd web && npm install && npm run dev
```

- API / Swagger: http://localhost:5251/swagger
- Web shell: http://localhost:5173


## Naming note

Project Palantir is intended as an internal codename. If this system is later commercialized or marketed externally, the name should be revisited with legal review due to potential confusion with existing companies/products.

## Version 0.2 identity and Microsoft 365 strategy

The initial pilot does not depend on access to Sable's corporate Microsoft Entra tenant. Palantir will use a standalone application identity system and permanent internal user IDs. Corporate Entra SSO can be linked later without recreating users or moving their data.

Microsoft 365 mailboxes are connected as external accounts through delegated OAuth. Individual user consent may work where corporate tenant policy permits it; however, corporate administrators can require approval or block the connection. Development and demonstrations should therefore use a Palantir-owned Microsoft 365 test tenant or approved test accounts until corporate review is complete.
