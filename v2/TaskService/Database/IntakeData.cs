using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TaskService.Database;

[DbContext(typeof(ServiceDb))]
[Migration("20261121000600_RequestIntake")]
public sealed class RequestIntakeMigration : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        using var stream = typeof(RequestIntakeMigration).Assembly.GetManifestResourceStream("TaskService.RequestIntake")!;
        using var reader = new StreamReader(stream);
        migration.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migration) =>
        throw new NotSupportedException("Restore a database backup to a separate database before reverting request intake.");
}

public sealed partial class ServiceDb
{
    public DbSet<RequestPhoto> RequestPhotos => Set<RequestPhoto>();
    public DbSet<RequestPhotoFile> RequestPhotoFiles => Set<RequestPhotoFile>();

    private static void ConfigureIntake(ModelBuilder model)
    {
        model.Entity<RequestPhoto>().ToTable("request_photos").HasKey(x => new { x.TenantId, x.RequestId, x.Id });
        model.Entity<RequestPhotoFile>().ToTable("request_photo_files").HasKey(x => new { x.TenantId, x.RequestId, x.Id });
        model.Entity<RequestPhotoFile>().HasOne<RequestPhoto>().WithOne()
            .HasForeignKey<RequestPhotoFile>(x => new { x.TenantId, x.RequestId, x.Id });
    }
}

public sealed record RequestPhoto(Guid TenantId, Guid RequestId, Guid Id, int Ordinal, string Mime, int Size, byte[] Checksum)
{
    public string ScanStatus { get; set; } = "pending";
}
public sealed record RequestPhotoFile(Guid TenantId, Guid RequestId, Guid Id, byte[] Content);
