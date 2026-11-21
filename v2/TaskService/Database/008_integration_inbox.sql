CREATE TABLE ts.integration_inbox (
    tenant_id uuid NOT NULL REFERENCES ts.tenants,
    connection_id uuid NOT NULL,
    event_id uuid NOT NULL,
    event_type varchar(80) NOT NULL,
    schema_version integer NOT NULL CHECK (schema_version = 1),
    resource_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    occurred_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL,
    checksum bytea NOT NULL CHECK (octet_length(checksum) = 32),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 131072),
    PRIMARY KEY (tenant_id,connection_id,event_id),
    UNIQUE (tenant_id,connection_id,resource_id,revision)
);
CREATE INDEX integration_inbox_recent ON ts.integration_inbox (tenant_id,received_at DESC,event_id DESC);
ALTER TABLE ts.integration_inbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE ts.integration_inbox FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_scope ON ts.integration_inbox
    USING (tenant_id = nullif(current_setting('taskservice.tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('taskservice.tenant', true), '')::uuid);
