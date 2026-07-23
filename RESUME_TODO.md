# Resume TODO (next session)

_Last updated: 2026-07-23 — put new items at the top._

## Now

1. **Connectors: InControl** — Scaffold read-only connector (health + open work / assets as applicable). Document API auth, environments, and config keys under `connectors/InControl/`. Wire Admin health when credentials exist.
2. **Connectors: Geoforce** — Scaffold GPS / asset-tracking connector (locations, assets, alerts). Document auth + capability map; surface in Open work / Ask fact sheet when useful.
3. **Connectors: Geotab** — Scaffold telematics connector (vehicles/assets, trips, exceptions). Same connector-first pattern as MaintainX/EZRentOut; Admin health + optional Ask context.
4. **Connectors: Ignition (evaluate)** — Spike whether Ignition (SCADA/IIoT) belongs as a Palantir connector vs. knowledge/docs only. If yes: define read capabilities (tags, alarms, historians) and security constraints before scaffolding.
5. **Mobile web formatting (continue)** — Mobile shell redesigned 2026-07-20: hamburger drawer nav, compact topbar, Inbox list-**or**-thread (not both), New conversation behind a toggle. Spot-check remaining tabs (Ask/Open work/Admin) on phone.
6. **Finish knowledge uploads** — Upload the remaining knowledge documents (large PDFs / PLC packs) now that 4 GB limits, PDF text extraction, upload progress, and background indexing are in place.

## In progress / recently landed

- **UI polish (2026-07-23)** — Theme bokeh motes, page-swap fade, orb brand/favicon, transparent header + body mask fade, Ask scroll fix, AI rich-text rendering, MaintainX `00-Parent Asset*` placeholders ignored.
- **Customers CRM 360 (2026-07-22)** — Activity folders, job-first orders + assets, company web research overviews, reading-pane loading state, activity persistence/warm.
- **WhatsApp bridge (pilot)** — WAHA on Mac reads internal groups → Inbox `Channel=WhatsApp` + Admin gaps vs MaintainX/Monday/EZRentOut. Deduped webhooks (`message.any` only). Docs: `connectors/WhatsApp/README.md`. **Refine later** (see parking lot).
- **Knowledge as own nav tab** — Browse/upload/preview moved out of Admin.
- **Knowledge browse library** — Docs auto-classified into collections from path + content (Engine Harness, VFDs, Flow Meters, …) with folder paths; `GET /knowledge/library`.
- **Knowledge duplicate scan** — SHA-256 content hash; periodic scan marks Duplicate uploads.
- **Ask background extract** — Attach uploads return immediately; extract runs in background.
- **Ask knowledge source preview** — Preview modal with Download from the previewer.
- **Ask “Save to knowledge”** — In-app title modal (no browser `prompt`); capture queues to Approvals then knowledge (user confirmed it looks saved).
- **Ask file upload** — Attach PDF/text in Ask for review; say “add to knowledge” to promote into org knowledge (`AskAttachments` + `/ask/attachments`).
- **Shared DB ops snapshots** — Background refresh (~5 min) of MaintainX / Monday / EZRentOut into `OpsSnapshots`; Ask reads shared org snapshot by default (faster, multi-user). Force live with `refreshFacts: true`.

## Parking lot / later

- **WhatsApp connector refine** — Harden ingest (media, group names, session reconnect UX), richer ops matching, Open work / Ask fact-sheet surfacing, LaunchAgent so API/WAHA don’t depend on Cursor terminals, unique DB index on provider message id.
- **Private vs shared knowledge** — Visibility on docs: shared = company-internal (default; Ask retrieves); private = uploader-only personal notes that don’t pollute org Ask answers. Needs owner field, Ask filter, and Knowledge-tab UI. Defer until browse library is stable.
- **Ask: parallel chats (low priority)** — Today the web UI serializes Ask to one in-flight question (`overviewChatBusy`). Future opportunity: allow multiple questions at once across different Ask chats/sessions while each chat stays independent.
- **Ask: opt-in web lookup (low priority)** — When knowledge + fact sheet have no good match (e.g. equipment troubleshooting with nothing uploaded), Ask may offer “Want me to find resources on the web?” and only then search/fetch external sources, clearly labeled vs internal knowledge. Requires provider choice + security review; never silent browsing.
- Scanned/image-only PDF OCR (text extraction only works for text-based PDFs today)
- Laptop machine check: run `./scripts/check-deps.sh` (see `DEPENDENCIES.md`)
