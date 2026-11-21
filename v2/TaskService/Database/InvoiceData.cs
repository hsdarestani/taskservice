using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261120000300_Invoices")]
public sealed class InvoicesMigration : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        using var source = typeof(InvoicesMigration).Assembly.GetManifestResourceStream("TaskService.Invoices")!;
        using var reader = new StreamReader(source);
        migration.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migration) =>
        throw new NotSupportedException("Restore a database backup to a separate database before reverting invoices.");
}

public sealed partial class ServiceDb
{
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceVersion> InvoiceVersions => Set<InvoiceVersion>();
    public DbSet<InvoiceFile> InvoiceFiles => Set<InvoiceFile>();
    public DbSet<InvoicePermission> InvoicePermissions => Set<InvoicePermission>();

    private static void ConfigureInvoices(ModelBuilder model)
    {
        model.Entity<Invoice>().ToTable("invoices").HasKey(x => new { x.TenantId, x.Id });
        model.Entity<Invoice>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<InvoiceVersion>().ToTable("invoice_versions").HasKey(x => new { x.TenantId, x.InvoiceId, x.Version });
        model.Entity<InvoiceFile>().ToTable("invoice_files").HasKey(x => new { x.TenantId, x.InvoiceId, x.Version });
        model.Entity<InvoicePermission>().ToTable("invoice_permissions").HasKey(x => new { x.TenantId, x.MemberId, x.Permission });
    }
}

public sealed record Invoice(Guid TenantId, Guid RequestId, Guid Id)
{
    public int Revision { get; set; }
    public int CurrentVersion { get; set; }
    public int? PublishedVersion { get; set; }
}

public sealed record InvoiceVersion(Guid TenantId, Guid InvoiceId, int Version, Guid UploaderId, string OriginalName,
    string ContentType, int Size, byte[] Checksum, DateTimeOffset UploadedAt)
{
    public string ScanStatus { get; set; } = "pending";
    public DateTimeOffset? ScanAt { get; set; }
    public string Status { get; set; } = "uploaded";
    public Guid? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewReason { get; set; }
    public string? RecipientKey { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed record InvoiceFile(Guid TenantId, Guid InvoiceId, int Version, byte[] Content);
public sealed record InvoicePermission(Guid TenantId, Guid MemberId, string Permission);
