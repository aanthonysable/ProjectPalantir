# Resume TODO (next session)

_Last updated: 2026-07-20 — put new items at the top._

## Now

1. **Fix Ask “Save to knowledge” button** — Chat capture / add-to-knowledge from Ask replies is broken or unreliable; debug end-to-end (UI → approval/capture → knowledge store).
2. **Mobile web formatting (continue)** — Keep polishing responsive layout for tablet/phone browsers (nav, Ask, compose, Open work, Inbox, Admin) beyond the first pass.
3. **Ask × knowledge** — Confirm Ask is retrieving from indexed knowledge (SQL chunks backed by blob-stored docs). It currently does not seem to be using knowledge in answers; debug `OverviewService` → `BuildKnowledgeBlockAsync` / `IKnowledgeService.SearchAsync` and verify Indexed docs + chunk search are wired into the chat prompt path end-to-end.
4. **Finish knowledge uploads** — Upload the remaining knowledge documents (large PDFs / PLC packs) now that 4 GB limits, PDF text extraction, upload progress, and background indexing are in place.

## In progress / recently landed

- **Ask file upload** — Attach PDF/text in Ask for review; say “add to knowledge” to promote into org knowledge (`AskAttachments` + `/ask/attachments`).
- **Shared DB ops snapshots** — Background refresh (~5 min) of MaintainX / Monday / EZRentOut into `OpsSnapshots`; Ask reads shared org snapshot by default (faster, multi-user). Force live with `refreshFacts: true`.

## Parking lot / later

- **WhatsApp thread integration** — Ingest / sync WhatsApp conversation threads into Palantir (connector + inbox/thread model); provider and compliance TBD.
- **Ask: parallel chats (low priority)** — Today the web UI serializes Ask to one in-flight question (`overviewChatBusy`). Future opportunity: allow multiple questions at once across different Ask chats/sessions while each chat stays independent.
- **Ask: opt-in web lookup (low priority)** — When knowledge + fact sheet have no good match (e.g. equipment troubleshooting with nothing uploaded), Ask may offer “Want me to find resources on the web?” and only then search/fetch external sources, clearly labeled vs internal knowledge. Requires provider choice + security review; never silent browsing.
- Scanned/image-only PDF OCR (text extraction only works for text-based PDFs today)
- Laptop machine check: run `./scripts/check-deps.sh` (see `DEPENDENCIES.md`)
