# Gemini AI provider

Google Gemini via the **OpenAI-compatible** chat completions API.

## Free / personal testing

1. Create an API key in [Google AI Studio](https://aistudio.google.com/apikey).
2. Store it in user-secrets (never commit):

```bash
cd backend/Palantir.Api
dotnet user-secrets set "Ai:Providers:gemini:ApiKey" "YOUR_KEY"
# optional alias:
# export GEMINI_API_KEY=YOUR_KEY
```

3. Restart the API. Default routing sends Recap / Chat / Summarize / DraftReply to `gemini`.
4. If Gemini is missing a key, Palantir **falls back** to any other configured provider (usually local Ollama).

## Corporate later

Keep `Provider: Gemini` and swap secrets/endpoint when IT issues a corporate key or Vertex AI OpenAI-compatible gateway:

```bash
dotnet user-secrets set "Ai:Providers:gemini:ApiKey" "<corp-key>"
dotnet user-secrets set "Ai:Providers:gemini:Endpoint" "https://generativelanguage.googleapis.com/v1beta/openai"
dotnet user-secrets set "Ai:Providers:gemini:Model" "gemini-flash-lite-latest"
```

Model names and quotas are org-specific — confirm with your Google Workspace / Cloud admin. Free-tier projects often have **zero quota** on older Flash models (`gemini-2.0-flash`); prefer `gemini-flash-lite-latest` or `gemini-3.1-flash-lite-preview`. On 429/503, Palantir falls back to Ollama automatically.

## Defaults (appsettings)

| Task | Provider profile | Why |
|------|------------------|-----|
| Recap | gemini | Long ops fact sheets, stronger instruction following |
| Chat | gemini | Grounded Q&A over large sheets |
| Summarize | gemini | Email thread compression |
| DraftReply | gemini | Writing quality for customer-facing drafts |

Ollama remains available as `Ai:Providers:ollama` for offline / no-cloud work. Point any task at `"ollama"` in `Ai:Tasks` to mix providers.

## Auth

- `POST {Endpoint}/chat/completions`
- Header: `Authorization: Bearer <API_KEY>`
- Default Endpoint: `https://generativelanguage.googleapis.com/v1beta/openai`
- Suggested free-tier model: `gemini-flash-lite-latest` (or `gemini-3.1-flash-lite-preview`)
- If you see **429 quota**, that model’s free tier is exhausted or capped at 0 — switch model or wait; Palantir falls back to Ollama when configured.
