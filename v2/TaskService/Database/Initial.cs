using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261120000100_Initial")]
public sealed class Initial : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        using var source = typeof(Initial).Assembly.GetManifestResourceStream("TaskService.Schema")!;
        using var reader = new StreamReader(source);
        migration.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migration) =>
        throw new NotSupportedException("Restore to a separate database before rolling back the initial migration.");
}
