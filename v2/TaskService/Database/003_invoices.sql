CREATE TABLE ts.invoice_permissions (
    tenant_id uuid NOT NULL,
    member_id uuid NOT NULL,
    permission varchar(24) NOT NULL CHECK (permission IN ('invoice.upload', 'invoice.read', 'invoice.approve', 'invoice.publish')),
    PRIMARY KEY (tenant_id, member_id, permission),
    FOREIGN KEY (tenant_id, member_id) REFERENCES ts.members (tenant_id, id)
);

CREATE TABLE ts.invoices (
    tenant_id uuid NOT NULL,
    request_id uuid NOT NULL,
    id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    current_version integer NOT NULL CHECK (current_version > 0),
    published_version integer CHECK (published_version BETWEEN 1 AND current_version),
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, request_id) REFERENCES ts.requests (tenant_id, id)
);

CREATE INDEX invoices_request ON ts.invoices (tenant_id, request_id, id);

CREATE TABLE ts.invoice_versions (
    tenant_id uuid NOT NULL,
    invoice_id uuid NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    uploader_id uuid NOT NULL,
    original_name varchar(160) NOT NULL,
    content_type varchar(24) NOT NULL CHECK (content_type IN ('application/pdf', 'application/xml')),
    size integer NOT NULL CHECK (size BETWEEN 1 AND 10485760),
    checksum bytea NOT NULL CHECK (octet_length(checksum) = 32),
    uploaded_at timestamptz NOT NULL,
    scan_status varchar(16) NOT NULL CHECK (scan_status IN ('pending', 'clean', 'quarantined', 'failed')),
    scan_at timestamptz,
    status varchar(16) NOT NULL CHECK (status IN ('uploaded', 'approved', 'rejected', 'published', 'superseded', 'revoked')),
    review_reason varchar(500),
    reviewer_id uuid,
    reviewed_at timestamptz,
    recipient_key varchar(64),
    published_at timestamptz,
    PRIMARY KEY (tenant_id, invoice_id, version),
    FOREIGN KEY (tenant_id, invoice_id) REFERENCES ts.invoices,
    FOREIGN KEY (tenant_id, uploader_id) REFERENCES ts.members,
    FOREIGN KEY (tenant_id, reviewer_id) REFERENCES ts.members,
    CHECK ((scan_status = 'pending') = (scan_at IS NULL)),
    CHECK ((reviewer_id IS NULL) = (reviewed_at IS NULL)),
    CHECK (status = 'uploaded' OR scan_status = 'clean'),
    CHECK ((recipient_key IS NULL) = (published_at IS NULL)),
    CHECK ((status IN ('published', 'superseded', 'revoked')) = (published_at IS NOT NULL))
);

ALTER TABLE ts.invoices ADD CONSTRAINT invoices_current_version
    FOREIGN KEY (tenant_id, id, current_version) REFERENCES ts.invoice_versions DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE ts.invoices ADD CONSTRAINT invoices_published_version
    FOREIGN KEY (tenant_id, id, published_version) REFERENCES ts.invoice_versions DEFERRABLE INITIALLY DEFERRED;

CREATE UNIQUE INDEX invoices_one_published ON ts.invoice_versions (tenant_id, invoice_id) WHERE status = 'published';

CREATE TABLE ts.invoice_files (
    tenant_id uuid NOT NULL,
    invoice_id uuid NOT NULL,
    version integer NOT NULL,
    content bytea NOT NULL CHECK (octet_length(content) BETWEEN 1 AND 10485760),
    PRIMARY KEY (tenant_id, invoice_id, version),
    FOREIGN KEY (tenant_id, invoice_id, version) REFERENCES ts.invoice_versions
);

DO $$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['invoice_permissions','invoices','invoice_versions','invoice_files'] LOOP
        EXECUTE format('ALTER TABLE ts.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('ALTER TABLE ts.%I FORCE ROW LEVEL SECURITY', table_name);
        EXECUTE format('CREATE POLICY tenant_scope ON ts.%I USING (tenant_id = nullif(current_setting(''taskservice.tenant'', true), '''')::uuid) WITH CHECK (tenant_id = nullif(current_setting(''taskservice.tenant'', true), '''')::uuid)', table_name);
    END LOOP;
END $$;

-- Even with a valid tenant context, the interactive role cannot read unscanned originals.
CREATE POLICY invoice_original_clean_for_api ON ts.invoice_files AS RESTRICTIVE FOR SELECT TO taskservice_api
    USING (EXISTS (
        SELECT 1 FROM ts.invoice_versions v
        WHERE v.tenant_id = invoice_files.tenant_id AND v.invoice_id = invoice_files.invoice_id
          AND v.version = invoice_files.version AND v.scan_status = 'clean'
    ));
