#!/usr/bin/env bash
# Invoke from the TaskService deployment directory as its authorized operator.
set -euo pipefail
umask 077
: "${TASKSERVICE_AGE_RECIPIENT:?Set the approved off-host backup recipient public age key.}"
: "${TASKSERVICE_BACKUP_DIR:?Set the TaskService encrypted backup directory.}"
[[ "$TASKSERVICE_BACKUP_DIR" = /* ]]
command -v age >/dev/null
install -d -m 0700 "$TASKSERVICE_BACKUP_DIR"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
staging="$(mktemp -d "$TASKSERVICE_BACKUP_DIR/.partial.XXXXXXXX")"
trap 'rm -rf -- "$staging"' EXIT
docker compose -p taskservice exec -T db sh -c 'exec pg_dump --no-password -U "${POSTGRES_USER:-postgres}" --format=custom --dbname=taskservice' |
  age -r "$TASKSERVICE_AGE_RECIPIENT" -o "$staging/postgres.dump.age"
docker compose -p taskservice exec -T api tar -C /var/lib/taskservice/keys -cf - . |
  age -r "$TASKSERVICE_AGE_RECIPIENT" -o "$staging/data-protection.tar.age"
(
  cd "$staging"
  sha256sum postgres.dump.age data-protection.tar.age > SHA256SUMS
)
destination="$TASKSERVICE_BACKUP_DIR/taskservice-$stamp"
[[ ! -e "$destination" ]]
mv -T -- "$staging" "$destination"
printf 'Encrypted TaskService backup: %s\n' "$destination"
