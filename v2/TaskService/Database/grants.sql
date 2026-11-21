-- Created externally before the migration. No passwords belong in this file.
-- The runtime and worker must log in as these roles, neither owning tables nor bypassing RLS.
GRANT USAGE ON SCHEMA ts TO taskservice_api, taskservice_worker;
GRANT SELECT ON ts.tenants TO taskservice_api, taskservice_worker;
GRANT SELECT ON ts.members TO taskservice_api;
GRANT SELECT, INSERT, UPDATE ON ts.requests, ts.claims, ts.appointments TO taskservice_api;
GRANT SELECT, INSERT ON ts.history, ts.messages, ts.commands TO taskservice_api;
GRANT INSERT ON ts.audit, ts.outbox TO taskservice_api;
GRANT USAGE ON SEQUENCE ts.messages_sequence_seq TO taskservice_api;
GRANT SELECT, UPDATE ON ts.outbox TO taskservice_worker;
GRANT SELECT ON ts.report_permissions, ts.report_policy, ts.report_files TO taskservice_api;
GRANT SELECT, INSERT ON ts.reports, ts.report_versions TO taskservice_api;
GRANT UPDATE (revision, current_version, published_version) ON ts.reports TO taskservice_api;
GRANT UPDATE (status, review_reason, reviewer_id, reviewed_at, recipient_key, published_at)
    ON ts.report_versions TO taskservice_api;
GRANT SELECT ON ts.report_versions, ts.report_files TO taskservice_worker;
GRANT INSERT ON ts.report_files TO taskservice_worker;
GRANT SELECT ON ts.invoice_permissions TO taskservice_api;
GRANT SELECT, INSERT ON ts.invoices, ts.invoice_versions, ts.invoice_files TO taskservice_api;
GRANT UPDATE (revision, current_version, published_version) ON ts.invoices TO taskservice_api;
GRANT UPDATE (status, review_reason, reviewer_id, reviewed_at, recipient_key, published_at)
    ON ts.invoice_versions TO taskservice_api;
GRANT SELECT ON ts.invoice_versions, ts.invoice_files TO taskservice_worker;
GRANT UPDATE (scan_status, scan_at) ON ts.invoice_versions TO taskservice_worker;
GRANT INSERT ON ts.audit TO taskservice_worker;
GRANT USAGE ON SCHEMA ts TO taskservice_platform;
GRANT SELECT, INSERT ON ts.tenants TO taskservice_platform;
GRANT UPDATE (name, accent, active, lifecycle, revision) ON ts.tenants TO taskservice_platform;
GRANT SELECT, INSERT ON ts.platform_audit, ts.platform_commands TO taskservice_platform;
GRANT SELECT, INSERT ON ts.request_photos, ts.request_photo_files TO taskservice_api;
GRANT SELECT ON ts.request_photos, ts.request_photo_files TO taskservice_worker;
GRANT UPDATE (scan_status) ON ts.request_photos TO taskservice_worker;
GRANT SELECT, INSERT ON ts.member_invites TO taskservice_api;
GRANT UPDATE (used_at, revoked_at) ON ts.member_invites TO taskservice_api;
GRANT INSERT ON ts.members TO taskservice_api;
GRANT UPDATE (active, revision) ON ts.members TO taskservice_api;
GRANT INSERT, DELETE ON ts.report_permissions, ts.invoice_permissions TO taskservice_api;
GRANT SELECT, INSERT ON ts.integration_inbox TO taskservice_api;
