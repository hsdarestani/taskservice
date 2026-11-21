#!/usr/bin/env bash
# Initial pre-cutover start from a fingerprint-verified Publisher session.
set -euo pipefail
cd "$(dirname "$0")"
[[ "$PWD" == /opt/taskservice ]] || { printf 'Run from /opt/taskservice.\n' >&2; exit 1; }
project=(docker compose -p taskservice)
for file in .env secrets/db.env secrets/migrate.env secrets/api.env secrets/worker.env; do
  if [[ ! -f "$file" || -L "$file" || "$(stat -c %a "$file")" != 600 ]]; then
    printf 'Expected a mode-0600 regular configuration file: %s\n' "$file" >&2
    exit 1
  fi
done
if [[ -n "$("${project[@]}" ps --all --quiet api worker)" ]]; then
  printf 'An existing TaskService application is present. Use the reviewed upgrade procedure.\n' >&2
  exit 1
fi
available_kib="$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)"
# 5.125 GiB max for the four steady services plus 1 GiB reserved for existing workloads.
if [[ ! "$available_kib" =~ ^[0-9]+$ ]] || (( available_kib < 6422528 )); then
  printf 'TaskService startup declined: host has %s KiB available.\n' "${available_kib:-unknown}" >&2
  exit 1
fi
"${project[@]}" config --quiet
"${project[@]}" up -d --wait --wait-timeout 90 db
"${project[@]}" run --rm migrate
"${project[@]}" exec -T db sh -ec 'exec psql -X --no-password --set ON_ERROR_STOP=1 -U "${POSTGRES_USER:-postgres}" -d taskservice' < postgres/grants.sql
"${project[@]}" up -d api worker scanner
printf 'TaskService containers started. Keep public routes unchanged until all acceptance checks pass.\n'
