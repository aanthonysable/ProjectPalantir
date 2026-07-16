# Project Palantir — local development

## Prerequisites

- .NET 8 SDK (`~/.dotnet` install is fine)
- Node.js 18+

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
```

## Backend

```bash
cd backend
dotnet run --project Palantir.Api
```

- API: http://localhost:5251
- Swagger: http://localhost:5251/swagger
- Health: http://localhost:5251/health
- SignalR hub: `/hubs/notifications`

Local Development uses **SQLite** (`palantir.dev.db`) by default. Switch to Azure SQL later with:

```json
"Database": { "Provider": "SqlServer" },
"ConnectionStrings": { "Palantir": "<azure-sql-connection-string>" }
```

Pilot identity is a header placeholder:

- `X-Palantir-User-Id: bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb`
- `X-Palantir-Organization-Id: aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`

## Web

```bash
cd web
npm install
npm run dev
```

Open http://localhost:5173 — Vite proxies `/api` and `/hubs` to the API.

## Tests

```bash
cd backend
dotnet test
```
