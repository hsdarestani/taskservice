#!/usr/bin/env bash
# Called on Publisher over pinned SSH after loading a CI-built image.
set -euo pipefail
umask 077
cd /opt/taskservice
[[ "$#" == 1 && "$1" =~ ^taskservice:demo-[a-f0-9]{7,40}$ ]]
image="$1"
compose() { docker compose -p taskservice -f compose.yaml -f compose-lowmem.experimental.yaml "$@"; }
demo_compose() { compose -f compose-demo-edge.yaml "$@"; }
publisher_health() {
  (cd /opt/aplus-publisher && docker compose exec -T web curl -fsS --max-time 5 http://127.0.0.1:8000/healthz/ </dev/null >/dev/null)
}
host_memory() { awk '/^MemAvailable:/ {print $2}' /proc/meminfo; }

publisher_health
[[ "$(systemctl show taskservice.slice -p MemoryMax --value)" == 1610612736 ]]
[[ "$(host_memory)" -ge 512000 ]]
for config in .env secrets/api.env secrets/worker.env; do
  [[ -f "$config" && ! -L "$config" && "$(stat -c %a "$config")" == 600 ]]
done
[[ "$(grep -c '^TASKSERVICE_IMAGE=' .env)" == 1 ]]
docker image inspect "$image" >/dev/null
docker volume inspect taskservice_keys >/dev/null
caddy="$(cd /opt/aplus-publisher && docker compose ps -q caddy)"
project="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$caddy")"
network="${project}_default"
proxy="$(docker inspect -f '{{(index .NetworkSettings.Networks "'"$network"'").IPAddress}}' "$caddy")"
[[ "$network" =~ ^[A-Za-z0-9_-]+$ && "$proxy" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]

backup="/opt/taskservice/backups/pre-native-demo-$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 0700 "$backup"
cp -- .env "$backup/compose.env"
cp -- secrets/api.env "$backup/api.env"
cp -- secrets/worker.env "$backup/worker.env"
if [[ -f compose-demo-edge.yaml ]]; then
  cp -- compose-demo-edge.yaml "$backup/edge.yaml"
fi
compose exec -T db psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d taskservice -c \
  "SELECT count(*) FROM ts.requests WHERE tenant_id IN (SELECT id FROM ts.tenants WHERE slug NOT IN ('demo','demo-validation'))" \
  </dev/null | grep -qx 0
compose exec -T db pg_dump -U postgres -Fc taskservice </dev/null > "$backup/taskservice.pgdump"
compose exec -T db pg_restore --list < "$backup/taskservice.pgdump" >/dev/null
compose exec -T api tar -C /var/lib/taskservice -cf - keys </dev/null > "$backup/keys.tar"
tar -tf "$backup/keys.tar" >/dev/null
(cd "$backup" && sha256sum taskservice.pgdump keys.tar > SHA256SUMS && sha256sum -c SHA256SUMS)

rollback() {
  status=$?
  if (( status != 0 )); then
    install -m 0600 "$backup/compose.env" .env
    install -m 0600 "$backup/api.env" secrets/api.env
    install -m 0600 "$backup/worker.env" secrets/worker.env
    if [[ -f "$backup/edge.yaml" ]]; then
      install -m 0600 "$backup/edge.yaml" compose-demo-edge.yaml
      demo_compose up -d --no-deps api worker </dev/null
    else
      rm -f -- compose-demo-edge.yaml
      compose up -d --no-deps api worker </dev/null
    fi
    printf 'Previous pilot application configuration restored.\n'
  fi
  return "$status"
}
trap rollback EXIT
compose stop api worker </dev/null
sed -i "s|^TASKSERVICE_IMAGE=.*|TASKSERVICE_IMAGE=$image|" .env
chmod 0600 .env
compose run --rm -T migrate </dev/null

compose exec -T db psql -X -q -v ON_ERROR_STOP=1 -U postgres -d taskservice <<'SQL'
INSERT INTO ts.tenants (id,slug,name,accent,active) VALUES
  ('cb28fdcc-7d6e-4f57-aa25-0cd3522ebe99','demo','TaskService Demo','#1064e8',true),
  ('f86cf9d8-3213-49bd-baf0-60ec31e516dc','demo-validation','TaskService Demo Süd','#1064e8',true)
ON CONFLICT (slug) DO NOTHING;
SQL

install -d -m 0700 /opt/taskservice/demo-private
python3 - /opt/taskservice/demo-private <<'PY'
import json
import pathlib
import secrets
import sys
target = pathlib.Path(sys.argv[1])
roles = [
    ("platform", "platform", None, None),
    ("company", "admin", "demo", "Demo-Verwaltung"),
    ("reviewer", "dispatcher", "demo", "Demo-Freigabe"),
    ("technician", "technician", "demo", "Demo-Technik"),
    ("other", "admin", "demo-validation", "Demo-Verwaltung Süd"),
]
for alias, role, tenant, name in roles:
    filename = target / (alias + ".json")
    if filename.exists():
        continue
    with filename.open("x") as credential:
        json.dump(dict(action="provision", email=alias + "@taskservice-demo.invalid",
                       password=secrets.token_urlsafe(32), role=role, tenant=tenant, name=name,
                       totpSecretHex=secrets.token_hex(20) if role != "technician" else None), credential)
PY

for role in platform company reviewer technician other; do
  found="$(compose exec -T db psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d taskservice \
    -c "SELECT count(*) FROM ts.native_accounts WHERE email='$role@taskservice-demo.invalid'" </dev/null)"
  if [[ "$found" == 0 ]]; then
    compose run --rm -T -e DataProtection__KeyDirectory=/var/lib/taskservice/keys \
      -v taskservice_keys:/var/lib/taskservice/keys migrate native-account < "/opt/taskservice/demo-private/$role.json"
  fi
done

compose exec -T db psql -X -q -v ON_ERROR_STOP=1 -U postgres -d taskservice <<'SQL'
INSERT INTO ts.report_permissions (tenant_id,member_id,permission)
SELECT tenant_id,id,'report.approve' FROM ts.members WHERE name='Demo-Freigabe' AND tenant_id='cb28fdcc-7d6e-4f57-aa25-0cd3522ebe99'
ON CONFLICT DO NOTHING;
INSERT INTO ts.report_permissions (tenant_id,member_id,permission)
SELECT tenant_id,id,'report.publish' FROM ts.members WHERE name='Demo-Freigabe' AND tenant_id='cb28fdcc-7d6e-4f57-aa25-0cd3522ebe99'
ON CONFLICT DO NOTHING;
INSERT INTO ts.invoice_permissions (tenant_id,member_id,permission)
SELECT m.tenant_id,m.id,p.permission FROM ts.members m
CROSS JOIN (VALUES ('invoice.approve'),('invoice.publish'),('invoice.read')) p(permission)
WHERE m.name='Demo-Freigabe' AND m.tenant_id='cb28fdcc-7d6e-4f57-aa25-0cd3522ebe99'
ON CONFLICT DO NOTHING;
INSERT INTO ts.invoice_permissions (tenant_id,member_id,permission)
SELECT m.tenant_id,m.id,p.permission FROM ts.members m
CROSS JOIN (VALUES ('invoice.upload'),('invoice.read')) p(permission)
WHERE m.name='Demo-Verwaltung' AND m.tenant_id='cb28fdcc-7d6e-4f57-aa25-0cd3522ebe99'
ON CONFLICT DO NOTHING;
SQL

sed -i '/^\(Identity__Mode\|Identity__RecoveryMode\|Demo__Enabled\|Routing__DefaultTenantSlug\|PublicBaseUrl\|AllowedHosts\|Privacy__NoticeUrl\|Operations__TrustedProxies__0\|Mail__DeliveryMode\)=/d' secrets/api.env
cat >> secrets/api.env <<ENV
Identity__Mode=native
Identity__RecoveryMode=demo
Demo__Enabled=true
Routing__DefaultTenantSlug=demo
PublicBaseUrl=https://taskservice.smarbiz.sbs
AllowedHosts=localhost;taskservice.smarbiz.sbs
Privacy__NoticeUrl=https://taskservice.smarbiz.sbs/demo-datenschutz
Operations__TrustedProxies__0=$proxy
Mail__DeliveryMode=disabled
ENV
sed -i '/^Mail__DeliveryMode=/d' secrets/worker.env
printf 'Mail__DeliveryMode=disabled\n' >> secrets/worker.env
chmod 0600 secrets/api.env secrets/worker.env
cat > compose-demo-edge.yaml <<YAML
services:
  api:
    networks:
      data: {}
      scan: {}
      egress: {}
      publisher_edge:
        aliases: [taskservice-demo-api]
networks:
  publisher_edge:
    external: true
    name: $network
YAML

demo_compose config --quiet </dev/null
demo_compose up -d --no-deps api worker </dev/null
ready_url=http://127.0.0.1:18081/health/ready
for attempt in {1..30}; do
  if curl -fs -H 'Host: localhost' --max-time 5 "$ready_url" >/dev/null; then break; fi
  sleep 2
done
curl -fsS -H 'Host: localhost' --max-time 5 "$ready_url" >/dev/null
demo_compose exec -T api curl -fsS -H 'Host: localhost' --max-time 5 http://127.0.0.1:8080/health/live </dev/null >/dev/null
[[ "$(docker inspect -f '{{.State.Running}}|{{.State.OOMKilled}}' taskservice-worker-1)" == true\|false ]]
[[ "$(host_memory)" -ge 450000 ]]
publisher_health

if [[ ! -s demo-private/origin.key ]]; then
  openssl ecparam -name prime256v1 -genkey -noout -out demo-private/origin.key
fi
openssl req -new -key demo-private/origin.key -subj '/CN=taskservice.smarbiz.sbs' \
  -addext 'subjectAltName=DNS:taskservice.smarbiz.sbs' -out demo-private/origin.csr
printf 'PILOT_READINESS=200\nPUBLISHER_HEALTH=200\nRAM_AVAILABLE_KIB=%s\n' "$(host_memory)"
printf 'DEMO_ORIGIN_CSR='
base64 -w0 demo-private/origin.csr
printf '\n'
