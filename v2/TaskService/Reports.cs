using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private static void MapReports(RouteGroupBuilder group)
    {
        group.MapGet("/requests/{id:guid}/report", GetReport);
        group.MapPost("/requests/{id:guid}/report/commands", ChangeReport)
            .WithMetadata(new RequestSizeLimitAttribute(65536));
        group.MapGet("/requests/{id:guid}/report/versions/{version:int}/pdf", DownloadReport);
    }

    private static async Task<IResult> GetReport(ServiceDb db, string slug, Guid id, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request))
            return ApiProblem.NotFound();

        var report = await db.Reports.FindAsync([tenant.Id, id], ct);
        var versions = db.ReportVersions.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.RequestId == id);
        if (!ReportStaff(actor, request))
        {
            var published = report?.PublishedVersion is int version
                ? await versions.Where(x => x.Version == version && x.Status == "published" && x.RecipientKey == actor.Key)
                    .Select(x => new { x.Version, x.PublishedAt }).SingleOrDefaultAsync(ct)
                : null;
            return Results.Ok(new { published });
        }

        var history = await versions.OrderByDescending(x => x.Version).Take(30).ToArrayAsync(ct);
        var available = report is not null && await db.ReportFiles.AnyAsync(x =>
            x.TenantId == tenant.Id && x.RequestId == id && x.Version == report.CurrentVersion, ct);
        var policy = await GetReportPolicy(db, tenant, actor, request, ct);
        var current = history.FirstOrDefault();
        http.Response.Headers.ETag = Tag(report?.Revision ?? 0);
        return Results.Ok(new
        {
            revision = report?.Revision ?? 0,
            publishedVersion = report?.PublishedVersion,
            actions = ReportActions(request, report, current, policy, actor, available),
            pdfReady = available,
            versions = history.Select(x => new
            {
                x.Version, x.SubmittedAt, x.Status, x.ReviewReason, x.ReviewedAt, x.PublishedAt,
                activities = JsonSerializer.Deserialize<ReportActivity[]>(x.Activities, Json),
                x.Result, x.Materials
            })
        });
    }

    private static async Task<IResult> ChangeReport(ServiceDb db, string slug, Guid id, ReportCommand body,
        HttpContext http, IDataProtectionProvider protection, IConfiguration config, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        await Lock(db, $"{tenant.Id}:request:{id}", ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request))
            return ApiProblem.NotFound();
        if (!ReportStaff(actor, request))
            return ApiProblem.Denied();
        var tag = http.Request.Headers.IfMatch.ToString();
        if (tag.Length == 0)
            return ApiProblem.Error(428, "precondition_required", "Bitte laden Sie den aktuellen Arbeitsbericht.");
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (!Guid.TryParseExact(key, "D", out _))
            return ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");

        var policy = await GetReportPolicy(db, tenant, actor, request, ct);
        if (!(body.Action switch
        {
            "submit" => policy.Create,
            "approve" or "reject" => policy.Approve,
            "publish" or "revoke" => policy.Publish,
            _ => false
        }))
            return ApiProblem.Denied();

        var operation = $"report:{id}";
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { body, tag }, Json));
        if (await db.Commands.FindAsync([tenant.Id, actor.Key, operation, key], ct) is { } previous)
            return Replay(http, previous, fingerprint);

        var report = await db.Reports.FindAsync([tenant.Id, id], ct);
        if (tag != Tag(report?.Revision ?? 0))
            return ApiProblem.Error(412, "stale_revision", "Der Arbeitsbericht wurde inzwischen geändert.");
        var current = report is null ? null : await FindReportVersion(db, report, report.CurrentVersion, ct);
        var fileReady = current is not null && await db.ReportFiles.AnyAsync(x =>
            x.TenantId == tenant.Id && x.RequestId == id && x.Version == current.Version, ct);
        if (!ReportActions(request, report, current, policy, actor, fileReady).Contains(body.Action))
            return ApiProblem.Error(409, "invalid_report_transition", "Diese Änderung ist derzeit nicht möglich.");
        var target = body.Action == "revoke" ? report!.PublishedVersion : current?.Version;
        if (body.Action != "submit" && body.Version != target)
            return ApiProblem.Error(409, "invalid_report_version", "Die ausgewählte Berichtsversion ist nicht mehr aktuell.");

        var now = DateTimeOffset.UtcNow;
        switch (body.Action)
        {
            case "submit":
                if (body.Activities is not { Length: >= 1 and <= 12 } activities ||
                    activities.Any(x => x is null || !ValidMultilineText(x.Description, 240) || x.Minutes is < 1 or > 1440) ||
                    activities.Sum(x => x!.Minutes) > 10080 || !ValidMultilineText(body.Result, 3000) ||
                    (!string.IsNullOrWhiteSpace(body.Materials) && !ValidMultilineText(body.Materials, 2000)))
                    return ApiProblem.Error(422, "invalid_report", "Bitte prüfen Sie Tätigkeiten, Dauer, Ergebnis und Material.");
                if (report is null)
                {
                    report = new Report(tenant.Id, id) { Revision = 1, CurrentVersion = 1 };
                    db.Reports.Add(report);
                    await db.SaveChangesAsync(ct);
                }
                else
                {
                    report.Revision++;
                    report.CurrentVersion++;
                }
                var activityJson = JsonSerializer.Serialize(activities.Select(x => new ReportActivity(x!.Description.Trim(), x.Minutes)), Json);
                db.ReportVersions.Add(new(tenant.Id, id, report.CurrentVersion, actor.Membership!.Id, activityJson,
                    body.Result!.Trim(), string.IsNullOrWhiteSpace(body.Materials) ? null : body.Materials.Trim(), now));
                break;
            case "approve" or "reject":
                if (body.Action == "reject" && !ValidMultilineText(body.Reason, 500))
                    return ApiProblem.Error(422, "missing_review_reason", "Bitte begründen Sie die Rückgabe.");
                current!.Status = body.Action == "approve" ? "approved" : "rejected";
                current.ReviewerId = actor.Membership!.Id;
                current.ReviewReason = body.Action == "reject" ? body.Reason!.Trim() : null;
                current.ReviewedAt = now;
                report!.Revision++;
                if (body.Action == "approve")
                    db.Outbox.Add(new(tenant.Id, Guid.NewGuid(), "report_pdf",
                        JsonSerializer.Serialize(new ReportRenderJob(id, current.Version, tenant.Name), Json), now));
                break;
            case "publish":
                if (report!.PublishedVersion is int previousVersion)
                {
                    var published = await FindReportVersion(db, report, previousVersion, ct);
                    published!.Status = "superseded";
                    await db.SaveChangesAsync(ct);
                }
                current!.Status = "published";
                current.RecipientKey = request.CustomerKey;
                current.PublishedAt = now;
                report.PublishedVersion = current.Version;
                report.Revision++;
                if (Uri.TryCreate(config["PublicBaseUrl"], UriKind.Absolute, out var publicBase) &&
                    publicBase.Scheme == "https" && !string.IsNullOrWhiteSpace(config["Mail:Host"]))
                {
                    var link = new Uri(publicBase, $"/unternehmen/{slug}/anfragen/{id}");
                    Workflow.QueueMail(db, tenant.Id, protection, request.ContactEmail, "Ein neuer Beleg ist verfügbar",
                        $"Melden Sie sich an, um Ihre Belege einzusehen: {link}");
                }
                break;
            case "revoke":
                var revoked = await FindReportVersion(db, report!, target!.Value, ct);
                revoked!.Status = "revoked";
                report!.PublishedVersion = null;
                report.Revision++;
                break;
        }

        var changedVersion = body.Action == "submit" ? report!.CurrentVersion : target;
        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, $"report.{body.Action}.v{changedVersion}", id, now));
        var command = new Command(tenant.Id, actor.Key, operation, key, fingerprint, id,
            JsonSerializer.Serialize(new { id, report!.Revision, report.CurrentVersion, report.PublishedVersion }, Json),
            null, report.Revision, 200);
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Replay(http, command, fingerprint);
    }

    private static async Task<IResult> DownloadReport(ServiceDb db, string slug, Guid id, int version,
        HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        await Lock(db, $"{tenant.Id}:request:{id}", ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request) ||
            await db.Reports.FindAsync([tenant.Id, id], ct) is not { } report)
            return ApiProblem.NotFound();

        var row = await FindReportVersion(db, report, version, ct);
        if (row is null || !ReportStaff(actor, request) &&
            !(report.PublishedVersion == version && row.Status == "published" && row.RecipientKey == actor.Key &&
              request.CustomerKey == actor.Key))
            return ApiProblem.NotFound();
        if (await db.ReportFiles.FindAsync([tenant.Id, id, version], ct) is not { } file)
            return ApiProblem.NotFound();
        if (!CryptographicOperations.FixedTimeEquals(file.Checksum, SHA256.HashData(file.Content)))
            return ApiProblem.Error(503, "document_unavailable", "Der Beleg ist vorübergehend nicht verfügbar.");

        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, $"report.download.v{version}", id, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.File(file.Content, "application/pdf", $"Arbeitsbericht-{id:N}-V{version}.pdf");
    }

    private static bool ReportStaff(Actor actor, ServiceRequest request) =>
        actor.IsCompany || actor.IsTechnician && actor.Membership!.Id == request.TechnicianId;

    private static async Task<ReportAccess> GetReportPolicy(ServiceDb db, Tenant tenant, Actor actor,
        ServiceRequest request, CancellationToken ct)
    {
        if (!ReportStaff(actor, request))
            return new(false, false, false, true);
        var grants = await db.ReportPermissions.Where(x => x.TenantId == tenant.Id && x.MemberId == actor.Membership!.Id)
            .Select(x => x.Permission).ToArrayAsync(ct);
        var independentReviewer = (await db.ReportPolicies.FindAsync([tenant.Id], ct))?.IndependentReviewer ?? true;
        return new(actor.IsTechnician || grants.Contains("report.create"), actor.IsCompany && grants.Contains("report.approve"),
            actor.IsCompany && grants.Contains("report.publish"), independentReviewer);
    }

    private static List<string> ReportActions(ServiceRequest request, Report? report, ReportVersion? current,
        ReportAccess access, Actor actor, bool fileReady)
    {
        var actions = new List<string>();
        if (access.Create && request.State is "in_progress" or "completed" &&
            current?.Status is null or "rejected" or "published" or "revoked")
            actions.Add("submit");
        if (access.Approve && current?.Status == "submitted" &&
            (!access.IndependentReviewer || current.AuthorId != actor.Membership!.Id))
            actions.AddRange(["approve", "reject"]);
        if (access.Publish && current?.Status == "approved" && fileReady && request.CustomerKey is not null)
            actions.Add("publish");
        if (access.Publish && report?.PublishedVersion is not null)
            actions.Add("revoke");
        return actions;
    }

    private static ValueTask<ReportVersion?> FindReportVersion(ServiceDb db, Report report, int version, CancellationToken ct) =>
        db.ReportVersions.FindAsync([report.TenantId, report.RequestId, version], ct);

}

public sealed record ReportActivity(string Description, int Minutes);
public sealed record ReportCommand(string Action, int? Version, ReportActivity?[]? Activities, string? Result,
    string? Materials, string? Reason);
internal sealed record ReportAccess(bool Create, bool Approve, bool Publish, bool IndependentReviewer);
public sealed record ReportRenderJob(Guid RequestId, int Version, string CompanyName);
