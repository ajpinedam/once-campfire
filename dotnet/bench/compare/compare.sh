#!/bin/bash
# Rails and the .NET port side by side, on this machine, following bench/compare_http.rb:
# each app in its own container on APP_CPUS, the load generator on CLIENT_CPUS, 16 keep-alive
# clients, uncompressed responses read in full, every response 200, a fresh copy of one seed per
# run, configurations alternated across rounds. See README.md.
#
#   dotnet/bench/compare/compare.sh                 # build what's missing, seed, run, summarize
#   ROUNDS=4 DURATION=10 CLIENT=ruby ./compare.sh   # the repo's Ruby client instead
#   CONFIGS="dotnet rails-4" REBUILD=1 ./compare.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
DOTNET_DIR="$(cd "$HERE/../.." && pwd)"
REPO="$(cd "$DOTNET_DIR/.." && pwd)"
WORK="$HERE/.work"

ROUNDS="${ROUNDS:-2}"
DURATION="${DURATION:-10}"
CONCURRENCY="${CONCURRENCY:-16}"
CONFIGS="${CONFIGS:-dotnet rails-1 rails-4}"
CLIENT="${CLIENT:-fast}"            # fast: bench/Campfire.Bench (compiled); ruby: the repo's bench/http_client.rb
APP_CPUS="${APP_CPUS:-8-11}"
CLIENT_CPUS="${CLIENT_CPUS:-12-15}"
RAILS_IMAGE="${RAILS_IMAGE:-campfire-rails:bench}"
DOTNET_IMAGE="${DOTNET_IMAGE:-campfire-dotnet:bench}"
PORT="${PORT:-3100}"
KEY=isolated-benchmark-fixture-key   # the public fixture key bench/compare_http.rb uses
NETWORK=cf-compare
REDIS=cf-compare-redis
APP=cf-compare-app

log() { printf '\033[1m%s\033[0m\n' "$*" >&2; }

build_images() {
  if [ -n "${REBUILD:-}" ] || ! docker image inspect "$RAILS_IMAGE" >/dev/null 2>&1; then
    log "Building $RAILS_IMAGE from the committed Rails app"
    mkdir -p "$WORK"
    git -C "$REPO" archive --format=tar HEAD > "$WORK/rails-context.tar"
    # COPY --chmod needs BuildKit; this keeps the image buildable with the classic builder too.
    sed 's|^COPY --chmod=755 hooks /hooks|COPY --chown=1000:1000 hooks /hooks\nRUN chmod 755 /hooks/*|' "$REPO/Dockerfile" > "$WORK/Dockerfile.bench"
    tar --append -f "$WORK/rails-context.tar" -C "$WORK" Dockerfile.bench
    docker build -q -t "$RAILS_IMAGE" -f Dockerfile.bench - < "$WORK/rails-context.tar" >/dev/null
  fi
  if [ -n "${REBUILD:-}" ] || ! docker image inspect "$DOTNET_IMAGE" >/dev/null 2>&1; then
    log "Building $DOTNET_IMAGE from the working tree"
    docker build -q -t "$DOTNET_IMAGE" "$DOTNET_DIR" >/dev/null
  fi
}

build_seed() {
  [ -f "$WORK/seed/labels.json" ] && [ -z "${RESEED:-}" ] && return 0
  log "Seeding with Rails (test-fixture names plus a busy room)"
  rm -rf "$WORK/seed" "$WORK/seeding" && mkdir -p "$WORK/seed" "$WORK/seeding/storage"
  cp "$HERE/seed.rb" "$WORK/seed/seed.rb"
  docker run --rm --entrypoint "" -v "$WORK/seeding/storage:/rails/storage" -v "$WORK/seed:/seed" \
    -e RAILS_ENV=production -e SECRET_KEY_BASE="$KEY" -e DISABLE_SSL=true -e SKIP_TELEMETRY=true -e RAILS_LOG_LEVEL=warn \
    "$RAILS_IMAGE" bash -c "bin/rails db:prepare >/dev/null && bin/rails runner /seed/seed.rb" >&2
  rm "$WORK/seed/seed.rb"
  mv "$WORK/seeding/storage/db" "$WORK/seed/db"
  mv "$WORK/seeding/storage/files" "$WORK/seed/storage"
  rm -rf "$WORK/seeding"
}

cleanup() { docker rm -f "$APP" "$REDIS" >/dev/null 2>&1 || true; docker network rm "$NETWORK" >/dev/null 2>&1 || true; }

start_app() {
  local config="$1" storage="$WORK/run/storage"
  rm -rf "$WORK/run" && mkdir -p "$storage"
  cp -r "$WORK/seed/db" "$storage/db" && cp -r "$WORK/seed/storage" "$storage/files"

  local common=(-d --name "$APP" --entrypoint "" --network "$NETWORK" --cpuset-cpus "$APP_CPUS"
                -p "127.0.0.1:$PORT:3000" -v "$storage:/rails/storage")
  case "$config" in
    rails-*)
      docker exec "$REDIS" redis-cli FLUSHALL >/dev/null
      docker run "${common[@]}" \
        -e RAILS_ENV=production -e SECRET_KEY_BASE="$KEY" -e DISABLE_SSL=true -e SKIP_TELEMETRY=true \
        -e RAILS_LOG_LEVEL=fatal -e WEB_CONCURRENCY="${config#rails-}" -e JOB_CONCURRENCY=1 -e RAILS_MAX_THREADS=5 \
        -e REDIS_URL="redis://$REDIS:6379/0" \
        "$RAILS_IMAGE" bundle exec puma -C config/puma.rb >/dev/null ;;
    dotnet)
      docker run "${common[@]}" \
        -e ASPNETCORE_ENVIRONMENT=Production -e ASPNETCORE_URLS=http://0.0.0.0:3000 -e DISABLE_SSL=1 \
        -e SECRET_KEY_BASE="$KEY" -e STORAGE_PATH=/rails/storage -e Logging__LogLevel__Default=Error \
        "$DOTNET_IMAGE" dotnet Campfire.Web.dll >/dev/null ;;
    *) echo "unknown configuration: $config" >&2; return 1 ;;
  esac

  for _ in $(seq 1 450); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/up")" = "200" ] && return 0
    sleep 0.1
  done
  docker logs "$APP" > "$WORK/results/$config-boot-failure.log" 2>&1
  echo "$config did not become ready (see .work/results/$config-boot-failure.log)" >&2
  return 1
}

measure() {
  local config="$1" output="$2" labels="$WORK/seed/labels.json"
  if [ "$CLIENT" = ruby ]; then
    docker run --rm --network host --cpuset-cpus "$CLIENT_CPUS" -v "$HERE:/compare" -v "$REPO/bench:/bench:ro" \
      -v "$WORK:/work" ruby:3.4-slim ruby /compare/driver.rb "http://127.0.0.1:$PORT" /work/seed/labels.json \
      "$DURATION" "$CONCURRENCY" "/work/results/$(basename "$output")"
  else
    local room before
    room=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['rooms.watercooler'])" "$labels")
    before=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['messages.busy_060'])" "$labels")
    taskset -c "$CLIENT_CPUS" dotnet "$DOTNET_DIR/bench/Campfire.Bench/bin/Release/net10.0/Campfire.Bench.dll" \
      --url "http://127.0.0.1:$PORT" --email david@37signals.com --password secret123456 \
      --room "$room" --before "$before" --query coffee --seed 0 \
      --concurrency "$CONCURRENCY" --duration "$DURATION" --json "$output" | tail -n +3
  fi
}

build_images
build_seed
[ "$CLIENT" = ruby ] || dotnet build -c Release "$DOTNET_DIR/bench/Campfire.Bench" >/dev/null

trap cleanup EXIT
cleanup
docker network create "$NETWORK" >/dev/null
docker run -d --name "$REDIS" --network "$NETWORK" redis:7-alpine >/dev/null
rm -rf "$WORK/results" && mkdir -p "$WORK/results"

read -r -a configs <<< "$CONFIGS"
for round in $(seq 1 "$ROUNDS"); do
  order=("${configs[@]}")
  if (( round % 2 == 0 )); then  # alternate the order, as compare_http.rb does
    order=(); for (( i=${#configs[@]}-1; i>=0; i-- )); do order+=("${configs[$i]}"); done
  fi
  for config in "${order[@]}"; do
    start_app "$config"
    log "round $round/$ROUNDS: $config ($CLIENT client)"
    measure "$config" "$WORK/results/$config-$round.json"
    docker rm -f "$APP" >/dev/null
  done
done

python3 "$HERE/summarize.py" "$WORK/results" "$CLIENT"
