CREATE TABLE ts.native_accounts (
    id uuid PRIMARY KEY,
    email varchar(254) NOT NULL UNIQUE,
    password_hash text NOT NULL,
    workforce boolean NOT NULL DEFAULT false,
    platform_admin boolean NOT NULL DEFAULT false,
    totp_cipher text,
    last_totp_step bigint,
    failures integer NOT NULL DEFAULT 0 CHECK (failures >= 0),
    lock_until timestamptz,
    CHECK (NOT platform_admin OR (workforce AND totp_cipher IS NOT NULL)),
    CHECK (workforce OR totp_cipher IS NULL)
);
CREATE TABLE ts.native_sessions (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES ts.native_accounts(id) ON DELETE CASCADE,
    workforce boolean NOT NULL,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz
);
CREATE INDEX native_sessions_account ON ts.native_sessions(account_id,expires_at);
CREATE INDEX native_sessions_expiry ON ts.native_sessions(expires_at);
CREATE TABLE ts.native_account_audit (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES ts.native_accounts(id),
    action varchar(24) NOT NULL,
    recorded_at timestamptz NOT NULL
);

GRANT SELECT ON ts.native_accounts TO taskservice_api;
GRANT INSERT (id,email,password_hash), UPDATE (password_hash,last_totp_step,failures,lock_until)
    ON ts.native_accounts TO taskservice_api;
GRANT SELECT, INSERT, DELETE, UPDATE (revoked_at) ON ts.native_sessions TO taskservice_api;

CREATE FUNCTION ts.hold_demo_mail() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $body$
BEGIN
    IF NEW.kind = 'mail' AND current_setting('taskservice.mail_held', true) = 'on' THEN
        NEW.kind := 'mail_held';
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER hold_demo_mail BEFORE INSERT ON ts.outbox
    FOR EACH ROW EXECUTE FUNCTION ts.hold_demo_mail();
