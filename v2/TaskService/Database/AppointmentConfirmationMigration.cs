using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261120000400_AppointmentConfirmation")]
public sealed class AppointmentConfirmationMigration : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        using var source = typeof(AppointmentConfirmationMigration).Assembly
            .GetManifestResourceStream("TaskService.AppointmentConfirmation")!;
        using var reader = new StreamReader(source);
        migration.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migration) =>
        throw new NotSupportedException("Restore a database backup to a separate database before reverting appointments.");
}
