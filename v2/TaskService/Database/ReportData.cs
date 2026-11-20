using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261120000200_Reports")]
public sealed class ReportsMigration : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        using var source = typeof(ReportsMigration).Assembly.GetManifestResourceStream("TaskService.Reports")!;
        using var reader = new StreamReader(source);
        migration.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migration) =>
        throw new NotSupportedException("Restore a database backup to a separate database before reverting reports.");
}

public sealed partial class ServiceDb
{
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportVersion> ReportVersions => Set<ReportVersion>();
    public DbSet<ReportPermission> ReportPermissions => Set<ReportPermission>();
    public DbSet<ReportPolicy> ReportPolicies => Set<ReportPolicy>();
    public DbSet<ReportFile> ReportFiles => Set<ReportFile>();

    private static void ConfigureReports(ModelBuilder model)
    {
        model.Entity<Report>().ToTable("reports").HasKey(x => new { x.TenantId, x.RequestId });
        model.Entity<Report>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<ReportPermission>().ToTable("report_permissions").HasKey(x => new { x.TenantId, x.MemberId, x.Permission });
        model.Entity<ReportPolicy>().ToTable("report_policy").HasKey(x => x.TenantId);
        model.Entity<ReportVersion>().ToTable("report_versions").HasKey(x => new { x.TenantId, x.RequestId, x.Version });
        model.Entity<ReportFile>().ToTable("report_files").HasKey(x => new { x.TenantId, x.RequestId, x.Version });
    }
}

public sealed record Report(Guid TenantId, Guid RequestId)
{
    public int Revision { get; set; }
    public int CurrentVersion { get; set; }
    public int? PublishedVersion { get; set; }
}

public sealed record ReportVersion(Guid TenantId, Guid RequestId, int Version, Guid AuthorId,
    string Activities, string Result, string? Materials, DateTimeOffset SubmittedAt)
{
    public string Status { get; set; } = "submitted";
    public string? ReviewReason { get; set; }
    public Guid? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? RecipientKey { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed record ReportPolicy(Guid TenantId, bool IndependentReviewer);
public sealed record ReportPermission(Guid TenantId, Guid MemberId, string Permission);
public sealed record ReportFile(Guid TenantId, Guid RequestId, int Version, string CompanyName,
    byte[] Checksum, byte[] Content, DateTimeOffset CreatedAt);
