ALTER TABLE ts.members ADD COLUMN revision integer NOT NULL DEFAULT 1 CHECK (revision > 0);

CREATE TABLE ts.member_invites (
    tenant_id uuid NOT NULL REFERENCES ts.tenants,
    id uuid NOT NULL,
    name varchar(120) NOT NULL,
    email varchar(254) NOT NULL,
    role varchar(24) NOT NULL CHECK (role IN ('admin','dispatcher','technician')),
    digest bytea NOT NULL CHECK (octet_length(digest) = 32),
    expires_at timestamptz NOT NULL,
    used_at timestamptz,
    revoked_at timestamptz,
    PRIMARY KEY (tenant_id,id),
    CHECK (used_at IS NULL OR revoked_at IS NULL)
);
CREATE UNIQUE INDEX member_invites_pending ON ts.member_invites (tenant_id,email)
    WHERE used_at IS NULL AND revoked_at IS NULL;
ALTER TABLE ts.member_invites ENABLE ROW LEVEL SECURITY;
ALTER TABLE ts.member_invites FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_scope ON ts.member_invites USING (tenant_id = nullif(current_setting('taskservice.tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('taskservice.tenant', true), '')::uuid);

-- The platform role can check onboarding and issue the first invitation without reading employee records.
CREATE FUNCTION ts.initial_admin_state(target uuid) RETURNS text
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $function$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM ts.tenants WHERE id = target AND active) THEN
        RETURN 'inactive';
    END IF;

    PERFORM pg_catalog.set_config('taskservice.tenant',target::text,true);
    IF EXISTS (SELECT 1 FROM ts.members WHERE tenant_id = target AND role = 'admin' AND active) THEN
        RETURN 'active';
    END IF;
    IF EXISTS (SELECT 1 FROM ts.member_invites WHERE tenant_id = target AND role = 'admin'
        AND used_at IS NULL AND revoked_at IS NULL AND expires_at > pg_catalog.now()) THEN
        RETURN 'pending';
    END IF;
    RETURN 'missing';
END;
$function$;

CREATE FUNCTION ts.issue_initial_admin(target uuid, invitation uuid, display_name text, address text,
    digest bytea, expires timestamptz, secured_message text, operator_key text) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $function$
BEGIN
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(target::text || ':team', 0));
    IF ts.initial_admin_state(target) <> 'missing' THEN
        RETURN false;
    END IF;

    UPDATE ts.member_invites SET revoked_at = pg_catalog.now()
        WHERE tenant_id = target AND used_at IS NULL AND revoked_at IS NULL AND expires_at <= pg_catalog.now()
          AND (role = 'admin' OR email = address);
    INSERT INTO ts.member_invites (tenant_id,id,name,email,role,digest,expires_at)
        VALUES (target,invitation,display_name,address,'admin',digest,expires);
    INSERT INTO ts.outbox (tenant_id,id,kind,payload,available_at,attempts)
        VALUES (target,invitation,'mail',secured_message,pg_catalog.now(),0);
    INSERT INTO ts.platform_audit (id,tenant_id,actor,action,recorded_at)
        VALUES (invitation,target,operator_key,'initial_admin_invited',pg_catalog.now());
    RETURN true;
END;
$function$;

REVOKE ALL ON FUNCTION ts.initial_admin_state(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION ts.issue_initial_admin(uuid,uuid,text,text,bytea,timestamptz,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ts.initial_admin_state(uuid) TO taskservice_platform;
GRANT EXECUTE ON FUNCTION ts.issue_initial_admin(uuid,uuid,text,text,bytea,timestamptz,text,text) TO taskservice_platform;
