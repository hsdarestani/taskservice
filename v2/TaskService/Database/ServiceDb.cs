using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace TaskService.Database;

public sealed partial class ServiceDb(DbContextOptions<ServiceDb> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<ServiceRequest> Requests => Set<ServiceRequest>();
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<History> History => Set<History>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Audit> Audit => Set<Audit>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<Outbox> Outbox => Set<Outbox>();

    public async Task<IDbContextTransaction> ForTenant(Guid tenantId, CancellationToken ct)
    {
        var transaction = await Database.BeginTransactionAsync(ct);
        try
        {
            await Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('taskservice.tenant', {tenantId.ToString("D")}, true)", ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("ts");
        model.Entity<Tenant>().ToTable("tenants").HasKey(x => x.Id);
        model.Entity<Member>().ToTable("members").HasKey(x => new { x.TenantId, x.Id });
        model.Entity<ServiceRequest>().ToTable("requests").HasKey(x => new { x.TenantId, x.Id });
        model.Entity<ServiceRequest>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<Claim>().ToTable("claims").HasKey(x => new { x.TenantId, x.RequestId });
        model.Entity<Appointment>().ToTable("appointments").HasKey(x => new { x.TenantId, x.Id });
        model.Entity<History>().ToTable("history").HasKey(x => new { x.TenantId, x.RequestId, x.Revision });
        model.Entity<Message>().ToTable("messages").HasKey(x => new { x.TenantId, x.Sequence });
        model.Entity<Message>().Property(x => x.Sequence).ValueGeneratedOnAdd();
        model.Entity<Audit>().ToTable("audit").HasKey(x => new { x.TenantId, x.Id });
        model.Entity<Command>().ToTable("commands").HasKey(x => new { x.TenantId, x.Actor, x.Operation, x.Key });
        model.Entity<Outbox>().ToTable("outbox").HasKey(x => new { x.TenantId, x.Id });
        ConfigureReports(model);
        ConfigureInvoices(model);
        ConfigurePlatform(model);
        ConfigureIntake(model);
        foreach (var entity in model.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            property.SetColumnName(NameBoundary().Replace(property.Name, "_$1").ToLowerInvariant());
    }

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex NameBoundary();
}

public sealed record Tenant(Guid Id, string Slug, string Name, string Accent, bool Active)
{
    public int Revision { get; set; } = 1;
    public string Lifecycle { get; set; } = "active";
    public string Name { get; set; } = Name;
    public string Accent { get; set; } = Accent;
    public bool Active { get; set; } = Active;
}
public sealed record Member(Guid TenantId, Guid Id, string SubjectKey, string Name, string Role, bool Active);
public sealed class ServiceRequest
{
    public required Guid TenantId { get; init; }
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string ContactEmail { get; init; }
    public string? ServiceCategory { get; init; }
    public string? ContactName { get; init; }
    public string? ContactPhone { get; init; }
    public string? CustomerKey { get; set; }
    public Guid? TechnicianId { get; set; }
    public string State { get; set; } = "received";
    public string? ResumeState { get; set; }
    public int Revision { get; set; } = 1;
    public required DateTimeOffset CreatedAt { get; init; }
}
public sealed record Claim(Guid TenantId, Guid RequestId, byte[] Digest, DateTimeOffset ExpiresAt)
{
    public DateTimeOffset? ConsumedAt { get; set; }
}
public sealed record Appointment(Guid TenantId, Guid Id, Guid RequestId, Guid TechnicianId,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt)
{
    public bool Cancelled { get; set; }
    public string ArrivalStatus { get; set; } = "planned";
    public string TimeZone { get; init; } = "Europe/Berlin";
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? CustomerConfirmedAt { get; set; }
    public string? CustomerNote { get; set; }
}
public sealed record History(Guid TenantId, Guid RequestId, int Revision, string State, DateTimeOffset At)
{
    public string? PrivateNote { get; init; }
}
public sealed record Message(Guid TenantId, Guid? RequestId, string Channel, string Author, string Body, DateTimeOffset SentAt)
{
    public long Sequence { get; set; }
}
public sealed record Audit(Guid TenantId, Guid Id, string Actor, string Action, Guid ObjectId, DateTimeOffset At);
public sealed record Command(Guid TenantId, string Actor, string Operation, string Key, byte[] Fingerprint,
    Guid ResourceId, string Response, string? Location, int? Revision, int Status);
public sealed record Outbox(Guid TenantId, Guid Id, string Kind, string Payload, DateTimeOffset AvailableAt)
{
    public DateTimeOffset AvailableAt { get; set; } = AvailableAt;
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeasedUntil { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DeadAt { get; set; }
}
