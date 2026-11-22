using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261121000900_NativeIdentity")]
public sealed class NativeIdentityMigration : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        using var source = typeof(NativeIdentityMigration).Assembly.GetManifestResourceStream("TaskService.NativeIdentity")!;
        using var reader = new StreamReader(source);
        migration.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migration) =>
        throw new NotSupportedException("Use the verified backup and rollback procedure.");
}

public sealed partial class ServiceDb
{
    public DbSet<NativeAccount> NativeAccounts => Set<NativeAccount>();
    public DbSet<NativeSession> NativeSessions => Set<NativeSession>();
    public DbSet<NativeAccountAudit> NativeAccountAudit => Set<NativeAccountAudit>();

    private static void ConfigureNativeIdentity(ModelBuilder model)
    {
        model.Entity<NativeAccount>().ToTable("native_accounts").HasKey(x => x.Id);
        model.Entity<NativeSession>().ToTable("native_sessions").HasKey(x => x.Id);
        model.Entity<NativeSession>().HasOne<NativeAccount>().WithMany().HasForeignKey(x => x.AccountId);
        model.Entity<NativeAccountAudit>().ToTable("native_account_audit").HasKey(x => x.Id);
        model.Entity<NativeAccountAudit>().HasOne<NativeAccount>().WithMany().HasForeignKey(x => x.AccountId);
    }
}

public sealed class NativeAccount
{
    public Guid Id { get; init; }
    public required string Email { get; init; }
    public required string PasswordHash { get; set; }
    public bool Workforce { get; init; }
    public bool PlatformAdmin { get; init; }
    public string? TotpCipher { get; set; }
    public long? LastTotpStep { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? LockUntil { get; set; }
}

public sealed record NativeSession(Guid Id, Guid AccountId, bool Workforce, DateTimeOffset ExpiresAt)
{
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed record NativeAccountAudit(Guid Id, Guid AccountId, string Action, DateTimeOffset RecordedAt);
