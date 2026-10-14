#!/usr/bin/env bash
# Rolls the app and Mushroom inference services to the same image tag. Postgres is left untouched.
set -euo pipefail

TAG="${1:?usage: rollout.sh <image tag>}"
STACK="${STACK_DIR:-/opt/cloud-ai-avenue-5/citibike/deploy}"
KEEP=3

cd "$STACK"
docker image inspect "citibike-ingest:$TAG" "mushroom-inference:$TAG" >/dev/null

PREVIOUS=$(sed -nE 's/^IMAGE_TAG=([^[:space:]#]*).*/\1/p' .env)
if [[ -z "$PREVIOUS" ]]; then
  echo "IMAGE_TAG must name the currently deployed image tag in .env" >&2
  exit 1
fi
PREVIOUS_MUSHROOM=$(sed -nE 's/^MUSHROOM_IMAGE_TAG=([^[:space:]#]*).*/\1/p' .env)
PREVIOUS_MUSHROOM="${PREVIOUS_MUSHROOM:-$PREVIOUS}"
if docker image inspect "mushroom-inference:$PREVIOUS_MUSHROOM" >/dev/null 2>&1; then
  HAS_PREVIOUS_MUSHROOM=true
else
  HAS_PREVIOUS_MUSHROOM=false
fi
PORT=$(sed -nE 's/^BACKEND_PORT=([^[:space:]#]*).*/\1/p' .env)
PORT="${PORT:-8081}"
cp -p .env ".env.bak-$(date +%Y%m%d-%H%M%S)"
grep -q '^IMAGE_TAG=' .env || echo "IMAGE_TAG=" >> .env
grep -q '^MUSHROOM_IMAGE_TAG=' .env || echo "MUSHROOM_IMAGE_TAG=" >> .env

set_tag() {
  sed -i -E "s/^(IMAGE_TAG=)[^[:space:]#]*/\1$1/" .env
  sed -i -E "s/^(MUSHROOM_IMAGE_TAG=)[^[:space:]#]*/\1$1/" .env
}

restore_previous() {
  if [[ "$HAS_PREVIOUS_MUSHROOM" == true ]]; then
    set_tag "$PREVIOUS"
    docker compose up -d --no-build mushroom-model backend
  else
    echo "No earlier Mushroom image exists; restoring the previous app without starting its new model dependency" >&2
    sed -i -E "s/^(IMAGE_TAG=)[^[:space:]#]*/\1$PREVIOUS/" .env
    docker compose up -d --no-build --no-deps backend
  fi
}

healthy() {
  for _ in $(seq 1 30); do
    curl -fsS "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && return 0
    sleep 3
  done
  return 1
}

echo "rolling app and Mushroom inference from $PREVIOUS to $TAG"
set_tag "$TAG"
if ! docker compose up -d --no-build mushroom-model backend; then
  echo "new services failed to start; restoring $PREVIOUS" >&2
  docker compose logs --tail 50 mushroom-model backend >&2 || true
  restore_previous
  exit 1
fi
if healthy; then
  echo "app and Mushroom inference healthy on $TAG"
else
  echo "app not healthy on $TAG after 90 s; back to $PREVIOUS" >&2
  docker compose logs --tail 50 mushroom-model backend >&2 || true
  restore_previous
  healthy && echo "backend healthy again on $PREVIOUS" >&2
  exit 1
fi

for image in citibike-ingest mushroom-inference; do
  docker image ls "$image" --format '{{.CreatedAt}}\t{{.Tag}}' | sort -r | tail -n +$((KEEP + 1)) | cut -f2 \
    | grep -vx -e "$TAG" -e "$PREVIOUS" | xargs -r -I{} docker image rm "$image:{}" || true
done
