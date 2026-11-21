ALTER TABLE ts.requests
    ADD COLUMN service_category varchar(24) CHECK (service_category IN ('repair','cleaning','electric','garden','furniture','other')),
    ADD COLUMN contact_name varchar(120),
    ADD COLUMN contact_phone varchar(50);

CREATE TABLE ts.request_photos (
    tenant_id uuid NOT NULL,
    request_id uuid NOT NULL,
    id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal BETWEEN 1 AND 3),
    mime varchar(16) NOT NULL CHECK (mime IN ('image/png','image/jpeg','image/webp')),
    size integer NOT NULL CHECK (size BETWEEN 1 AND 350000),
    checksum bytea NOT NULL CHECK (octet_length(checksum) = 32),
    scan_status varchar(16) NOT NULL CHECK (scan_status IN ('pending','clean','quarantined','failed')),
    PRIMARY KEY (tenant_id, request_id, id),
    UNIQUE (tenant_id, request_id, ordinal),
    FOREIGN KEY (tenant_id, request_id) REFERENCES ts.requests (tenant_id, id)
);
CREATE TABLE ts.request_photo_files (
    tenant_id uuid NOT NULL,
    request_id uuid NOT NULL,
    id uuid NOT NULL,
    content bytea NOT NULL CHECK (octet_length(content) BETWEEN 1 AND 350000),
    PRIMARY KEY (tenant_id, request_id, id),
    FOREIGN KEY (tenant_id, request_id, id) REFERENCES ts.request_photos
);
DO $$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['request_photos','request_photo_files'] LOOP
        EXECUTE format('ALTER TABLE ts.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('ALTER TABLE ts.%I FORCE ROW LEVEL SECURITY', table_name);
        EXECUTE format('CREATE POLICY tenant_scope ON ts.%I USING (tenant_id = nullif(current_setting(''taskservice.tenant'', true), '''')::uuid) WITH CHECK (tenant_id = nullif(current_setting(''taskservice.tenant'', true), '''')::uuid)', table_name);
    END LOOP;
END $$;
CREATE POLICY request_photo_clean_for_api ON ts.request_photo_files AS RESTRICTIVE FOR SELECT TO taskservice_api
    USING (EXISTS (SELECT 1 FROM ts.request_photos p WHERE p.tenant_id = request_photo_files.tenant_id
        AND p.request_id = request_photo_files.request_id AND p.id = request_photo_files.id AND p.scan_status = 'clean'));
