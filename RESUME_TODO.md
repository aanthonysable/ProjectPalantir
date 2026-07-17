# Resume TODO (next session)

_Last updated: 2026-07-17 — put new items at the top._

## Now

1. **Ask × knowledge** — Confirm Ask is retrieving from indexed knowledge (SQL chunks backed by blob-stored docs). It currently does not seem to be using knowledge in answers; debug `OverviewService` → `BuildKnowledgeBlockAsync` / `IKnowledgeService.SearchAsync` and verify Indexed docs + chunk search are wired into the chat prompt path end-to-end.
2. **Finish knowledge uploads** — Upload the remaining knowledge documents (large PDFs / PLC packs) now that 4 GB limits, PDF text extraction, upload progress, and background indexing are in place.

## Parking lot / later

- Scanned/image-only PDF OCR (text extraction only works for text-based PDFs today)
- Laptop machine check: run `./scripts/check-deps.sh` (see `DEPENDENCIES.md`)
