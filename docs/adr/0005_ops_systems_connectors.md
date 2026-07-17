# ADR 0005 - Ops Systems Connectors (MaintainX, EZRentOut, Monday.com)

## Status
Accepted

## Context
Sable operates maintenance (MaintainX, two environments), equipment rental (EZRentOut), and project boards (Monday.com) in parallel. The original roadmap prioritized Microsoft 365 communications. Leadership needs a unified view of open work and AI insights across these ops systems, with future limited accounting reads from SAP or Syteline.

## Decision
Treat ops platforms as first-class connectors under the existing connector-first model:

- Two MaintainX environments are two configured instances (separate credentials), never silently merged.
- Prefer API-key / token auth for the pilot; encrypt at rest the same way as Graph tokens.
- Normalize inbound work into Palantir tasks / external work projections for insights.
- Keep a future `IAccountingConnector` seam for SAP or Syteline without choosing the ERP yet.
- Document this as a parallel **ops intelligence** track (`docs/13_ops_systems_integrations.md`), not a replacement of Phases 1–4.

## Consequences
- Faster value for field/ops users while M365 corporate approval continues.
- More secret and rate-limit surface area to manage.
- Cross-system linking will be imperfect until real business keys are mapped.
- Roadmap Phase 7 “additional connectors” is partially pulled forward.
