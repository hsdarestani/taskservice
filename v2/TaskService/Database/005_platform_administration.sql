ALTER TABLE ts.tenants
    ADD COLUMN revision integer NOT NULL DEFAULT 1 CHECK (revision > 0),
    ADD COLUMN lifecycle varchar(16) NOT NULL DEFAULT 'active' CHECK (lifecycle IN ('active','suspended','deactivated'));
UPDATE ts.tenants SET lifecycle='suspended' WHERE NOT active;
ALTER TABLE ts.tenants ADD CONSTRAINT tenants_lifecycle_agrees_with_active CHECK (active = (lifecycle='active'));

CREATE TABLE ts.platform_audit (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES ts.tenants,
    actor varchar(64) NOT NULL,
    action varchar(24) NOT NULL,
    reason varchar(500),
    recorded_at timestamptz NOT NULL
);
CREATE INDEX platform_audit_latest ON ts.platform_audit (recorded_at DESC, id DESC);

CREATE TABLE ts.platform_commands (
    actor varchar(64) NOT NULL,
    operation varchar(80) NOT NULL,
    key uuid NOT NULL,
    fingerprint bytea NOT NULL CHECK (octet_length(fingerprint)=32),
    response text NOT NULL CHECK (jsonb_typeof(response::jsonb)='object'),
    revision integer NOT NULL,
    status integer NOT NULL CHECK (status IN (200,201)),
    PRIMARY KEY (actor, operation, key)
);
