# TaskService V2 staging and production operations

Use these files for one isolated TaskService project under `/opt/taskservice`. Its API listens on a configurable loopback-only port. PostgreSQL and ClamAV expose no host ports. Keep production identity, email and backup material outside the repository.

## Before starting containers

1. Verify the Publisher SSH host key against an independently trusted source. Run the capacity audit and review representative peaks, disk, backups and a recovery rehearsal. For the existing Publisher pilot, use both `compose.yaml` and `compose-lowmem.experimental.yaml` under the existing `taskservice.slice`. See the [measured pilot](../docs/Publisher-lowmem-pilot-2026-11-21.md). The base-only `start-isolated.sh` uses sizing for the standard profile and is for a fresh installation.
2. Build the approved revision off-host. Record the immutable digest for the API, PostgreSQL 18 and supported official ClamAV image. Copy the contents of this directory to the deployment directory, keeping the non-secret `postgres/initialize.sh` readable and executable by the container user, alongside `TaskService/Database/grants.sql` from the same commit at `postgres/grants.sql`.
3. Set the Compose interpolation file `.env` with `TASKSERVICE_IMAGE`, `TASKSERVICE_POSTGRES_IMAGE`, `TASKSERVICE_CLAMAV_IMAGE` and a free `TASKSERVICE_HTTP_PORT`. Protect `.env` and all `secrets/*.env` files with mode 0600. Use an approved secret store to provision them.

| File | Settings |
| --- | --- |
| `secrets/db.env` | `POSTGRES_PASSWORD` and independent `TASKSERVICE_MIGRATOR_PASSWORD`, `TASKSERVICE_API_PASSWORD`, `TASKSERVICE_WORKER_PASSWORD`, `TASKSERVICE_PLATFORM_PASSWORD` for the initial volume. |
| `secrets/migrate.env` | `ConnectionStrings__Migration` using `taskservice_migrator`. |
| `secrets/api.env` | `ConnectionStrings__Runtime` using `taskservice_api`; `ConnectionStrings__Platform` using `taskservice_platform`. Set `PublicBaseUrl`, `AllowedHosts`, `TASKSERVICE_HEALTH_HOST`, `Routing__DefaultTenantSlug`, `Privacy__NoticeUrl`, `Identity__Authority`, `Identity__ClientId`, `Identity__ClientSecret`, the privileged assurance policy below, `Mail__Host` and `Mail__From`. Configure the actual proxy IP through `Operations__TrustedProxies__0`. |
| `secrets/worker.env` | `ConnectionStrings__Worker` using `taskservice_worker`, plus real `Mail__Host`, `Mail__Port`, `Mail__From` and any SMTP authentication. |

Point connection strings at host `db`, database `taskservice`, using the corresponding role and its matching password. Register both OIDC callbacks on the actual staging or production hostname. Configure approved HTTPS privacy information and real administrative assurance and role claims. Keep optional integrations disabled unless explicitly authorized.

Register a confidential OIDC web application using Authorization Code and PKCE with
`/signin-oidc` and `/signin-oidc-workforce` on the verified HTTPS hostname.
Enable customer self-registration, email verification and recovery at the provider.
Choose a provider-enforced MFA policy and configure `Identity__PrivilegedAcr` to
its demonstrated ACR or set `Identity__PrivilegedAmr=mfa` when the provider attests
MFA in the signed ID token. When both are set, both proofs are required.
Grant `taskservice_role=platform_admin` only to authorized operators. For initial
operator provisioning, `Identity__PlatformAdminSubject` may contain the exact
provider subject of one authorized operator. That subject also requires the
workforce session and configured MFA. Use the approved secret store for the OIDC
client secret and the worker's real SMTP username and password.

## Isolated start

Use `start-isolated.sh` for the first start on the audited host. The script initializes only the dedicated database, applies migrations with the dedicated migrator role, and starts the API, worker and scanner. Before readiness can pass, a real platform operator needs to establish the configured default tenant. Supply a protected HTTPS staging entry point for that work. Route a new hostname using a minimal dedicated proxy rule and keep the existing Worker domain untouched.

Stage and verify customer/workforce sign-in and MFA, real mail delivery, the claim flow, both tenant boundaries, reports, invoice and photo scanning, SignalR and the new tenant landing page. Check that the Cloudflare edge rejects unauthorized access to any pilot and that the origin is unreachable from outside expected paths. Validate liveness, readiness and the state of the workers independently. Readiness checks the configured tenant, OIDC metadata, and ClamAV response. A delivery test is still required for SMTP.

In the low-memory profile, API readiness and the file worker additionally verify
that ClamAV has a loaded database built in the past 72 hours. A failed signature
check preserves the upload's non-downloadable state and triggers scan retries.
Watch scanner health, file-scan retries and dead-letter counts through a complete
FreshClam update and reload during the capacity rehearsal.

## Backups and rollback

Run `backup.sh` with the configured secure backup location and public age recipient. Transfer the encrypted directory to the approved off-host destination with retention. Preserve the recovery identity and necessary deployment configuration separately, and verify the encrypted checksums before attempting recovery. Restore the PostgreSQL backup into a separate test system with the expected roles provisioned. Recover the Data Protection volume for continuity of protected application state, then repeat tenant and file access verification. The automated smoke tests are for disposable data.

Before cutover, back up D1 and reconcile its ticket, attachment and message ownership with the destination tenant. Capture the active Cloudflare Worker routing and redirect configuration. Keep Worker, D1 and the previous configuration available. To revert the public cutover, restore the original route and verify the Worker, then stop only the TaskService API and worker after preserving its database and the records received since cutover. Reconcile those records before retrying. Production traffic is a separate operation from starting the Compose project.

### D1 snapshot rehearsal

Use the official D1 SQL export and store it in the approved private backup
location. Verify the export's digest, SQLite integrity, and source and target
record counts after every rehearsal. Install the approved Psycopg 3 version
on the operator host. Supply the dedicated migrator connection securely via
`TASKSERVICE_IMPORT_DSN`.

```sh
python v2/ops/import-d1.py /secure/taskservice-d1.sql --tenant "$TENANT_ID" --slug "$TENANT_SLUG"
```

The default run rolls back all PostgreSQL writes. Use `--apply` only on the
validated final snapshot after reconciling ownership and stopping source writes.
Use an approved, empty destination tenant. An exact snapshot replay does not
insert duplicates. A different snapshot or a destination with existing requests
requires reconciliation. Legacy request IDs map to deterministic tenant-local IDs;
preserved photos are initially private and queued for scanning. Imported requests
are not linked to accounts by email address. Keep a verified procedure to claim
historical requests before sending customer invitations or switching traffic.
