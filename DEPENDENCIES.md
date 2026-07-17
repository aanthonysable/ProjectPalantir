# Project Palantir — local dependencies

Use this when switching machines (e.g. laptop). Run the checker first:

```bash
./scripts/check-deps.sh
```

Add `--install` to attempt installs where safe (Homebrew / npm / dotnet restore):

```bash
./scripts/check-deps.sh --install
```

## Required

| Tool | Version | Notes |
|------|---------|--------|
| **.NET SDK** | 8.x | `dotnet --list-sdks` should show `8.0.x`. Install: https://dotnet.microsoft.com/download/dotnet/8.0 or `brew install --cask dotnet-sdk` |
| **Node.js** | 18+ (LTS) | Pilot uses Node 18. `node -v` → `v18+`. Install via nvm or `brew install node@18` |
| **npm** | 9+ | Comes with Node |

## Optional but used in pilot

| Tool | Why |
|------|-----|
| **Ollama** | Local AI fallback (`llama3.2`). `brew install ollama && brew services start ollama && ollama pull llama3.2` |
| **Azure CLI** | User-secrets / blob / SQL ops. `brew install azure-cli` |
| **Git + gh** | Source control / PRs |

## Repo package restore

```bash
# Backend
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
cd backend && dotnet restore && dotnet build

# Web
cd web && npm install
```

## Secrets / config (not in git)

Secrets live in **.NET user-secrets** for `Palantir.Api`, not in committed files:

```bash
cd backend/Palantir.Api
dotnet user-secrets list
```

Typical keys (set on each machine if missing):

- `Azure:Storage:ConnectionString` — knowledge blob uploads
- `ConnectionStrings:Palantir` + `Database:Provider` — Azure SQL when not using local SQLite
- `Ai:Providers:Gemini:ApiKey` (and/or Ollama base URL)
- Connector keys: MaintainX / EZRentOut / Monday / Microsoft Graph client secret

Copy from your other machine with `dotnet user-secrets list` (or Key Vault) — never commit raw connection strings.

## Quick start after deps are green

```bash
# Terminal 1 — API
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
cd backend
ASPNETCORE_ENVIRONMENT=Development dotnet run --project Palantir.Api --urls http://localhost:5251

# Terminal 2 — Web
cd web && npm run dev
```

- Web: http://localhost:5173  
- API health: http://localhost:5251/health  
- Swagger: http://localhost:5251/swagger  

See also `backend/README.md` and root `README.md`.
