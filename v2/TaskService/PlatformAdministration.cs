using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private static void MapPlatform(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/platform").AddEndpointFilter<CsrfFilter>();
        group.MapGet("/policies/{scope}", ReadPolicy);
        group.MapPost("/policies/{scope}", SavePolicy).WithMetadata(new RequestSizeLimitAttribute(8192));
        group.MapGet("/overview", Platform);
        group.MapGet("/companies/{slug}/features", CompanyFeatures);
        group.MapGet("/audit", PlatformAuditLog);
        group.MapGet("/companies/{slug}/setup", AdminSetupStatus);
        group.MapPost("/companies/{slug}/setup", InviteInitialAdmin).WithMetadata(new RequestSizeLimitAttribute(4096)).RequireRateLimiting("invitations");
        group.MapPost("/companies", CreateCompany).WithMetadata(new RequestSizeLimitAttribute(4096));
        group.MapPost("/companies/{slug}/commands", ModifyCompany).WithMetadata(new RequestSizeLimitAttribute(16384));
    }

    private static async Task<IResult> Platform(Guid? cursor, HttpContext http, IConfiguration config, CancellationToken ct)
    {
        if (Operator(http, config) is null)
            return ApiProblem.Denied();
        await using var db = OpenPlatform(config);
        if (db is null)
            return PlatformUnavailable();
        var query = db.Tenants.AsNoTracking();
        if (cursor is { } after)
            query = query.Where(x => x.Id.CompareTo(after) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(51).ToArrayAsync(ct);
        return Results.Ok(new
        {
            items = rows.Take(50).Select(CompanyView),
            nextCursor = rows.Length == 51 ? rows[49].Id : (Guid?)null
        });
    }

    private static async Task<IResult> PlatformAuditLog(DateTimeOffset? before, Guid? cursor, HttpContext http,
        IConfiguration config, CancellationToken ct)
    {
        if (Operator(http, config) is null)
            return ApiProblem.Denied();
        await using var db = OpenPlatform(config);
        if (db is null)
            return PlatformUnavailable();
        var query = db.PlatformAudit.AsNoTracking();
        if (before is { } time && cursor is { } id)
        {
            var utc = time.ToUniversalTime();
            query = query.Where(x => x.RecordedAt < utc || x.RecordedAt == utc && x.Id.CompareTo(id) < 0);
        }
        var rows = await (from action in query
            join tenant in db.Tenants.AsNoTracking() on action.TenantId equals tenant.Id
            orderby action.RecordedAt descending, action.Id descending
            select new { action.Id, tenant.Slug, action.Action, action.Reason, action.RecordedAt, action.Actor })
            .Take(51).ToArrayAsync(ct);
        return Results.Ok(new
        {
            items = rows.Take(50),
            next = rows.Length == 51 ? new { id = rows[49].Id, before = rows[49].RecordedAt } : null
        });
    }

    private static Task<IResult> CreateCompany(NewCompany body, HttpContext http, IConfiguration config, CancellationToken ct) =>
        ApplyCompany(http, config, body.Slug, new CompanyCommand("provision", body.Name, body.Accent, null), ct);

    private static Task<IResult> ModifyCompany(string slug, CompanyCommand body, HttpContext http, IConfiguration config,
        CancellationToken ct) =>
        body.Action is "brand" or "suspend" or "activate" or "deactivate" or "feature" or "plan" or "form"
            ? ApplyCompany(http, config, slug, body, ct)
            : Task.FromResult(ApiProblem.Error(422, "invalid_company_action", "Diese Unternehmensaktion ist ungültig."));

    private static async Task<IResult> ApplyCompany(HttpContext http, IConfiguration config, string? slug, CompanyCommand body,
        CancellationToken ct)
    {
        if (Operator(http, config) is not { } actor)
            return ApiProblem.Denied();
        if (slug is not { Length: > 0 and <= 63 } || !TenantSlug().IsMatch(slug))
            return ApiProblem.Error(422, "invalid_slug", "Die Portaladresse darf aus Kleinbuchstaben, Ziffern und Bindestrichen bestehen.");
        if (body.Action is "provision" or "brand" &&
            (!ValidMultilineText(body.Name, 120) || body.Name!.Any(char.IsControl) ||
                body.Accent is not { Length: 7 } accent || accent[0] != '#' || !accent.Skip(1).All(Uri.IsHexDigit)))
            return ApiProblem.Error(422, "invalid_brand", "Bitte geben Sie einen Namen und eine gültige Markenfarbe an.");
        if (body.Action is "suspend" or "activate" or "deactivate" && !ValidMultilineText(body.Reason, 500))
            return ApiProblem.Error(422, "missing_reason", "Bitte begründen Sie diese Statusänderung.");

        if (body.Action == "form" && !ServiceForms.Valid(body.Form))
            return ApiProblem.Error(422, "invalid_service_form", "Bitte prüfen Sie die Felder und Anzeigebedingungen.");
        if (body.Action == "plan" && (body.Plan is null || !PlanScopes.Contains(body.Plan) || body.Plan == "global"))
            return ApiProblem.Error(422, "invalid_plan", "Der gewählte Tarif ist ungültig.");
        if (body.Action == "feature" && (body.Feature is not { Key: { Length: > 0 and <= 64 }, State: "enabled" or "disabled" or "default" } feature ||
            !ElectricalFeatures.IsKnown(feature.Key)))
            return ApiProblem.Error(422, "invalid_feature", "Die gewählte Funktion oder Einstellung ist ungültig.");

        var tag = http.Request.Headers.IfMatch.ToString();
        if (tag.Length == 0)
            return ApiProblem.Error(428, "precondition_required", "Bitte laden Sie den aktuellen Stand des Unternehmens.");
        if (!Guid.TryParseExact(http.Request.Headers["Idempotency-Key"], "D", out var key))
            return ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");
        await using var db = OpenPlatform(config);
        if (db is null)
            return PlatformUnavailable();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var operation = $"platform:{slug}";
        await Lock(db, operation, ct);
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { body, slug, tag }, Json));
        if (await db.PlatformCommands.FindAsync([actor, operation, key], ct) is { } previous)
            return PlatformResponse(http, previous, fingerprint);

        var tenant = await db.Tenants.SingleOrDefaultAsync(x => x.Slug == slug, ct);
        var creating = body.Action == "provision";
        if (creating && tenant is not null)
            return ApiProblem.Error(409, "slug_taken", "Diese Portaladresse ist bereits belegt.");
        if (!creating && tenant is null)
            return ApiProblem.NotFound();
        if (tag != Tag(tenant?.Revision ?? 0))
            return ApiProblem.Error(412, "stale_revision", "Das Unternehmen wurde inzwischen geändert.");

        if (creating)
        {
            tenant = new Tenant(Guid.NewGuid(), slug, body.Name!.Trim(), body.Accent!.ToLowerInvariant(), true);
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var next = (tenant!.Lifecycle, body.Action) switch
            {
                ("active", "suspend") => "suspended",
                ("suspended", "activate") => "active",
                ("active" or "suspended", "deactivate") => "deactivated",
                ("active" or "suspended", "brand" or "feature" or "plan" or "form") => tenant.Lifecycle,
                _ => null
            };
            if (next is null)
                return ApiProblem.Error(409, "invalid_company_transition", "Diese Änderung ist im aktuellen Zustand nicht möglich.");
            tenant.Lifecycle = next;
            tenant.Active = next == "active";
            if (body.Action == "brand")
            {
                tenant.Name = body.Name!.Trim();
                tenant.Accent = body.Accent!.ToLowerInvariant();
            }
            if (body.Action == "form")
                ServiceForms.Save(tenant, body.Form!);
            if (body.Action == "plan")
                tenant.PlanCode = body.Plan!;
            if (body.Action == "feature")
                SetFeatureOverride(tenant, body.Feature!);
            tenant.Revision++;
        }

        db.PlatformAudit.Add(new(Guid.NewGuid(), tenant!.Id, actor, body.Action,
            body.Action is "suspend" or "activate" or "deactivate" ? body.Reason!.Trim() :
                body.Action == "form" ? body.Form!.Service : body.Action == "plan" ? body.Plan : body.Feature is { } change && body.Action == "feature" ? $"{change.Key} = {change.State}" : null, DateTimeOffset.UtcNow));
        var command = new PlatformCommand(actor, operation, key, fingerprint,
            JsonSerializer.Serialize(CompanyView(tenant), Json), tenant.Revision, creating ? 201 : 200);
        db.PlatformCommands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PlatformResponse(http, command, fingerprint);
    }

    private static string? Operator(HttpContext http, IConfiguration config) =>
        IdentitySessions.HasPrivilegedAssurance(http.User, config) &&
        !http.User.HasClaim(IdentitySessions.AssuranceClaim, NativeIdentity.DemoPasswordOnlyAssurance) &&
        (http.User.HasClaim("taskservice_role", "platform_admin") ||
         config["Identity:PlatformAdminSubject"] is { Length: > 0 } subject && http.User.HasClaim("sub", subject))
            ? Actor.Subject(http.User)
            : null;

    private static ServiceDb? OpenPlatform(IConfiguration config) =>
        config.GetConnectionString("Platform") is { Length: > 0 } connection
            ? new ServiceDb(new DbContextOptionsBuilder<ServiceDb>().UseNpgsql(connection).Options)
            : null;

    private static IResult PlatformUnavailable() =>
        ApiProblem.Error(503, "platform_unavailable", "Die Plattformverwaltung ist vorübergehend nicht verfügbar.");

    private static PlatformCompany CompanyView(Tenant row) =>
        new(row.Slug, row.Name, row.Accent, row.Active, row.Lifecycle, row.Revision, row.PlanCode);

    private static IResult PlatformResponse(HttpContext http, PlatformCommand previous, byte[] fingerprint)
    {
        if (!CryptographicOperations.FixedTimeEquals(previous.Fingerprint, fingerprint))
            return ApiProblem.Error(409, "idempotency_conflict", "Dieser Schlüssel wurde bereits für eine andere Anfrage verwendet.");
        http.Response.Headers.ETag = Tag(previous.Revision);
        return Results.Content(previous.Response, "application/json", statusCode: previous.Status);
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TenantSlug();
}

public sealed record NewCompany(string Slug, string Name, string Accent);
public sealed record CompanyCommand(string Action, string? Name, string? Accent, string? Reason,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    FeatureChange? Feature = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Plan = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    FormChange? Form = null);
public sealed record PlatformCompany(string Slug, string Name, string Accent, bool Active, string Lifecycle, int Revision, string PlanCode);
