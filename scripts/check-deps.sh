#!/usr/bin/env bash
# Project Palantir — dependency checker for switching machines.
# Usage:
#   ./scripts/check-deps.sh           # report only
#   ./scripts/check-deps.sh --install # attempt installs where safe
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
INSTALL=0
if [[ "${1:-}" == "--install" ]]; then
  INSTALL=1
fi

PASS=0
FAIL=0
WARN=0

green() { printf '\033[32m%s\033[0m\n' "$*"; }
yellow() { printf '\033[33m%s\033[0m\n' "$*"; }
red() { printf '\033[31m%s\033[0m\n' "$*"; }

ok() { green "  ✓ $*"; PASS=$((PASS + 1)); }
warn() { yellow "  ! $*"; WARN=$((WARN + 1)); }
bad() { red "  ✗ $*"; FAIL=$((FAIL + 1)); }

have() { command -v "$1" >/dev/null 2>&1; }

ensure_path() {
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:/opt/homebrew/bin:/usr/local/bin:$PATH"
  if [[ -s "$HOME/.nvm/nvm.sh" ]]; then
    # shellcheck disable=SC1090
    . "$HOME/.nvm/nvm.sh" >/dev/null 2>&1 || true
  fi
}

version_ge() {
  # usage: version_ge "$actual" "$needed"  (simple dotted numeric compare)
  python3 - "$1" "$2" <<'PY'
import sys
def parts(s):
    out = []
    for p in s.lstrip("vV").split("."):
        try: out.append(int(p))
        except: out.append(0)
    return out
a, b = parts(sys.argv[1]), parts(sys.argv[2])
n = max(len(a), len(b))
a += [0] * (n - len(a))
b += [0] * (n - len(b))
sys.exit(0 if a >= b else 1)
PY
}

echo "Project Palantir — dependency check"
echo "Root: $ROOT"
echo

ensure_path

echo "Toolchain"
# --- .NET 8 ---
if have dotnet; then
  SDK="$(dotnet --list-sdks 2>/dev/null | awk '{print $1}' | grep '^8\.' | tail -1 || true)"
  if [[ -n "$SDK" ]]; then
    ok ".NET SDK $SDK"
  else
    bad ".NET SDK 8.x not found (have: $(dotnet --list-sdks 2>/dev/null | tr '\n' ' '))"
    if [[ $INSTALL -eq 1 ]] && have brew; then
      yellow "    Installing .NET SDK via Homebrew…"
      brew install --cask dotnet-sdk || true
    fi
  fi
else
  bad "dotnet not on PATH (install .NET 8 SDK)"
  if [[ $INSTALL -eq 1 ]] && have brew; then
    yellow "    Installing .NET SDK via Homebrew…"
    brew install --cask dotnet-sdk || true
  fi
fi

# --- Node ---
if have node; then
  NODE_V="$(node -v 2>/dev/null || echo 0)"
  if version_ge "$NODE_V" "18.0.0"; then
    ok "Node $NODE_V"
  else
    bad "Node $NODE_V — need 18+"
  fi
else
  bad "node not on PATH (need Node 18+)"
  if [[ $INSTALL -eq 1 ]] && have brew; then
    yellow "    Installing node@18 via Homebrew…"
    brew install node@18 || brew install node || true
  fi
fi

# --- npm ---
if have npm; then
  ok "npm $(npm -v 2>/dev/null)"
else
  bad "npm not on PATH"
fi

# --- Optional: Ollama ---
if have ollama; then
  if curl -sf --max-time 2 http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
    ok "Ollama running (localhost:11434)"
  else
    warn "Ollama installed but not responding — try: brew services start ollama"
  fi
else
  warn "Ollama not installed (optional local AI). brew install ollama"
  if [[ $INSTALL -eq 1 ]] && have brew; then
    yellow "    Installing Ollama…"
    brew install ollama || true
    brew services start ollama || true
  fi
fi

# --- Optional: Azure CLI ---
if have az; then
  ok "Azure CLI $(az version --query '\"azure-cli\"' -o tsv 2>/dev/null || echo present)"
else
  warn "Azure CLI not installed (optional). brew install azure-cli"
fi

echo
echo "Repo packages"

# --- Backend restore ---
if have dotnet; then
  if (cd "$ROOT/backend" && dotnet restore -v q >/dev/null 2>&1); then
    ok "dotnet restore (backend)"
  else
    bad "dotnet restore failed — run: cd backend && dotnet restore"
  fi
else
  warn "Skipping dotnet restore (no SDK)"
fi

# --- Web node_modules ---
if [[ -d "$ROOT/web/node_modules" ]]; then
  ok "web/node_modules present"
else
  bad "web/node_modules missing"
  if [[ $INSTALL -eq 1 ]] && have npm; then
    yellow "    Running npm install…"
    (cd "$ROOT/web" && npm install) || true
    if [[ -d "$ROOT/web/node_modules" ]]; then
      ok "web/node_modules installed"
      FAIL=$((FAIL - 1))
    fi
  else
    yellow "    Fix: cd web && npm install"
  fi
fi

echo
echo "Secrets / config hints"
if have dotnet && [[ -d "$ROOT/backend/Palantir.Api" ]]; then
  if (cd "$ROOT/backend/Palantir.Api" && dotnet user-secrets list >/tmp/palantir-secrets.txt 2>/dev/null); then
    COUNT="$(wc -l < /tmp/palantir-secrets.txt | tr -d ' ')"
    if [[ "$COUNT" -gt 0 ]]; then
      ok "dotnet user-secrets has $COUNT entries"
    else
      warn "dotnet user-secrets empty — blob/AI/connectors may need keys from your other machine"
    fi
  else
    warn "Could not list user-secrets (init with: cd backend/Palantir.Api && dotnet user-secrets init)"
  fi
else
  warn "Skipping user-secrets check"
fi

echo
echo "Summary: $PASS ok, $WARN warn, $FAIL fail"
if [[ $FAIL -gt 0 ]]; then
  red "Missing required deps — see DEPENDENCIES.md"
  exit 1
fi
green "Ready enough to run locally. Next:"
echo "  cd backend && ASPNETCORE_ENVIRONMENT=Development dotnet run --project Palantir.Api --urls http://localhost:5251"
echo "  cd web && npm run dev"
exit 0
