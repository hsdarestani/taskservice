using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private const int MaxWebhookBytes = 131072;
    private static readonly JsonSerializerOptions WebhookJson = new(JsonSerializerDefaults.Web) { MaxDepth = 18 };

    private static void MapIntegrationInbox(WebApplication app, RouteGroupBuilder tenant)
    {
        // This external endpoint authenticates the original bytes independently of browser sessions.
        app.MapPost("/api/v1/inbound/{connectionId:guid}", ReceiveWebhook)
            .RequireRateLimiting("incoming-webhook").WithMetadata(new RequestSizeLimitAttribute(MaxWebhookBytes));
        tenant.MapGet("/integrations/inbox", ReadIntegrationInbox);
    }

    private static async Task<IResult> ReceiveWebhook(ServiceDb db, Guid connectionId, HttpContext http,
        IConfiguration config, CancellationToken ct)
    {
        var definition = config.GetSection($"Integration:Connections:{connectionId:D}");
        var slug = definition["TenantSlug"];
        if (slug is not { Length: > 0 and <= 63 } || !definition.GetValue<bool>("Enabled"))
            return ApiProblem.Denied();

        var eventId = http.Request.Headers["webhook-id"].ToString();
        var timestamp = http.Request.Headers["webhook-timestamp"].ToString();
        var signatureHeader = http.Request.Headers["webhook-signature"].ToString();
        var now = DateTimeOffset.UtcNow;
        if (!Guid.TryParseExact(eventId, "D", out var id) ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) ||
            Math.Abs(now.ToUnixTimeSeconds() - (decimal)unix) > 300 || signatureHeader is not { Length: > 0 and <= 512 })
            return ApiProblem.Denied();

        if (!http.Request.HasJsonContentType())
            return ApiProblem.Error(415, "invalid_webhook_type", "Ein JSON-Ereignis ist erforderlich.");
        if (http.Request.ContentLength is > MaxWebhookBytes)
            return ApiProblem.Error(413, "webhook_too_large", "Das Ereignis ist zu groß.");
        var raw = await ReadWebhookBody(http.Request.Body, ct);
        if (raw is null)
            return ApiProblem.Error(413, "webhook_too_large", "Das Ereignis ist zu groß.");
        if (!ValidWebhookSignature(definition, eventId, timestamp, raw, signatureHeader))
            return ApiProblem.Denied();

        ExternalEvent? body;
        try { body = JsonSerializer.Deserialize<ExternalEvent>(raw, WebhookJson); }
        catch (JsonException) { body = null; }
        if (body is null || body.Id != id || body.Tenant != slug || body.ResourceId == Guid.Empty || body.SchemaVersion != 1 ||
            body.Revision < 1 || body.Type is not { Length: > 0 and <= 80 } ||
            body.Type.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) ||
            body.OccurredAt == default || body.Data.ValueKind != JsonValueKind.Object)
            return ApiProblem.Error(422, "invalid_webhook_event", "Das Ereignis entspricht nicht dem vereinbarten Schema.");

        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.Denied();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        await Lock(db, $"{tenant.Id}:inbound:{connectionId}", ct);
        var checksum = SHA256.HashData(raw);
        if (await db.IntegrationInbox.FindAsync([tenant.Id, connectionId, id], ct) is { } previous)
        {
            if (!CryptographicOperations.FixedTimeEquals(previous.Checksum, checksum))
                return ApiProblem.Error(409, "webhook_event_conflict", "Die Ereignis-ID gehört bereits zu einer anderen Nutzlast.");
            return Results.Ok(new { id, state = "duplicate" });
        }

        if (await db.IntegrationInbox.AnyAsync(x => x.TenantId == tenant.Id && x.ConnectionId == connectionId &&
            x.ResourceId == body.ResourceId && x.Revision >= body.Revision, ct))
            return ApiProblem.Error(409, "webhook_revision_old", "Eine gleiche oder neuere Revision wurde bereits erfasst.");

        db.IntegrationInbox.Add(new(tenant.Id, connectionId, id, body.Type, body.SchemaVersion, body.ResourceId,
            body.Revision, body.OccurredAt.ToUniversalTime(), now, checksum, raw));
        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), Actor.SubjectKey("taskservice-integration", connectionId.ToString("D")),
            "integration.inbox.received", id, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Json(new { id, state = "received" }, statusCode: 202);
    }

    private static async Task<IResult> ReadIntegrationInbox(ServiceDb db, string slug, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        if ((await Actor.Read(db, tenant, http, ct)).Membership?.Role != "admin")
            return ApiProblem.Denied();
        var events = await db.IntegrationInbox.AsNoTracking().Where(x => x.TenantId == tenant.Id)
            .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.EventId).Take(50)
            .Select(x => new { x.EventId, x.EventType, x.Revision, x.ReceivedAt }).ToArrayAsync(ct);
        return Results.Ok(events);
    }

    private static bool ValidWebhookSignature(IConfiguration definition, string id, string timestamp, byte[] body, string header)
    {
        var prefix = Encoding.ASCII.GetBytes($"{id}.{timestamp}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed, prefix.Length);
        var candidates = header.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (candidates.Length is < 1 or > 6)
            return false;
        var provided = candidates.Where(x => x.StartsWith("v1,", StringComparison.Ordinal))
            .Select(x => Decode(x[3..], 32, 32)).OfType<byte[]>().ToArray();

        var valid = false;
        foreach (var key in definition.GetSection("Keys").GetChildren().Take(4))
        {
            var secret = key.Value;
            if (secret is null || !secret.StartsWith("whsec_", StringComparison.Ordinal) ||
                Decode(secret[6..], 24, 64) is not { } bytes)
                continue;
            var expected = HMACSHA256.HashData(bytes, signed);
            foreach (var signature in provided)
                valid |= CryptographicOperations.FixedTimeEquals(signature, expected);
        }
        return valid;
    }

    private static byte[]? Decode(string encoded, int min, int max)
    {
        if (encoded.Length > 4 * ((max + 2) / 3))
            return null;
        Span<byte> buffer = stackalloc byte[max];
        return Convert.TryFromBase64String(encoded, buffer, out var length) && length >= min
            ? buffer[..length].ToArray()
            : null;
    }

    private static async Task<byte[]?> ReadWebhookBody(Stream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, 8192), ct)) > 0)
            {
                if (body.Length + read > MaxWebhookBytes)
                    return null;
                body.Write(buffer, 0, read);
            }
            return body.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

public sealed record ExternalEvent(Guid Id, string Type, int SchemaVersion, DateTimeOffset OccurredAt,
    string Tenant, Guid ResourceId, int Revision, JsonElement Data);
