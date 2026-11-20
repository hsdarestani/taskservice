#!/usr/bin/env bash
# Inspect the already authenticated Publisher host. No writes or stress tests.
set -euo pipefail

printf '%s\n' '=== Identity and capacity ==='
date -u '+%Y-%m-%dT%H:%M:%SZ'
test -d /opt/aplus-publisher
nproc
lscpu | awk -F: '/^(CPU\(s\)|On-line CPU\(s\) list|Socket\(s\)|Core\(s\) per socket|Thread\(s\) per core):/ {print}'
uptime
free -m
swapon --show=TYPE,SIZE,USED,PRIO
df -h / /opt
df -i / /opt
for resource in cpu memory io; do
  file="/proc/pressure/$resource"
  if [[ -r "$file" ]]; then
    printf 'PSI %s: ' "$resource"
    cat "$file"
  fi
done
printf '%s\n' '=== Light sampling ==='
if command -v vmstat >/dev/null; then vmstat 1 4; fi
printf '%s\n' '=== Containers, utilization and listeners ==='
docker ps --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
docker stats --no-stream --format 'table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.BlockIO}}'
ss -H -lntu | awk '{print $1, $5}'
cd /opt/aplus-publisher
docker compose ps --format 'table {{.Service}}\t{{.State}}\t{{.Health}}'
printf '%s\n' '=== Publisher database counters ==='
docker compose exec -T db sh -c 'PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=5000" psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At' <<'SQL'
SELECT 'connections=' || count(*) || ',active=' || count(*) FILTER (WHERE state='active') FROM pg_stat_activity WHERE datname=current_database();
SELECT 'commits=' || xact_commit || ',rollbacks=' || xact_rollback || ',disk_blocks_read=' || blks_read || ',cache_hits=' || blks_hit FROM pg_stat_database WHERE datname=current_database();
SQL
printf '%s\n' '=== Backup evidence supplied to the operator ==='
for dir in /opt/aplus-publisher/backups /var/backups/aplus-publisher; do
  if [[ -d "$dir" ]]; then
    find "$dir" -maxdepth 1 -type f -printf '%TY-%Tm-%Td %TH:%TM UTC %s bytes\n' | sort -r | sed -n '1,5p'
  fi
done
printf '%s\n' 'Record the latest independently verified restore and representative historical peaks from established monitoring.'
