# TaskService V2 development increment

The original Cloudflare Worker and D1 schema at the repository root are the existing live prototype. V2 lives here and has no deployment trigger. See [V2 coverage and evidence](docs/V2-traceability.md) before using the code outside a test environment.

## Build and verify

Use .NET 10, Node 22 and PostgreSQL 18. The integration tests require a disposable local PostgreSQL instance. They create and drop uniquely named test databases and use synthetic users only.

```sh
npm ci --prefix v2/web
npm run --prefix v2/web build
dotnet build v2/TaskService/TaskService.csproj
TASKSERVICE_TEST_ADMIN_DB='<disposable localhost PostgreSQL connection>' \
  dotnet test v2/tests/TaskService.Tests.csproj
docker build -f v2/Dockerfile .
```

Run the application from a working directory that contains the built frontend under `wwwroot`, or use the container build. Configure runtime, migration and worker credentials separately. Provision the database roles externally and apply `TaskService/Database/grants.sql` after migration.

| Entry | Behavior |
| --- | --- |
| `dotnet TaskService.dll migrate` | Runs the dedicated schema migration using `ConnectionStrings__Migration`. The API never applies migrations. |
| `dotnet TaskService.dll` | HTTP API and static UI using `ConnectionStrings__Runtime`. |
| `dotnet TaskService.dll worker` | Processes the verification, notification, report PDF and invoice scan outbox with `ConnectionStrings__Worker`. |

Runtime configuration uses `Identity__Authority`, `Identity__ClientId`, `Identity__ClientSecret` and the provider-specific `Identity__PrivilegedAcr`. Ensure the provider defines the privileged assurance value and controls the `taskservice_role` claim server-side. A missing IdP leaves login and readiness unavailable. Configure `PublicBaseUrl` as an HTTPS origin, `Mail__Host`, `Mail__From`, and any SMTP authentication in a secret store. The API and worker use a shared restricted Data Protection key directory. Review and verify proxy configuration before terminating TLS outside the app so the origin check sees the trusted request origin.

The test tenant UI lives at `/unternehmen/{slug}`, `/unternehmen/{slug}/betrieb` and `/unternehmen/{slug}/technik`. The platform catalog is at `/plattform`. A fresh environment requires test tenant and membership data. Automated tests seed both companies directly in their disposable database. OpenAPI is exposed at `/api/openapi/v1.json`.

## Working reports

Assigned technicians can submit dated activities with duration, an outcome and optional materials on a request in progress. The company can also author on tickets it can access, subject to `report.create`. Grants in `ts.report_permissions` independently allow `report.approve` or `report.publish` for company staff. Apply grants using the database provisioning identity until tenant administration is implemented. The default tenant policy in `ts.report_policy` requires a reviewer distinct from the author.

Approval queues PDF generation as a separate worker job. Work report PDFs are stored as immutable, checksummed objects within tenant-scoped PostgreSQL rows in this increment. The renderer needs the DejaVu Sans Linux fonts installed by the container build. Customers can download a particular version only after publication to their verified account. Staff can revoke the release. Completing a request requires the current report version to be published. Attachments to reports remain open.

To run the browser-driven role flow, install Python Playwright and Chromium, build the frontend and set absolute paths in `TASKSERVICE_REPORT_BROWSER` to `v2/tests/report_browser.py`, `TASKSERVICE_BROWSER_WEBROOT` to `v2/web/dist` and optionally `TASKSERVICE_BROWSER_OUTPUT` to a disposable screenshots directory before running the same test command. The test starts an isolated HTTPS loopback host and uses only test data and identities.

## Invoice originals

Staff get explicit per-membership grants from `ts.invoice_permissions`. A technician can upload or read only when delegated and assigned to the request. The original filename is supplied in the UTF-8 percent-encoded `X-Invoice-Name` header. Upload the unchanged original as `application/pdf` or `application/xml` with an idempotency key and corresponding revision. The limit is 10 MiB per original. The service checks file type and safe XML parsing. Accounting, schema-level e-invoice conformance and tax validation are outside the application workflow.

The original is uploaded to a tenant-scoped quarantine. `ClamAv__Host` and `ClamAv__Port` must point to a private clamd instance with up-to-date official signatures, `StreamMaxLength` of at least 10 MiB and blocking alerts on encrypted content or scan limits. A scanner error leaves the file blocked, with retry and eventual failure. After a clean result, a different employee reviews the original. Publication has its own permission and is bound to the verified customer and exact version. Revocation stops customer downloads. A customer who can download receives the original bytes with no intermediary transformation.

CI runs the invoice browser workflow with controlled scan verdicts, as well as a TCP framing test. An isolated scanner test can use `TASKSERVICE_TEST_CLAMAV_PORT` on loopback with a synthetic signature for `TASKSERVICE-QA-SCAN-MARKER`. Set `TASKSERVICE_INVOICE_BROWSER` to the absolute path to `v2/tests/invoice_browser.py` to also drive the UI. This fixture does not validate production signature updates or production data handling.

## Production boundary

V2 is under development. The [read-only audit script](ops/publisher-capacity.sh) is for a verified Publisher SSH session. Production deployment must be gated on current capacity, known-host verification, restore evidence, the full acceptance suite, environment-specific IdP/mail/storage configuration and approved data migration. There is no V2 cutover workflow in this increment.
