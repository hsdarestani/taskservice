#!/usr/bin/env bash
# Exercises the deployment files inside a disposable GitHub CI runner.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
scratch="$(mktemp -d)"
umask 077
cleanup() {
  status=$?
  if (( status != 0 )); then
    docker compose -p taskservice-ci ps --all || true
    docker compose -p taskservice-ci logs --tail=35 db api 2>/dev/null |
      awk 'tolower($0) !~ /password|secret|token/' >&2 || true
  fi
  docker compose -p taskservice-ci down --volumes --remove-orphans >/dev/null 2>&1 || true
  rm -rf -- "$scratch"
}
trap cleanup EXIT
cp "$root/v2/ops/compose.yaml" "$scratch/"
install -m 0644 "$root/v2/ops/clamd.conf" "$scratch/clamd.conf"
install -D -m 0555 "$root/v2/ops/postgres/initialize.sh" "$scratch/postgres/initialize.sh"
cd "$scratch"
mkdir -p secrets
export TASKSERVICE_IMAGE=taskservice:ci
export TASKSERVICE_POSTGRES_IMAGE=postgres:18
export TASKSERVICE_CLAMAV_IMAGE=clamav/clamav:stable
export TASKSERVICE_HTTP_PORT=18080
for kind in POSTGRES_PASSWORD TASKSERVICE_MIGRATOR_PASSWORD TASKSERVICE_API_PASSWORD TASKSERVICE_WORKER_PASSWORD TASKSERVICE_PLATFORM_PASSWORD; do
  printf '%s=%s\n' "$kind" "$(openssl rand -hex 24)" >> secrets/db.env
done
set -a
source secrets/db.env
set +a
echo "ConnectionStrings__Migration=Host=db;Database=taskservice;Username=taskservice_migrator;Password=$TASKSERVICE_MIGRATOR_PASSWORD" > secrets/migrate.env
{
  echo "ConnectionStrings__Runtime=Host=db;Database=taskservice;Username=taskservice_api;Password=$TASKSERVICE_API_PASSWORD"
  echo "ConnectionStrings__Platform=Host=db;Database=taskservice;Username=taskservice_platform;Password=$TASKSERVICE_PLATFORM_PASSWORD"
  printf '%s\n' 'AllowedHosts=localhost' 'TASKSERVICE_HEALTH_HOST=localhost' 'Routing__DefaultTenantSlug=release-fixture'
} > secrets/api.env
touch secrets/worker.env
compose=(docker compose -p taskservice-ci)
"${compose[@]}" config --quiet
"${compose[@]}" up -d --wait --wait-timeout 60 db
"${compose[@]}" run --rm migrate
"${compose[@]}" exec -T db psql -X -v ON_ERROR_STOP=1 -U postgres -d taskservice < "$root/v2/TaskService/Database/grants.sql"
"${compose[@]}" exec -T db psql -X -v ON_ERROR_STOP=1 -U postgres -d taskservice <<'SQL'
SET ROLE taskservice_platform;
INSERT INTO ts.tenants(id,slug,name,accent,active)
VALUES ('14ea086c-c616-4837-a754-aa1706bdf3df','release-fixture','Service QA','#1064e8',true);
SQL
"${compose[@]}" up -d api
live_url=http://localhost:18080/health/live
for attempt in {1..30}; do
  if curl -fs --max-time 2 "$live_url" >/dev/null; then
    break
  fi
  sleep 1
done
curl -fsS --max-time 5 "$live_url" >/dev/null
test "$(curl -sS -o /dev/null -w '%{http_code}' http://localhost:18080/health/ready)" = 503
test "$(curl -sS -o /dev/null -w '%{redirect_url}' http://localhost:18080/)" = http://localhost:18080/unternehmen/release-fixture
curl -fsS http://localhost:18080/api/v1/tenants/release-fixture | grep -F 'Service QA' >/dev/null
test "$(curl -sS -o /dev/null -w '%{http_code}' http://localhost:18080/api/v1/session)" = 200
echo 'Isolated container migration and root routing passed. Production readiness correctly rejected missing dependencies.'
