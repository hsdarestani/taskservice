using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261127001000_ElectricalFeatures")]
public sealed class ElectricalFeatureMigration : Migration
{
    protected override void Up(MigrationBuilder migration) => migration.Sql("""
        ALTER TABLE ts.tenants ADD COLUMN feature_overrides text[] NOT NULL DEFAULT '{}';
        CREATE TABLE ts.feature_plans (
            code varchar(20) PRIMARY KEY,
            withheld text[] NOT NULL DEFAULT '{}',
            revision integer NOT NULL DEFAULT 1 CHECK (revision > 0)
        );
        INSERT INTO ts.feature_plans(code) VALUES ('global'), ('custom'), ('essential'), ('professional'), ('enterprise');
        ALTER TABLE ts.tenants ADD COLUMN plan_code varchar(20) NOT NULL DEFAULT 'custom'
            REFERENCES ts.feature_plans(code) CHECK (plan_code <> 'global');
        CREATE TABLE ts.feature_policy_history (
            scope varchar(20) NOT NULL REFERENCES ts.feature_plans(code), revision integer NOT NULL,
            actor varchar(64) NOT NULL, withheld text[] NOT NULL, recorded_at timestamptz NOT NULL,
            PRIMARY KEY (scope, revision)
        );
        GRANT SELECT ON ts.feature_plans TO taskservice_api, taskservice_platform;
        GRANT UPDATE (withheld, revision) ON ts.feature_plans TO taskservice_platform;
        GRANT SELECT, INSERT ON ts.feature_policy_history TO taskservice_platform;
        GRANT UPDATE (feature_overrides, plan_code) ON ts.tenants TO taskservice_platform;
        ALTER TABLE ts.requests DROP CONSTRAINT requests_service_category_check;
        ALTER TABLE ts.requests ADD CONSTRAINT requests_service_category_check
            CHECK (service_category ~ '^[a-z][a-z0-9-]{0,23}$') NOT VALID;
        ALTER TABLE ts.requests VALIDATE CONSTRAINT requests_service_category_check;
        """);

    protected override void Down(MigrationBuilder migration) => migration.Sql("""
        LOCK TABLE ts.tenants, ts.requests IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS (SELECT FROM ts.tenants WHERE cardinality(feature_overrides) > 0) THEN
                RAISE EXCEPTION 'Export and clear company overrides before reverting.';
            END IF;
        END $$;
        ALTER TABLE ts.requests DROP CONSTRAINT requests_service_category_check;
        ALTER TABLE ts.requests ADD CONSTRAINT requests_service_category_check
            CHECK (service_category IN ('repair','cleaning','electric','garden','furniture','other'));
        DO $$ BEGIN
            IF EXISTS (SELECT FROM ts.feature_policy_history) OR EXISTS (SELECT FROM ts.tenants WHERE plan_code <> 'custom') THEN
                RAISE EXCEPTION 'Archive policy history and plan assignments before reverting.';
            END IF;
        END $$;
        ALTER TABLE ts.tenants DROP COLUMN feature_overrides, DROP COLUMN plan_code;
        DROP TABLE ts.feature_policy_history, ts.feature_plans;
        """);
}
