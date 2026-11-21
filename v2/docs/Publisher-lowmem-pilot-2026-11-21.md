# TaskService V2 — Publisher 4 GiB low-memory pilot evidence

Date: 2026-11-21 (UTC). This is a **protected, loopback-only pilot**, **not a public production release**.

## Host and isolation

- Existing STRATO Publisher VPS has 3.762 GiB RAM, 2 vCPU, no swap.
- Publisher, Reimflow, FFM Aktuell, and other original workloads were left unchanged. PWYW had already been intentionally stopped.
- TaskService has its own directory `/opt/taskservice`, Docker Compose project, PostgreSQL 18 volume and database, generated dedicated DB-role secrets, worker, API and ClamAV scanner.
- Public TaskService Worker/D1 traffic is unchanged.
- API listens only on `127.0.0.1:18081` on the Publisher host. There is no public Caddy route.
- Docker uses cgroup v2 with the systemd driver. `taskservice.slice` imposes a **1536 MiB aggregate memory ceiling** and `1300M` MemoryHigh. Every TaskService container uses `cgroup_parent: taskservice.slice`.
- The experimental Compose profile is `v2/ops/compose-lowmem.experimental.yaml`. Do not confuse the profile with approved production sizing.

## Optimizations and independent evidence

- ClamAV `MaxThreads=1` and `ConcurrentDatabaseReload=no`.
- The low-memory profile mounts a version-matched freshclam configuration with `TestDatabases no`. This reduces peak memory at the cost of skipping the separate signature-database test at update time. It is **not** equivalent to normal ClamAV update validation. Keep scanner health alerting and fail-closed quarantine behavior; return to full validation when capacity permits.
- Standard image with a 1344 MiB limit OOM-killed during signature database testing; revised test passed with EICAR detected and approximately 956 MiB used: https://github.com/hsdarestani/taskservice/actions/runs/44933986342.
- Full stack built and passed an isolated Docker smoke test: https://github.com/hsdarestani/taskservice/actions/runs/44934165279. This is not a multi-hour load or complete business-flow test.

## Live Publisher sample

Read-only measurements at **2026-11-21 18:20:28 UTC**:
- `taskservice-api-1`: 44.83 MiB, running, no OOM, zero restarts.
- `taskservice-worker-1`: 101.6 MiB, running, no OOM, zero restarts.
- `taskservice-scanner-1`: 950.6 MiB, running, healthy, no OOM, zero restarts; PING=PONG.
- `taskservice-db-1`: 45.16 MiB, running, healthy, no OOM, zero restarts.
- Sum approximately **1142 MiB / 1.12 GiB** of live Docker memory (not a worst-case guarantee).
- Host MemAvailable: `755968 KiB` (~738 MiB); this is a **tight headroom**, so do not blindly increase concurrency.
- Publisher health endpoint: HTTP 200.
- TaskService `/health/live`: HTTP 200.
- TaskService `/health/ready`: HTTP 503, **as expected**, because real OIDC identity and other required production configuration are missing; Docker API healthcheck accordingly says *unhealthy*. Do not use live endpoint as proof of release readiness.
- Evidence: https://github.com/hsdarestani/publisher/actions/runs/44934961441.

## Production blockers — do not cut over before resolving

1. Real OIDC identity provider for customers and workforce, actual registration and admin MFA verification.
2. Real SMTP delivery for guest verification and invitations. Pilot worker has synthetic SMTP placeholders, not functioning email.
3. Publisher backup location and restore rehearsal; TaskService durable backup, safe D1 inventory/migration/rollback.
4. Browser end-to-end staging using real identity and mail, authorization, upload quarantine and report/invoice downloads.
5. Long-run resource testing of signature updates, multiple uploads and concurrent traffic with sufficient host headroom. Protect other running Publisher applications.
6. Final Caddy/Cloudflare routing and public migration only after the above gates pass.

## Execution history and limitations

- Initial staging job https://github.com/hsdarestani/publisher/actions/runs/44934419141 only reached PostgreSQL; a Compose command consumed the rest of the SSH heredoc. Its successful status was **not** full staging success.
- Resume job https://github.com/hsdarestani/publisher/actions/runs/44934781409 created all four running containers and the pilot tenant.
- Independent inspection https://github.com/hsdarestani/publisher/actions/runs/44934961441 confirmed above states.
- A running pilot is not a complete production release. Keep the public Cloudflare Worker serving until authentication, data continuity and safety are verified.
