ALTER TABLE ts.appointments
    ADD COLUMN time_zone varchar(64) NOT NULL DEFAULT 'Europe/Berlin',
    ADD COLUMN updated_at timestamptz,
    ADD COLUMN customer_confirmed_at timestamptz,
    ADD COLUMN customer_note varchar(500);
ALTER TABLE ts.history ADD COLUMN private_note varchar(500);
