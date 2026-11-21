using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private const int MaxInvoiceBytes = 10 * 1024 * 1024;
    private static void MapInvoices(RouteGroupBuilder group)
    {
        var invoices = group.MapGroup("/requests/{id:guid}/invoices");
        invoices.MapGet("", ListInvoices);
        invoices.MapGet("/{invoiceId:guid}", GetInvoice);
        invoices.MapPost("/upload", UploadInvoice).WithMetadata(new RequestSizeLimitAttribute(MaxInvoiceBytes))
            .RequireRateLimiting("invoice-upload");
        invoices.MapPost("/{invoiceId:guid}/commands", ChangeInvoice)
            .WithMetadata(new RequestSizeLimitAttribute(4096));
        invoices.MapGet("/{invoiceId:guid}/versions/{version:int}/download", DownloadInvoice);
    }

    private static async Task<IResult> ListInvoices(ServiceDb db, string slug, Guid id, Guid? cursor,
        HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request))
            return ApiProblem.NotFound();
        var permissions = await InvoiceAccess(db, tenant, actor, request, ct);
        var internalView = permissions.Count > 0;
        var canRead = permissions.Contains("invoice.read") || permissions.Contains("invoice.approve");
        var query = db.Invoices.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.RequestId == id);
        if (cursor is Guid after)
            query = query.Where(x => x.Id.CompareTo(after) > 0);
        var versions = db.InvoiceVersions.AsNoTracking();
        var rows = await (from record in query
            join version in versions on new { record.TenantId, InvoiceId = record.Id,
                Version = internalView ? (int?)record.CurrentVersion : record.PublishedVersion }
                equals new { version.TenantId, version.InvoiceId, Version = (int?)version.Version }
            where internalView || version.Status == "published" && version.RecipientKey == actor.Key && request.CustomerKey == actor.Key
            orderby record.Id
            select new
            {
                record.Id, record.Revision, record.PublishedVersion, version.Version, version.OriginalName, version.UploadedAt,
                version.PublishedAt, version.Status, version.ScanStatus, version.Size, version.UploaderId
            }).Take(41).ToArrayAsync(ct);
        return Results.Ok(new
        {
            canUpload = permissions.Contains("invoice.upload"),
            items = rows.Take(40).Select(x => new
            {
                x.Id, revision = internalView ? (int?)x.Revision : null, x.PublishedVersion, x.Version,
                x.OriginalName, x.PublishedAt, x.UploadedAt, x.Status, x.ScanStatus, x.Size,
                canDownload = x.ScanStatus == "clean" && (!internalView || canRead),
                actions = InvoiceActions(permissions, actor, request, x.Status, x.ScanStatus, x.UploaderId, x.PublishedVersion)
            }),
            nextCursor = rows.Length == 41 ? rows[39].Id : (Guid?)null
        });
    }

    private static async Task<IResult> GetInvoice(ServiceDb db, string slug, Guid id, Guid invoiceId,
        int? before, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request) ||
            await FindInvoice(db, tenant, id, invoiceId, ct) is not { } invoice)
            return ApiProblem.NotFound();
        var access = await InvoiceAccess(db, tenant, actor, request, ct);
        if (access.Count == 0)
            return ApiProblem.NotFound();
        var rows = await db.InvoiceVersions.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.InvoiceId == invoiceId &&
                (before == null || x.Version < before))
            .OrderByDescending(x => x.Version).Take(31).Select(x => new
            {
                x.Version, x.OriginalName, x.Size, x.UploadedAt, x.ScanStatus, x.ScanAt,
                x.Status, x.ReviewReason, x.ReviewedAt, x.PublishedAt
            }).ToArrayAsync(ct);
        http.Response.Headers.ETag = Tag(invoice.Revision);
        return Results.Ok(new { items = rows.Take(30), nextCursor = rows.Length == 31 ? rows[29].Version : (int?)null });
    }

    private static async Task<IResult> UploadInvoice(ServiceDb db, string slug, Guid id, Guid? invoiceId,
        HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request))
            return ApiProblem.NotFound();
        if (!(await InvoiceAccess(db, tenant, actor, request, ct)).Contains("invoice.upload"))
            return ApiProblem.Denied();
        var tag = http.Request.Headers.IfMatch.ToString();
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (InvoicePreconditions(tag, key) is { } error)
            return error;

        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType))
            return ApiProblem.Error(415, "unsupported_invoice", "Bitte laden Sie eine PDF- oder XML-Rechnung hoch.");
        var contentType = mediaType.MediaType.Value!.ToLowerInvariant();
        var encodedName = http.Request.Headers["X-Invoice-Name"].ToString();
        var name = encodedName.Length <= 2000 ? Uri.UnescapeDataString(encodedName) : null;
        if (!ValidInvoiceName(name, contentType))
            return ApiProblem.Error(415, "unsupported_invoice", "Bitte wählen Sie eine PDF- oder XML-Rechnung mit gültigem Dateinamen.");
        if (http.Request.ContentLength is > MaxInvoiceBytes)
            return ApiProblem.Error(413, "invoice_too_large", "Die Rechnung darf höchstens 10 MB groß sein.");

        var bytes = await ReadInvoice(http.Request.Body, ct);
        if (bytes is null)
            return ApiProblem.Error(413, "invoice_too_large", "Die Rechnung darf höchstens 10 MB groß sein.");
        if (!HasInvoiceFormat(bytes, contentType))
            return ApiProblem.Error(422, "invalid_invoice_format", "Der Dateiinhalt entspricht keinem unterstützten Rechnungsformat.");
        var checksum = SHA256.HashData(bytes);

        await Lock(db, $"{tenant.Id}:request:{id}", ct);
        var operation = invoiceId is Guid logicalId ? $"invoice:upload:{logicalId}" : $"invoice:new:{id}";
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { id, tag, name, contentType, checksum }, Json));
        if (await db.Commands.FindAsync([tenant.Id, actor.Key, operation, key], ct) is { } previous)
            return Replay(http, previous, fingerprint);

        var invoice = invoiceId is Guid existing ? await FindInvoice(db, tenant, id, existing, ct) : null;
        if (invoiceId is not null && invoice is null)
            return ApiProblem.NotFound();
        if (tag != Tag(invoice?.Revision ?? 0))
            return ApiProblem.Error(412, "stale_revision", "Die Rechnung wurde inzwischen geändert.");
        if (invoice is not null)
        {
            var current = await FindInvoiceVersion(db, invoice, invoice.CurrentVersion, ct);
            if (!CanUploadRevision(current!.Status, current.ScanStatus))
                return ApiProblem.Error(409, "invoice_version_pending", "Die aktuelle Version muss zuerst geprüft oder zurückgegeben werden.");
        }
        if (invoice is null)
        {
            invoice = new(tenant.Id, id, Guid.NewGuid()) { CurrentVersion = 1, Revision = 1 };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            invoice.Revision++;
            invoice.CurrentVersion++;
        }

        var now = DateTimeOffset.UtcNow;
        db.InvoiceVersions.Add(new(tenant.Id, invoice.Id, invoice.CurrentVersion, actor.Membership!.Id, name!, contentType,
            bytes.Length, checksum, now));
        await db.SaveChangesAsync(ct);
        db.InvoiceFiles.Add(new(tenant.Id, invoice.Id, invoice.CurrentVersion, bytes));
        db.Outbox.Add(new(tenant.Id, Guid.NewGuid(), "invoice_scan",
            JsonSerializer.Serialize(new InvoiceScanJob(invoice.Id, invoice.CurrentVersion), Json), now));
        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, $"invoice.upload.v{invoice.CurrentVersion}", invoice.Id, now));
        var command = new Command(tenant.Id, actor.Key, operation, key, fingerprint, invoice.Id,
            JsonSerializer.Serialize(new { invoice.Id, invoice.Revision, invoice.CurrentVersion }, Json),
            $"/api/v1/tenants/{slug}/requests/{id}/invoices/{invoice.Id}", invoice.Revision, 201);
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Replay(http, command, fingerprint);
    }

    private static async Task<IResult> ChangeInvoice(ServiceDb db, string slug, Guid id, Guid invoiceId, InvoiceCommand body,
        HttpContext http, IDataProtectionProvider protection, IConfiguration config, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        await Lock(db, $"{tenant.Id}:request:{id}", ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request) ||
            await FindInvoice(db, tenant, id, invoiceId, ct) is not { } invoice)
            return ApiProblem.NotFound();
        var permissions = await InvoiceAccess(db, tenant, actor, request, ct);
        var required = body.Action switch
        {
            "approve" or "reject" => "invoice.approve",
            "publish" or "revoke" => "invoice.publish",
            _ => ""
        };
        if (!permissions.Contains(required))
            return ApiProblem.Denied();

        var tag = http.Request.Headers.IfMatch.ToString();
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (InvoicePreconditions(tag, key) is { } error)
            return error;
        var operation = $"invoice:{invoiceId}";
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { body, tag }, Json));
        if (await db.Commands.FindAsync([tenant.Id, actor.Key, operation, key], ct) is { } previous)
            return Replay(http, previous, fingerprint);
        if (tag != Tag(invoice.Revision))
            return ApiProblem.Error(412, "stale_revision", "Die Rechnung wurde inzwischen geändert.");
        var current = (await FindInvoiceVersion(db, invoice, invoice.CurrentVersion, ct))!;
        var target = body.Action == "revoke" ? invoice.PublishedVersion : current.Version;
        if (body.Version != target ||
            !InvoiceActions(permissions, actor, request, current.Status, current.ScanStatus, current.UploaderId,
                invoice.PublishedVersion).Contains(body.Action))
            return ApiProblem.Error(409, "invalid_invoice_transition", "Diese Änderung ist für die ausgewählte Rechnungsversion nicht möglich.");

        var now = DateTimeOffset.UtcNow;
        switch (body.Action)
        {
            case "approve" or "reject":
                if (body.Action == "reject" && !ValidMultilineText(body.Reason, 500))
                    return ApiProblem.Error(422, "missing_review_reason", "Bitte begründen Sie die Rückgabe.");
                current.Status = body.Action == "approve" ? "approved" : "rejected";
                current.ReviewerId = actor.Membership!.Id;
                current.ReviewedAt = now;
                current.ReviewReason = body.Action == "reject" ? body.Reason!.Trim() : null;
                break;
            case "publish":
                if (invoice.PublishedVersion is int oldVersion)
                {
                    var old = await FindInvoiceVersion(db, invoice, oldVersion, ct);
                    old!.Status = "superseded";
                    await db.SaveChangesAsync(ct);
                }
                current.Status = "published";
                current.RecipientKey = request.CustomerKey;
                current.PublishedAt = now;
                invoice.PublishedVersion = current.Version;
                if (Uri.TryCreate(config["PublicBaseUrl"], UriKind.Absolute, out var publicBase) &&
                    publicBase.Scheme == "https" && (!string.IsNullOrWhiteSpace(config["Mail:Host"]) || NativeIdentity.MailHeld(config)))
                {
                    var link = new Uri(publicBase, $"/unternehmen/{slug}/anfragen/{id}");
                    Workflow.QueueMail(db, tenant.Id, protection, request.ContactEmail, "Eine Rechnung ist verfügbar",
                        $"Melden Sie sich an, um Ihre Rechnung einzusehen: {link}", NativeIdentity.MailHeld(config));
                }
                break;
            case "revoke":
                var released = await FindInvoiceVersion(db, invoice, body.Version, ct);
                released!.Status = "revoked";
                invoice.PublishedVersion = null;
                break;
        }

        invoice.Revision++;
        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, $"invoice.{body.Action}.v{target}", invoice.Id, now));
        var command = new Command(tenant.Id, actor.Key, operation, key, fingerprint, invoice.Id,
            JsonSerializer.Serialize(new { invoice.Id, invoice.Revision, invoice.PublishedVersion }, Json),
            null, invoice.Revision, 200);
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Replay(http, command, fingerprint);
    }

    private static async Task<IResult> DownloadInvoice(ServiceDb db, string slug, Guid id, Guid invoiceId, int version,
        HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        await Lock(db, $"{tenant.Id}:request:{id}", ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request) ||
            await FindInvoice(db, tenant, id, invoiceId, ct) is not { } invoice ||
            await FindInvoiceVersion(db, invoice, version, ct) is not { ScanStatus: "clean" } row)
            return ApiProblem.NotFound();
        var permissions = await InvoiceAccess(db, tenant, actor, request, ct);
        var internalRead = permissions.Contains("invoice.read") || permissions.Contains("invoice.approve");
        var isRecipient = invoice.PublishedVersion == version && row.Status == "published" &&
            request.CustomerKey == actor.Key && row.RecipientKey == actor.Key;
        if (!internalRead && !isRecipient)
            return ApiProblem.NotFound();
        if (await db.InvoiceFiles.FindAsync([tenant.Id, invoiceId, version], ct) is not { } file)
            return ApiProblem.NotFound();
        if (file.Content.Length != row.Size || !CryptographicOperations.FixedTimeEquals(row.Checksum, SHA256.HashData(file.Content)))
            return ApiProblem.Error(503, "document_unavailable", "Die Rechnung ist vorübergehend nicht verfügbar.");

        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, $"invoice.download.v{version}", invoiceId, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.File(file.Content, row.ContentType, row.OriginalName);
    }

    private static async Task<HashSet<string>> InvoiceAccess(ServiceDb db, Tenant tenant, Actor actor,
        ServiceRequest request, CancellationToken ct)
    {
        if (!ReportStaff(actor, request))
            return [];
        var permissions = await db.InvoicePermissions.Where(x => x.TenantId == tenant.Id && x.MemberId == actor.Membership!.Id)
            .Select(x => x.Permission).ToArrayAsync(ct);
        return permissions.Where(permission => actor.IsCompany || permission is "invoice.upload" or "invoice.read")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<string> InvoiceActions(HashSet<string> permissions, Actor actor, ServiceRequest request, string status,
        string scan, Guid uploader, int? publishedVersion)
    {
        var actions = new List<string>();
        if (permissions.Contains("invoice.upload") && CanUploadRevision(status, scan))
            actions.Add("upload");
        if (permissions.Contains("invoice.approve") && status == "uploaded" && scan == "clean" && actor.Membership!.Id != uploader)
            actions.AddRange(["approve", "reject"]);
        if (permissions.Contains("invoice.publish") && status == "approved" && request.CustomerKey is not null)
            actions.Add("publish");
        if (permissions.Contains("invoice.publish") && publishedVersion is not null)
            actions.Add("revoke");
        return actions;
    }

    private static bool CanUploadRevision(string status, string scan) =>
        status is "rejected" or "published" or "revoked" || scan is "quarantined" or "failed";

    private static Task<Invoice?> FindInvoice(ServiceDb db, Tenant tenant, Guid requestId, Guid invoiceId, CancellationToken ct) =>
        db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenant.Id && x.RequestId == requestId && x.Id == invoiceId, ct);

    private static ValueTask<InvoiceVersion?> FindInvoiceVersion(ServiceDb db, Invoice invoice, int version, CancellationToken ct) =>
        db.InvoiceVersions.FindAsync([invoice.TenantId, invoice.Id, version], ct);

    private static IResult? InvoicePreconditions(string tag, string key)
    {
        if (tag.Length == 0)
            return ApiProblem.Error(428, "precondition_required", "Bitte laden Sie den aktuellen Stand der Rechnung.");
        return Guid.TryParseExact(key, "D", out _) ? null :
            ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");
    }

    private static bool ValidInvoiceName(string? name, string type)
    {
        var suffix = type switch { "application/pdf" => ".pdf", "application/xml" => ".xml", _ => null };
        return suffix is not null && Valid(name, 160) && name!.Length > suffix.Length &&
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Trim() == name &&
            !name.Any(c => c is '/' or '\\' or ':' || char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);
    }

    private static async Task<byte[]?> ReadInvoice(Stream body, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var output = new MemoryStream();
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > MaxInvoiceBytes)
                return null;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool HasInvoiceFormat(byte[] bytes, string type)
    {
        if (type == "application/pdf")
            return bytes.AsSpan().StartsWith("%PDF-"u8) && bytes.AsSpan(Math.Max(0, bytes.Length - 1024)).IndexOf("%%EOF"u8) >= 0;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxInvoiceBytes
            });
            reader.MoveToContent();
            var recognized = (reader.LocalName, reader.NamespaceURI) is
                ("Invoice", "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2") or
                ("CreditNote", "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2") or
                ("CrossIndustryInvoice", "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100");
            if (!recognized)
                return false;
            while (reader.Read()) { }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}

public sealed record InvoiceCommand(string Action, int Version, string? Reason);
public sealed record InvoiceScanJob(Guid InvoiceId, int Version);
