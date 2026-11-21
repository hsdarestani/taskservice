using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private static readonly ServiceCategory[] Catalog =
    [
        new("repair", "Reparaturen", "Kleine Reparaturen", "Wasserhahn, Türen, Ausbesserungen", "Reparatur & Montage", true),
        new("cleaning", "Reinigung", "Wohnungsreinigung", "Wohnung, Haushalt, Fenster", "Reinigung & Haushalt", true),
        new("electric", "Elektro & Sanitär", "Elektro & Sanitär", "Licht, Steckdose, Wasseranschluss", "Haustechnik", false),
        new("garden", "Haus & Garten", "Gartenpflege", "Rasen, Hecke, Außenbereich", "Haus & Garten", true),
        new("furniture", "Möbelmontage", "Möbelmontage", "Schrank, Regal, Aufbau", "Reparatur & Montage", false),
        new("other", "Weitere Services", "Sonstige Arbeiten", "Individuelle Unterstützung", "Weitere Services", false)
    ];

    private static async Task<IResult> ReadCatalog(ServiceDb db, string slug, CancellationToken ct) =>
        await FindTenant(db, slug, ct) is null ? ApiProblem.NotFound() : Results.Ok(Catalog);

    private static bool ValidIntake(NewRequest body)
    {
        // Older clients can still create the earlier three-field request.
        if (body is { Category: null, Name: null, Phone: null, Photos: null })
            return true;
        if (!Catalog.Any(x => x.Id == body.Category) || !Valid(body.Name, 120) || body.Name!.Trim().Length < 2 ||
            body.Name.Any(char.IsControl) || !Valid(body.Phone, 50) || body.Phone!.Trim().Length < 5 ||
            body.Phone.Count(char.IsAsciiDigit) < 5 || body.Phone.Any(c => !char.IsAsciiDigit(c) && !"+-()/ .".Contains(c)) ||
            body.Title.Trim().Length < 3 || body.Description.Trim().Length < 10 || body.Photos is { Length: > 3 })
            return false;
        return body.Photos is null || body.Photos.All(x => x is { Content.Length: > 0 and <= 350000 } &&
            IsPhoto(x.Mime, x.Content));
    }

    private static bool IsPhoto(string mime, ReadOnlySpan<byte> bytes) => mime switch
    {
        "image/png" => bytes.Length >= 33 && bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes[12..].StartsWith("IHDR"u8),
        "image/jpeg" => bytes.Length >= 12 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 &&
            bytes[^2] == 255 && bytes[^1] == 217,
        "image/webp" => bytes.Length >= 20 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8),
        _ => false
    };

    private static void QueuePhotos(ServiceDb db, ServiceRequest request, NewRequestPhoto?[]? photos)
    {
        if (photos is null)
            return;
        for (var index = 0; index < photos.Length; index++)
        {
            var photo = photos[index]!;
            var id = Guid.NewGuid();
            db.RequestPhotos.Add(new(request.TenantId, request.Id, id, index + 1, photo.Mime, photo.Content.Length,
                SHA256.HashData(photo.Content)));
            db.RequestPhotoFiles.Add(new(request.TenantId, request.Id, id, photo.Content));
            db.Outbox.Add(new(request.TenantId, Guid.NewGuid(), "request_photo_scan",
                JsonSerializer.Serialize(new PhotoScanJob(request.Id, id), Json), DateTimeOffset.UtcNow));
        }
    }

    private static async Task<IResult> DownloadPhoto(ServiceDb db, string slug, Guid id, Guid photoId, HttpContext http,
        CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request) ||
            await db.RequestPhotos.FindAsync([tenant.Id, id, photoId], ct) is not { ScanStatus: "clean" } photo ||
            await db.RequestPhotoFiles.FindAsync([tenant.Id, id, photoId], ct) is not { } file ||
            file.Content.Length != photo.Size || !CryptographicOperations.FixedTimeEquals(photo.Checksum, SHA256.HashData(file.Content)))
            return ApiProblem.NotFound();

        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, "request.photo.download", photoId, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var extension = photo.Mime == "image/jpeg" ? "jpg" : photo.Mime["image/".Length..];
        return Results.File(file.Content, photo.Mime, $"Anfragefoto-{photo.Ordinal}.{extension}");
    }
}

public sealed record ServiceCategory(string Id, string Label, string Title, string SearchTerms, string Group, bool Featured)
{
    public string Image => $"/services/{Id}.webp";
}
public sealed record NewRequestPhoto(string Mime, byte[] Content);
public sealed record PhotoScanJob(Guid RequestId, Guid PhotoId);

public static class RequestPhotoScan
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<bool> ProcessNext(ServiceDb db, Guid tenant, InvoiceScanner scanner, ILogger logger, CancellationToken ct)
    {
        await using var tx = await db.ForTenant(tenant, ct);
        var jobs = await db.Outbox.FromSqlInterpolated($"""
            SELECT * FROM ts.outbox WHERE tenant_id = {tenant} AND kind = 'request_photo_scan'
              AND completed_at IS NULL AND dead_at IS NULL AND available_at <= now()
            ORDER BY available_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (jobs.SingleOrDefault() is not { } job)
            return false;

        job.Attempts++;
        RequestPhoto? photo = null;
        try
        {
            var payload = JsonSerializer.Deserialize<PhotoScanJob>(job.Payload, Json)
                ?? throw new InvalidOperationException("Invalid request photo job.");
            photo = await db.RequestPhotos.FindAsync([tenant, payload.RequestId, payload.PhotoId], ct)
                ?? throw new InvalidOperationException("Request photo is missing.");
            if (photo.ScanStatus == "pending")
            {
                var file = await db.RequestPhotoFiles.FindAsync([tenant, payload.RequestId, payload.PhotoId], ct);
                var intact = file is not null && photo.Size == file.Content.Length &&
                    CryptographicOperations.FixedTimeEquals(photo.Checksum, SHA256.HashData(file.Content));
                photo.ScanStatus = intact && await scanner.IsClean(file!.Content, ct) ? "clean" : "quarantined";
                db.Audit.Add(new(tenant, Guid.NewGuid(), "scanner", $"request.photo.{photo.ScanStatus}", photo.Id, DateTimeOffset.UtcNow));
            }
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (job.Attempts >= 8)
            {
                job.DeadAt = now;
                if (photo?.ScanStatus == "pending")
                {
                    photo.ScanStatus = "failed";
                    db.Audit.Add(new(tenant, Guid.NewGuid(), "scanner", "request.photo.failed", photo.Id, now));
                }
            }
            else
                job.AvailableAt = now.AddSeconds(Math.Min(1800, 10 * Math.Pow(2, job.Attempts)) + Random.Shared.Next(10));
            logger.LogWarning("Request photo scan job {JobId} failed on attempt {Attempt} ({ErrorType})", job.Id, job.Attempts, ex.GetType().Name);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
