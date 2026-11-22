CREATE TABLE ts.report_policy (
    tenant_id uuid PRIMARY KEY REFERENCES ts.tenants,
    independent_reviewer boolean NOT NULL DEFAULT true
);

CREATE TABLE ts.report_permissions (
    tenant_id uuid NOT NULL,
    member_id uuid NOT NULL,
    permission varchar(24) NOT NULL CHECK (permission IN ('report.create', 'report.approve', 'report.publish')),
    PRIMARY KEY (tenant_id, member_id, permission),
    FOREIGN KEY (tenant_id, member_id) REFERENCES ts.members (tenant_id, id)
);

CREATE TABLE ts.reports (
    tenant_id uuid NOT NULL,
    request_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    current_version integer NOT NULL CHECK (current_version > 0),
    published_version integer CHECK (published_version BETWEEN 1 AND current_version),
    PRIMARY KEY (tenant_id, request_id),
    FOREIGN KEY (tenant_id, request_id) REFERENCES ts.requests (tenant_id, id)
);

CREATE TABLE ts.report_versions (
    tenant_id uuid NOT NULL,
    request_id uuid NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    author_id uuid NOT NULL,
    activities varchar(18000) NOT NULL CHECK (jsonb_typeof(activities::jsonb) = 'array'),
    result varchar(3000) NOT NULL,
    materials varchar(2000),
    submitted_at timestamptz NOT NULL,
    status varchar(16) NOT NULL CHECK (status IN ('submitted', 'rejected', 'approved', 'published', 'superseded', 'revoked')),
    review_reason varchar(500),
    reviewer_id uuid,
    reviewed_at timestamptz,
    recipient_key varchar(64),
    published_at timestamptz,
    PRIMARY KEY (tenant_id, request_id, version),
    FOREIGN KEY (tenant_id, request_id) REFERENCES ts.reports (tenant_id, request_id),
    FOREIGN KEY (tenant_id, author_id) REFERENCES ts.members (tenant_id, id),
    FOREIGN KEY (tenant_id, reviewer_id) REFERENCES ts.members (tenant_id, id),
    CHECK ((reviewer_id IS NULL) = (reviewed_at IS NULL)),
    CHECK ((recipient_key IS NULL) = (published_at IS NULL)),
    CHECK ((status IN ('published', 'superseded', 'revoked')) = (published_at IS NOT NULL))
);

ALTER TABLE ts.reports ADD CONSTRAINT reports_current_version
    FOREIGN KEY (tenant_id, request_id, current_version) REFERENCES ts.report_versions
    DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE ts.reports ADD CONSTRAINT reports_published_version
    FOREIGN KEY (tenant_id, request_id, published_version) REFERENCES ts.report_versions
    DEFERRABLE INITIALLY DEFERRED;
CREATE UNIQUE INDEX report_versions_one_published ON ts.report_versions (tenant_id, request_id) WHERE status = 'published';

CREATE TABLE ts.report_files (
    tenant_id uuid NOT NULL,
    request_id uuid NOT NULL,
    version integer NOT NULL,
    company_name varchar(120) NOT NULL,
    checksum bytea NOT NULL CHECK (octet_length(checksum) = 32),
    content bytea NOT NULL CHECK (octet_length(content) BETWEEN 1 AND 1048576),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, request_id, version),
    FOREIGN KEY (tenant_id, request_id, version) REFERENCES ts.report_versions
);

DO $$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['report_policy','report_permissions','reports','report_versions','report_files'] LOOP
        EXECUTE format('ALTER TABLE ts.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('ALTER TABLE ts.%I FORCE ROW LEVEL SECURITY', table_name);
        EXECUTE format(
            'CREATE POLICY tenant_scope ON ts.%I USING (tenant_id = nullif(current_setting(''taskservice.tenant'', true), '''')::uuid) WITH CHECK (tenant_id = nullif(current_setting(''taskservice.tenant'', true), '''')::uuid)', table_name);
    END LOOP;
END $$;
