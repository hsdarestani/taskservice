using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261120000500_PlatformAdministration")]
public sealed class PlatformAdministrationMigration : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        using var source = typeof(PlatformAdministrationMigration).Assembly
            .GetManifestResourceStream("TaskService.PlatformAdministration")!;
        using var reader = new StreamReader(source);
        migration.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migration) =>
        throw new NotSupportedException("Restore a database backup to a separate database before reverting platform administration.");
}

public sealed partial class ServiceDb
{
    public DbSet<PlatformAudit> PlatformAudit => Set<PlatformAudit>();
    public DbSet<PlatformCommand> PlatformCommands => Set<PlatformCommand>();

    private static void ConfigurePlatform(ModelBuilder model)
    {
        model.Entity<PlatformAudit>().ToTable("platform_audit").HasKey(x => x.Id);
        model.Entity<PlatformCommand>().ToTable("platform_commands").HasKey(x => new { x.Actor, x.Operation, x.Key });
        model.Entity<Tenant>().Property(x => x.Revision).IsConcurrencyToken();
    }
}

public sealed record PlatformAudit(Guid Id, Guid TenantId, string Actor, string Action, string? Reason, DateTimeOffset RecordedAt);
public sealed record PlatformCommand(string Actor, string Operation, Guid Key, byte[] Fingerprint, string Response, int Revision, int Status);
