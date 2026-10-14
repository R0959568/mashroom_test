#!/usr/bin/env bash
# Runs the backend (dotnet) and the frontend (Vite, hot reload) on the laptop, against the project database on the
# server over NetBird and the forecast API of notebook 11. Usage, from citibike/:  ./debug.sh   (Ctrl-C stops both)
#
#   backend   http://127.0.0.1:8080   Swagger on /swagger, the built SPA is NOT served here (see the frontend)
#   frontend  http://localhost:5173   the SPA with hot reload, /api proxied to the backend (vite.config.ts)
#
# Credentials come from the repo root .env, the one the notebooks use (see .env.example): CITIBIKE_DB_USER and
# CITIBIKE_DB_PASSWORD for Postgres, FORECAST_API_URL and FORECAST_API_KEY for the models. That login is READ-ONLY by
# design: reads and the forecast page work, starting an ingest, weather or calendar run does not (the backend logs a
# warning at startup and answers those with an error). Nothing of production can be changed from a debug session.
# To debug the ingest side as well, set DEBUG_PG_CONNECTION to a full Npgsql connection string of a login that may
# write (for example a local Postgres from deploy/README.md "Full stack locally"); it replaces the one built here.
#
# The database is reached over NetBird (netbird status must say Connected; "NeedsLogin" means: netbird up). Without
# NetBird an ssh tunnel to the host does the same:
#     ssh -N -L 15432:100.88.194.89:5432 root@<host> &      then      CITIBIKE_DB_HOST=127.0.0.1 CITIBIKE_DB_PORT=15432 ./debug.sh
# Values are never printed. Logs go to citibike/.debug/backend.log and frontend.log and are followed on this terminal.
# Once both answer, the SPA opens in a new tab of the default browser (DEBUG_NO_BROWSER=1 to skip).
set -euo pipefail

cd "$(dirname "$0")"
ROOT=$(cd .. && pwd)
ENV_FILE="$ROOT/.env"
LOG_DIR="$PWD/.debug"
DB_HOST="${CITIBIKE_DB_HOST:-100.88.194.89}"
DB_PORT="${CITIBIKE_DB_PORT:-5432}"
BACKEND_URL="http://127.0.0.1:8080"
FRONTEND_URL="http://localhost:5173"   # the dev server listens on localhost (::1 here), not on 127.0.0.1

say() { printf '\033[1;36m[debug]\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m[debug]\033[0m %s\n' "$*" >&2; exit 1; }
open_url() {  # a new tab in the default browser: Windows (Git Bash), macOS, Linux
  if [ -n "${WINDIR:-}" ] || command -v cmd.exe >/dev/null 2>&1; then (cd / && cmd.exe //c start "" "$1" >/dev/null 2>&1 || true)
  elif command -v open >/dev/null 2>&1; then open "$1" >/dev/null 2>&1 || true
  elif command -v xdg-open >/dev/null 2>&1; then xdg-open "$1" >/dev/null 2>&1 || true
  else say "open $1 in a browser"
  fi
}

# ---- the env file: KEY=value lines, comments and blank lines skipped, CR stripped; exported without echoing
[ -f "$ENV_FILE" ] || die "$ENV_FILE is missing; copy .env.example and fill it in"
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%$'\r'}"
  case "$line" in ''|'#'*) continue ;; esac
  [[ "$line" == *=* ]] || continue
  key="${line%%=*}"
  value="${line#*=}"
  [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
  export "$key=$value"
done < "$ENV_FILE"

for key in CITIBIKE_DB_USER CITIBIKE_DB_PASSWORD FORECAST_API_URL FORECAST_API_KEY; do
  [ -n "${!key:-}" ] || die "$key is empty or missing in $ENV_FILE"
done

# ---- what the backend reads (environment beats appsettings.Development.json)
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Postgres="${DEBUG_PG_CONNECTION:-Host=$DB_HOST;Port=$DB_PORT;Database=citibike;Username=$CITIBIKE_DB_USER;Password=$CITIBIKE_DB_PASSWORD}"
export Forecast__Url="$FORECAST_API_URL"
export Forecast__ApiKey="$FORECAST_API_KEY"

command -v dotnet >/dev/null || die ".NET SDK not found (dotnet)"
command -v npm >/dev/null || die "Node/npm not found"
# ---- the database must really answer before the backend starts: a TCP connect alone succeeds on the NetBird
# interface even when the client is not logged in (the connection is reset afterwards), so send Postgres an
# SSLRequest and expect its one-byte answer (S or N).
pg_answers() {
  local reply
  reply=$( (exec 3<>"/dev/tcp/$1/$2" && printf '\000\000\000\010\004\322\026\057' >&3 && IFS= read -r -t 4 -n 1 reply <&3 && printf '%s' "$reply") 2>/dev/null ) || return 1
  [ "$reply" = S ] || [ "$reply" = N ]
}
if [ -z "${DEBUG_PG_CONNECTION:-}" ]; then
  if [ "$DB_HOST" = "100.88.194.89" ] && command -v netbird >/dev/null 2>&1      && ! netbird status 2>/dev/null | grep -q -E '^Management: Connected'; then
    # a connected client prints "Management: Connected"; a logged-out one prints "Daemon status: NeedsLogin"
    die "NetBird is not connected on this laptop ($(netbird status 2>/dev/null | head -n 1)); run: netbird up (SSO in the browser), then try again. Or use the ssh tunnel, see the header."
  fi
  pg_answers "$DB_HOST" "$DB_PORT" || die "no Postgres answers on $DB_HOST:$DB_PORT (NetBird route down, or the tunnel not up); see the header of this script"
fi

# ---- the ports must be free: a backend or dev server that already answers is another session (this script in a
# second terminal, an IDE run); starting on top of it binds nothing and tests the wrong processes
if curl -s -o /dev/null --max-time 2 "$BACKEND_URL/healthz"; then
  die "something already answers on $BACKEND_URL (another debug session or an IDE run?); stop it first"
fi
if curl -s -o /dev/null --max-time 2 "$FRONTEND_URL/"; then
  die "something already answers on $FRONTEND_URL (another debug session?); stop it first"
fi

mkdir -p "$LOG_DIR"
: > "$LOG_DIR/backend.log"
: > "$LOG_DIR/frontend.log"

# ---- stop both on exit; on Windows (Git Bash) a plain kill leaves the child dotnet/node processes alive
PIDS=()
stop_all() {
  trap - EXIT INT TERM
  say "stopping"
  for pid in "${PIDS[@]:-}"; do
    [ -n "$pid" ] || continue
    if command -v taskkill >/dev/null 2>&1; then
      taskkill //PID "$pid" //T //F >/dev/null 2>&1 || true
    else
      kill -- -"$pid" 2>/dev/null || kill "$pid" 2>/dev/null || true
    fi
  done
}
trap stop_all EXIT INT TERM

# ---- backend
say "backend: dotnet run (log: .debug/backend.log)"
( cd backend && exec dotnet run --project src/CitibikeIngest.Api --no-launch-profile ) >> "$LOG_DIR/backend.log" 2>&1 &
PIDS+=("$!")
for _ in $(seq 1 90); do
  if curl -s -o /dev/null "$BACKEND_URL/healthz"; then break; fi
  if ! kill -0 "${PIDS[0]}" 2>/dev/null; then
    tail -n 30 "$LOG_DIR/backend.log"
    die "the backend stopped; see .debug/backend.log"
  fi
  sleep 1
done
curl -s -o /dev/null "$BACKEND_URL/healthz" || { tail -n 30 "$LOG_DIR/backend.log"; die "the backend did not answer /healthz within 90 s"; }
say "backend up: $BACKEND_URL (Swagger: $BACKEND_URL/swagger)"
if grep -q "Metadata schema not ensured" "$LOG_DIR/backend.log"; then
  say "read-only login: the forecast page works, ingest/weather/calendar runs are unavailable (expected, see the header of this script)"
fi

# ---- frontend
if [ ! -d frontend/node_modules ]; then
  say "frontend: npm install"
  ( cd frontend && npm install --no-audit --no-fund ) >> "$LOG_DIR/frontend.log" 2>&1
fi
say "frontend: vite dev server (log: .debug/frontend.log)"
( cd frontend && exec npm run dev ) >> "$LOG_DIR/frontend.log" 2>&1 &
PIDS+=("$!")
for _ in $(seq 1 60); do
  if curl -s -o /dev/null --max-time 2 "$FRONTEND_URL/"; then break; fi
  if ! kill -0 "${PIDS[1]}" 2>/dev/null; then
    tail -n 20 "$LOG_DIR/frontend.log"
    die "the dev server stopped; see .debug/frontend.log"
  fi
  sleep 1
done
curl -s -o /dev/null --max-time 2 "$FRONTEND_URL/" || { tail -n 20 "$LOG_DIR/frontend.log"; die "the dev server did not answer on $FRONTEND_URL within 60 s"; }
say "frontend up: $FRONTEND_URL  (Ctrl-C stops both)"
[ -n "${DEBUG_NO_BROWSER:-}" ] || open_url "$FRONTEND_URL"

# ---- follow both logs until Ctrl-C
tail -n +1 -f "$LOG_DIR/backend.log" "$LOG_DIR/frontend.log"
