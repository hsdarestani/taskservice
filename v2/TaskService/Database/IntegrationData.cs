using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261121000800_IntegrationInbox")]
public sealed class IntegrationInboxMigration : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        using var source = typeof(IntegrationInboxMigration).Assembly.GetManifestResourceStream("TaskService.IntegrationInbox")!;
        using var reader = new StreamReader(source);
        migration.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migration) =>
        throw new NotSupportedException("Restore a database backup to a separate database before reverting integration intake.");
}

public sealed partial class ServiceDb
{
    public DbSet<IntegrationEvent> IntegrationInbox => Set<IntegrationEvent>();

    private static void ConfigureIntegration(ModelBuilder model) =>
        model.Entity<IntegrationEvent>().ToTable("integration_inbox").HasKey(x => new { x.TenantId, x.ConnectionId, x.EventId });
}

public sealed record IntegrationEvent(Guid TenantId, Guid ConnectionId, Guid EventId, string EventType, int SchemaVersion,
    Guid ResourceId, int Revision, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, byte[] Checksum, byte[] Payload);
