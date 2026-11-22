#!/bin/sh
# Official PostgreSQL entrypoint runs this file once on the empty TaskService volume.
set -eu
: "${TASKSERVICE_MIGRATOR_PASSWORD:?}"
: "${TASKSERVICE_API_PASSWORD:?}"
: "${TASKSERVICE_WORKER_PASSWORD:?}"
: "${TASKSERVICE_PLATFORM_PASSWORD:?}"
psql --no-psqlrc --set=ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname taskservice <<'SQL'
\getenv migration_password TASKSERVICE_MIGRATOR_PASSWORD
\getenv api_password TASKSERVICE_API_PASSWORD
\getenv worker_password TASKSERVICE_WORKER_PASSWORD
\getenv platform_password TASKSERVICE_PLATFORM_PASSWORD
CREATE ROLE taskservice_migrator LOGIN PASSWORD :'migration_password' NOSUPERUSER NOBYPASSRLS;
CREATE ROLE taskservice_api LOGIN PASSWORD :'api_password' NOSUPERUSER NOBYPASSRLS;
CREATE ROLE taskservice_worker LOGIN PASSWORD :'worker_password' NOSUPERUSER NOBYPASSRLS;
CREATE ROLE taskservice_platform LOGIN PASSWORD :'platform_password' NOSUPERUSER NOBYPASSRLS;
REVOKE ALL ON DATABASE taskservice FROM PUBLIC;
GRANT CONNECT ON DATABASE taskservice TO taskservice_api, taskservice_worker, taskservice_platform;
ALTER DATABASE taskservice OWNER TO taskservice_migrator;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
