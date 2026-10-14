#!/usr/bin/env bash
# Monthly ingest trigger. Citi Bike publishes a month a few days after month end, so run on the 5th.
# The backend decides what is new (files modified after the last successful run); no date is passed.
# Afterwards the weather is brought up to date (POST /api/weather/runs, synchronous, a few seconds) and the
# holiday calendar is reloaded (POST /api/calendar/runs, synchronous, about a second; the default years move
# forward on 1 January, so a monthly reload keeps the years ahead covered).
#
# Install on the host (root crontab, the .env holds the token):
#   5 3 5 * * /opt/cloud-ai-avenue-5/citibike/deploy/cron/citibike-ingest.sh >> /var/log/citibike-ingest.log 2>&1
#
# Manual trigger for the demo, with an explicit date:
#   ./citibike-ingest.sh 2026-09-01
set -euo pipefail

ENV_FILE="$(cd "$(dirname "$0")/.." && pwd)/.env"
set -a; . "$ENV_FILE"; set +a

SINCE="${1:-}"
if [[ -n "$SINCE" ]]; then
  BODY="{\"trigger\":\"manual\",\"since\":\"$SINCE\"}"
else
  BODY='{"trigger":"cron"}'
fi

echo "$(date -u +%FT%TZ) starting ingest run (${SINCE:-since last successful run})"
curl -fsS -X POST "http://127.0.0.1:${BACKEND_PORT:-8081}/api/ingest/runs" \
  -H "Content-Type: application/json" \
  -H "X-Admin-Token: ${INGEST_ADMIN_TOKEN}" \
  -d "$BODY"
echo

# The weather load does not depend on the trip ingest (that one keeps running in the background).
WEATHER_TRIGGER="cron"
[[ -n "$SINCE" ]] && WEATHER_TRIGGER="manual"
echo "$(date -u +%FT%TZ) loading weather (since the last stored day)"
curl -fsS -X POST "http://127.0.0.1:${BACKEND_PORT:-8081}/api/weather/runs" \
  -H "Content-Type: application/json" \
  -H "X-Admin-Token: ${INGEST_ADMIN_TOKEN}" \
  -d "{\"trigger\":\"$WEATHER_TRIGGER\"}"
echo

# Same trigger as the weather load: cron, or manual when a date was passed.
echo "$(date -u +%FT%TZ) loading the holiday calendar (default years)"
curl -fsS -X POST "http://127.0.0.1:${BACKEND_PORT:-8081}/api/calendar/runs" \
  -H "Content-Type: application/json" \
  -H "X-Admin-Token: ${INGEST_ADMIN_TOKEN}" \
  -d "{\"trigger\":\"$WEATHER_TRIGGER\"}"
echo
