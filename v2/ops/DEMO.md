# TaskService public demo operations

The public demo uses the existing PostgreSQL, ASP.NET Core API and worker, and
ClamAV on the Publisher VPS. Start with the existing low-memory profile, the
TaskService cgroup and the dedicated volumes. Publisher services keep their
current deployment settings.

- Native auth is selected with `Identity__Mode=native` and
  `Demo__Enabled=true`. Add the public HTTPS base URL and the actual Caddy
  address to the trusted proxy configuration. The ASP.NET Data Protection key
  volume must be shared by the API, worker and account operator.
- Customer signup creates an unverified account with a stable UUID, an Identity
  password hash and no workforce privilege. New requests belong to the signed-in
  UUID. A matching email address grants no access to previously stored requests.
- Privileged accounts require a separately provisioned TOTP secret and a valid
  one-time code. The demo workforce accounts are provisioned from the host by
  an authorized operator. A visitor cannot create or promote a workforce account.
- With `Mail__DeliveryMode=disabled`, new mail is encrypted and stored as
  `mail_held`. The mail worker consumes only `mail`; changing SMTP settings
  will not deliver held messages without review. Do not release demo messages
  to real recipients. Domain and account settings for outbound sending are
  separate from Cloudflare Email Routing.
- The public demo does not import Worker/D1 records. Apply D1 migrations only
  to an approved non-demo tenant after reconciliation.

## Recovery and workforce provisioning

Verify the account holder by an established private channel before a recovery.
The authorized operator generates a new strong password on a trusted device
and delivers it privately. If required, generate a fresh 160-bit TOTP key,
enroll the operator securely, and replace the stored key. Do not request an
old password, print credentials in CI, or reuse demo credentials elsewhere.
The command invalidates every existing session and preserves the account UUID.

Feed a permission-0600 JSON file via standard input to the image's
`native-account` command. Acceptable keys are `action` (`provision` or
`recover`), `email`, `password`, `role`, `tenant`, `name` and
`totpSecretHex`. Omit the role and tenant when recovering. A new privileged
account requires a TOTP seed. Set `Identity__RecoveryMode=external` with an
approved HTTPS recovery URL only when a working recovery integration exists.

```sh
docker compose -p taskservice -f compose.yaml -f compose-lowmem.experimental.yaml \
  run --rm -T -e DataProtection__KeyDirectory=/var/lib/taskservice/keys \
  -v taskservice_keys:/var/lib/taskservice/keys migrate native-account < /secure/account.json
```

## Go-live and rollback

Verify HTTPS, the demo label, the four portal roles, file scanning, held mail,
and Publisher health. Save the Worker custom-domain mapping and active version,
existing DNS settings, and the previous Caddy configuration. Validate the
single new Caddy virtual host before a graceful reload. Use the already issued
certificate for the canonical TaskService hostname and a scoped Cloudflare TLS
rule. Stage through the origin first. Validate the public route after switching.

If verification fails, remove the replacement TaskService DNS entry and
reattach the incumbent Worker custom domain. Run a public check on the restored
Worker. Preserve D1 and the V2 database independently. Roll back the TaskService
image and configuration using the pre-demo snapshot if necessary. Restore
database backups to disposable PostgreSQL for verification before replacing any
live data.
