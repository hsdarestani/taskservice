CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE SCHEMA ts;
REVOKE ALL ON SCHEMA ts FROM PUBLIC;

CREATE TABLE ts.tenants (
    id uuid PRIMARY KEY,
    slug varchar(63) NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    name varchar(120) NOT NULL,
    accent varchar(7) NOT NULL CHECK (accent ~ '^#[0-9a-fA-F]{6}$'),
    active boolean NOT NULL
);

CREATE TABLE ts.members (
    tenant_id uuid NOT NULL REFERENCES ts.tenants,
    id uuid NOT NULL,
    subject_key varchar(64) NOT NULL,
    name varchar(120) NOT NULL,
    role varchar(24) NOT NULL CHECK (role IN ('admin', 'dispatcher', 'technician')),
    active boolean NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, subject_key)
);

CREATE TABLE ts.requests (
    tenant_id uuid NOT NULL REFERENCES ts.tenants,
    id uuid NOT NULL,
    title varchar(160) NOT NULL,
    description varchar(4000) NOT NULL,
    contact_email varchar(254) NOT NULL,
    customer_key varchar(64),
    technician_id uuid,
    state varchar(24) NOT NULL CHECK (state IN
        ('received', 'verified', 'triage', 'awaiting_customer', 'assigned', 'scheduled',
         'in_progress', 'completed', 'cancelled')),
    resume_state varchar(24),
    revision integer NOT NULL CHECK (revision > 0),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, technician_id) REFERENCES ts.members (tenant_id, id)
);
CREATE INDEX requests_customer ON ts.requests (tenant_id, customer_key, id);
CREATE INDEX requests_technician ON ts.requests (tenant_id, technician_id, id);

CREATE TABLE ts.claims (
    tenant_id uuid NOT NULL,
    request_id uuid NOT NULL,
    digest bytea NOT NULL CHECK (octet_length(digest) = 32),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    PRIMARY KEY (tenant_id, request_id),
    FOREIGN KEY (tenant_id, request_id) REFERENCES ts.requests (tenant_id, id)
);

CREATE TABLE ts.appointments (
    tenant_id uuid NOT NULL,
    id uuid NOT NULL,
    request_id uuid NOT NULL,
    technician_id uuid NOT NULL,
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL CHECK (ends_at > starts_at),
    cancelled boolean NOT NULL,
    arrival_status varchar(24) NOT NULL CHECK (arrival_status IN ('planned', 'departed', 'delayed', 'arrived')),
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, request_id) REFERENCES ts.requests (tenant_id, id),
    FOREIGN KEY (tenant_id, technician_id) REFERENCES ts.members (tenant_id, id),
    EXCLUDE USING gist (tenant_id WITH =, technician_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
        WHERE (NOT cancelled)
);
CREATE UNIQUE INDEX appointments_one_active ON ts.appointments (tenant_id, request_id) WHERE NOT cancelled;

CREATE TABLE ts.history (
    tenant_id uuid NOT NULL,
    request_id uuid NOT NULL,
    revision integer NOT NULL,
    state varchar(24) NOT NULL,
    at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, request_id, revision),
    FOREIGN KEY (tenant_id, request_id) REFERENCES ts.requests (tenant_id, id)
);

CREATE TABLE ts.messages (
    tenant_id uuid NOT NULL REFERENCES ts.tenants,
    sequence bigint GENERATED ALWAYS AS IDENTITY,
    request_id uuid,
    channel varchar(12) NOT NULL CHECK (channel IN ('customer', 'internal', 'company')),
    author varchar(120) NOT NULL,
    body varchar(2000) NOT NULL,
    sent_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, sequence),
    FOREIGN KEY (tenant_id, request_id) REFERENCES ts.requests (tenant_id, id),
    CHECK ((channel = 'company') = (request_id IS NULL))
);
CREATE INDEX messages_room ON ts.messages (tenant_id, request_id, channel, sequence);

CREATE TABLE ts.audit (
    tenant_id uuid NOT NULL REFERENCES ts.tenants,
    id uuid NOT NULL,
    actor varchar(64) NOT NULL,
    action varchar(64) NOT NULL,
    object_id uuid NOT NULL,
    at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id)
);
CREATE INDEX audit_tenant_order ON ts.audit (tenant_id, at DESC);
CREATE TABLE ts.commands (
    tenant_id uuid NOT NULL REFERENCES ts.tenants,
    actor varchar(64) NOT NULL,
    operation varchar(80) NOT NULL,
    key varchar(128) NOT NULL,
    fingerprint bytea NOT NULL,
    resource_id uuid NOT NULL,
    response text NOT NULL CHECK (jsonb_typeof(response::jsonb) = 'object'),
    location text,
    revision integer,
    status integer NOT NULL,
    PRIMARY KEY (tenant_id, actor, operation, key)
);
CREATE TABLE ts.outbox (
    tenant_id uuid NOT NULL REFERENCES ts.tenants,
    id uuid NOT NULL,
    kind varchar(24) NOT NULL,
    payload text NOT NULL,
    available_at timestamptz NOT NULL,
    lease_id uuid,
    leased_until timestamptz,
    attempts integer NOT NULL,
    completed_at timestamptz,
    dead_at timestamptz,
    PRIMARY KEY (tenant_id, id)
);
CREATE INDEX outbox_ready ON ts.outbox (tenant_id, available_at) WHERE completed_at IS NULL AND dead_at IS NULL;

DO $$
DECLARE name text;
BEGIN
    FOREACH name IN ARRAY ARRAY['members','requests','claims','appointments','history','messages','audit','commands','outbox'] LOOP
        EXECUTE format('ALTER TABLE ts.%I ENABLE ROW LEVEL SECURITY', name);
        EXECUTE format('ALTER TABLE ts.%I FORCE ROW LEVEL SECURITY', name);
        EXECUTE format(
            'CREATE POLICY tenant_scope ON ts.%I USING (tenant_id = nullif(current_setting(''taskservice.tenant'', true), '''')::uuid) WITH CHECK (tenant_id = nullif(current_setting(''taskservice.tenant'', true), '''')::uuid)', name);
    END LOOP;
END $$;
