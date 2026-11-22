using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261121000700_TeamAccess")]
public sealed class TeamAccessMigration : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        using var source = typeof(TeamAccessMigration).Assembly.GetManifestResourceStream("TaskService.TeamAccess")!;
        using var reader = new StreamReader(source);
        migration.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migration) =>
        throw new NotSupportedException("Restore a database backup to a separate database before reverting team access.");
}

public sealed partial class ServiceDb
{
    public DbSet<MemberInvite> MemberInvites => Set<MemberInvite>();

    private static void ConfigureTeam(ModelBuilder model)
    {
        model.Entity<MemberInvite>().ToTable("member_invites").HasKey(x => new { x.TenantId, x.Id });
        model.Entity<Member>().Property(x => x.Revision).IsConcurrencyToken();
    }
}

public sealed record MemberInvite(Guid TenantId, Guid Id, string Name, string Email, string Role, byte[] Digest,
    DateTimeOffset ExpiresAt)
{
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
