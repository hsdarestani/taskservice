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
