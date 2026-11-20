#!/usr/bin/env bash
# Validate migrations and logical recovery against the disposable CI PostgreSQL container.
set -euo pipefail
: "${TASKSERVICE_TEST_PG_CONTAINER:?Set the disposable PostgreSQL container ID.}"
: "${TASKSERVICE_TEST_ADMIN_DB:?Set the local test connection string.}"
if [[ "$TASKSERVICE_TEST_ADMIN_DB" != Host=127.0.0.1\;*Database=postgres ]]; then
  printf '%s\n' 'Use the disposable, loopback PostgreSQL test server.'
  exit 1
fi

container="$TASKSERVICE_TEST_PG_CONTAINER"
root="$(cd "$(dirname "$0")/../.." && pwd)"
suffix="$(date +%s)_$$"
source_db="ts_restore_source_$suffix"
target_db="ts_restore_target_$suffix"
dump="$(mktemp)"
trap 'rm -f "$dump"' EXIT

db() {
  docker exec -i "$container" psql -X -v ON_ERROR_STOP=1 -U postgres "$@"
}

db -d postgres <<SQL
CREATE DATABASE $source_db;
CREATE DATABASE $target_db;
SQL
ConnectionStrings__Migration="${TASKSERVICE_TEST_ADMIN_DB%Database=postgres}Database=$source_db" \
  dotnet "$root/v2/TaskService/bin/Release/net10.0/TaskService.dll" migrate
db -d "$source_db" < "$root/v2/TaskService/Database/grants.sql"
db -d "$source_db" <<'SQL'
INSERT INTO ts.tenants (id, slug, name, accent, active)
VALUES ('b04512dd-4e65-4875-acbf-a73306a39adc','restore-fixture','Werkstatt Restore','#0a6159',true),
       ('c84b1594-96e4-4d29-aff3-a804316608e0','other-fixture','Werkstatt Süd','#37313a',true);
INSERT INTO ts.requests (tenant_id, id, title, description, contact_email, state, revision, created_at)
VALUES ('b04512dd-4e65-4875-acbf-a73306a39adc','9b8c3680-ec06-4dd8-8654-dac389a291b1',
        'Recovery fixture','Synthetic only','qa@example.invalid','verified',1,now());
SQL

docker exec "$container" pg_dump -U postgres --format=custom "$source_db" > "$dump"
docker exec -i "$container" pg_restore -U postgres --exit-on-error --dbname="$target_db" < "$dump"
db -d "$target_db" <<'SQL'
SET ROLE taskservice_api;
DO $$ BEGIN
  IF (SELECT count(*) FROM ts.requests) <> 0 THEN
    RAISE EXCEPTION 'Restored RLS did not fail closed';
  END IF;
  PERFORM set_config('taskservice.tenant','b04512dd-4e65-4875-acbf-a73306a39adc',true);
  IF (SELECT count(*) FROM ts.requests WHERE title='Recovery fixture') <> 1 THEN
    RAISE EXCEPTION 'Expected restored request is missing';
  END IF;
  PERFORM set_config('taskservice.tenant','c84b1594-96e4-4d29-aff3-a804316608e0',true);
  IF (SELECT count(*) FROM ts.requests) <> 0 THEN
    RAISE EXCEPTION 'Restored tenant isolation failed';
  END IF;
END $$;
SQL
printf '%s\n' 'Logical dump restored into a separate disposable database. Runtime RLS and synthetic request verified.'
