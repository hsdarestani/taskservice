using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261127001100_ServiceForms")]
public sealed class ServiceFormsMigration : Migration
{
    protected override void Up(MigrationBuilder migration) => migration.Sql("""
        ALTER TABLE ts.tenants ADD COLUMN service_forms_json text NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(service_forms_json::jsonb) = 'object');
        ALTER TABLE ts.requests ADD COLUMN intake_answers_json text NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(intake_answers_json::jsonb) = 'array');
        GRANT UPDATE (service_forms_json) ON ts.tenants TO taskservice_platform;
        """);
    protected override void Down(MigrationBuilder migration) => migration.Sql("""
        LOCK TABLE ts.tenants, ts.requests IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS (SELECT FROM ts.tenants WHERE service_forms_json <> '{}') OR
               EXISTS (SELECT FROM ts.requests WHERE intake_answers_json <> '[]') THEN
                RAISE EXCEPTION 'Preserve configured forms and historical answers before reverting.';
            END IF;
        END $$;
        ALTER TABLE ts.requests DROP COLUMN intake_answers_json;
        ALTER TABLE ts.tenants DROP COLUMN service_forms_json;
        """);
}
