using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapTaskService(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tenants/{slug}").AddEndpointFilter<CsrfFilter>();
        group.MapGet("", TenantView);
        group.MapGet("/manifest.webmanifest", TenantManifest);
        group.MapGet("/requests", Requests);
        group.MapGet("/requests/{id:guid}", Request);
        group.MapPost("/requests", Submit).RequireRateLimiting("guest");
        group.MapPost("/requests/{id:guid}/claim", ClaimRequest).RequireRateLimiting("guest");
        group.MapPost("/requests/{id:guid}/commands", Change);
        group.MapGet("/technicians", Technicians);
        group.MapGet("/rooms/{channel}", Messages);
        group.MapPost("/rooms/{channel}", PostMessage);
        MapReports(group);
        MapInvoices(group);
        MapPlatform(app);
    }

    internal static async Task<Tenant?> FindTenant(ServiceDb db, string slug, CancellationToken ct) =>
        slug.Length <= 63
            ? await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == slug && x.Active, ct)
            : null;

    private static async Task<IResult> TenantManifest(ServiceDb db, string slug, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        var entry = $"/unternehmen/{tenant.Slug}";
        return Results.Json(new
        {
            id = entry,
            name = tenant.Name,
            short_name = tenant.Name,
            lang = "de",
            start_url = entry,
            scope = "/",
            display = "standalone",
            background_color = "#faf9f6",
            theme_color = tenant.Accent,
            icons = new[] { 192, 512 }.Select(size => new
            {
                src = $"/icons/taskservice-{size}.png", sizes = $"{size}x{size}", type = "image/png", purpose = "any maskable"
            })
        }, contentType: "application/manifest+json");
    }

    private static async Task<IResult> TenantView(ServiceDb db, string slug, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        return Results.Ok(new { tenant.Slug, tenant.Name, tenant.Accent, Role = actor.Membership?.Role });
    }

    private static async Task<IResult> Requests(ServiceDb db, string slug, Guid? cursor, string? view, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (actor == Actor.Guest)
            return ApiProblem.Denied();
        var query = db.Requests.AsNoTracking().Where(x => x.TenantId == tenant.Id);
        if (view == "mine")
            query = query.Where(x => x.CustomerKey == actor.Key);
        else if (!actor.IsCompany)
        {
            var technician = actor.IsTechnician ? actor.Membership!.Id : (Guid?)null;
            query = query.Where(x => x.CustomerKey == actor.Key || (technician != null && x.TechnicianId == technician));
        }
        if (cursor is { } id)
            query = query.Where(x => x.Id.CompareTo(id) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(51)
            .Select(x => new { x.Id, x.Title, x.State, x.Revision, x.CreatedAt, x.TechnicianId }).ToListAsync(ct);
        return Results.Ok(new
        {
            items = rows.Take(50).Select(x => new
            {
                x.Id, x.Title,
                State = view != "mine" && (actor.IsCompany || actor.IsTechnician && actor.Membership!.Id == x.TechnicianId)
                    ? x.State : Workflow.CustomerState(x.State),
                x.Revision, x.CreatedAt
            }),
            nextCursor = rows.Count > 50 ? rows[49].Id : (Guid?)null
        });
    }

    private static async Task<IResult> Request(ServiceDb db, string slug, Guid id, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request))
            return ApiProblem.NotFound();
        var staff = actor.IsCompany || actor.IsTechnician && actor.Membership!.Id == request.TechnicianId;
        var history = await db.History.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.RequestId == id)
            .OrderBy(x => x.Revision).Select(x => new { x.State, x.At, x.PrivateNote }).ToListAsync(ct);
        var appointment = await db.Appointments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant.Id && x.RequestId == id && !x.Cancelled, ct);
        http.Response.Headers.ETag = Tag(request.Revision);
        return Results.Ok(new
        {
            request.Id, request.Title, request.Description, request.CreatedAt, request.Revision,
            State = staff ? request.State : Workflow.CustomerState(request.State),
            TechnicianId = staff ? request.TechnicianId : null,
            CanReadInternal = staff,
            History = history.Select(x => new
            {
                State = staff ? x.State : Workflow.CustomerState(x.State), x.At,
                PrivateNote = actor.IsCompany ? x.PrivateNote : null
            }),
            Appointment = appointment is null ? null : new
            {
                appointment.StartsAt, appointment.EndsAt, appointment.ArrivalStatus, appointment.TimeZone,
                appointment.UpdatedAt, appointment.CustomerConfirmedAt, appointment.CustomerNote
            },
            Actions = Workflow.Actions(request, actor, appointment)
        });
    }

    private static async Task<IResult> Technicians(ServiceDb db, string slug, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        if (!(await Actor.Read(db, tenant, http, ct)).IsCompany)
            return ApiProblem.Denied();
        return Results.Ok(await db.Members.Where(x => x.TenantId == tenant.Id && x.Active && x.Role == "technician")
            .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(ct));
    }

    private static async Task<IResult> Submit(ServiceDb db, string slug, NewRequest body, HttpContext http,
        IConfiguration config, IDataProtectionProvider protection, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        if (!Valid(body.Title, 160) || !Valid(body.Description, 4000) || !Valid(body.Email, 254) ||
            !MailAddress.TryCreate(body.Email, out var mail) || mail.Address != body.Email)
            return ApiProblem.Error(422, "invalid_request", "Bitte ergänzen Sie Betreff, Beschreibung und eine gültige E-Mail-Adresse.");
        if (!Uri.TryCreate(config["PublicBaseUrl"], UriKind.Absolute, out var publicBase) ||
            publicBase.Scheme != "https" || string.IsNullOrWhiteSpace(config["Mail:Host"]))
            return ApiProblem.Error(503, "verification_unavailable", "Die Bestätigung per E-Mail ist derzeit nicht verfügbar.");
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (!Guid.TryParseExact(key, "D", out _))
            return ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");
        const string operation = "create-request";
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, Json));
        await Lock(db, $"{tenant.Id}:{actor.Key}:{operation}:{key}", ct);
        if (await db.Commands.FindAsync([tenant.Id, actor.Key, operation, key], ct) is { } previous)
            return Replay(http, previous, fingerprint);
        var id = Guid.NewGuid();
        var request = new ServiceRequest
        {
            TenantId = tenant.Id, Id = id, Title = body.Title.Trim(), Description = body.Description.Trim(),
            ContactEmail = mail.Address, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync(ct);
        var token = RandomNumberGenerator.GetBytes(32);
        db.Claims.Add(new(tenant.Id, id, SHA256.HashData(token), request.CreatedAt.AddHours(24)));
        Workflow.Record(db, request, actor, "request.created");
        var claim = new Uri(publicBase, $"/unternehmen/{tenant.Slug}/anfragen/{id}/bestaetigen#{Convert.ToHexStringLower(token)}");
        Workflow.QueueMail(db, tenant.Id, protection, mail.Address, "Bitte bestätigen Sie Ihre Serviceanfrage",
            $"Melden Sie sich über diesen Link an, um Ihre Anfrage zu bestätigen: {claim}");
        var command = new Command(tenant.Id, actor.Key, operation, key, fingerprint, id,
            JsonSerializer.Serialize(new { id, status = "received" }, Json),
            $"/api/v1/tenants/{slug}/requests/{id}", 1, 201);
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Replay(http, command, fingerprint);
    }

    private static async Task<IResult> ClaimRequest(ServiceDb db, string slug, Guid id, ClaimBody body,
        HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant || Actor.Subject(http.User) is not { } key ||
            body.Token is not { Length: 64 } || !body.Token.All(Uri.IsHexDigit))
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        await Lock(db, $"{tenant.Id}:request:{id}", ct);
        var claim = await db.Claims.FindAsync([tenant.Id, id], ct);
        if (claim is null || claim.ConsumedAt is not null || claim.ExpiresAt <= DateTimeOffset.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(claim.Digest, SHA256.HashData(Convert.FromHexString(body.Token))) ||
            await FindRequest(db, tenant, id, ct) is not { State: "received", CustomerKey: null } request)
            return ApiProblem.NotFound();
        claim.ConsumedAt = DateTimeOffset.UtcNow;
        request.CustomerKey = key;
        request.State = "verified";
        request.Revision++;
        Workflow.Record(db, request, new(key, null), "request.claimed");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Change(ServiceDb db, string slug, Guid id, RequestCommand body,
        HttpContext http, IDataProtectionProvider protection, IConfiguration config, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        await Lock(db, $"{tenant.Id}:request:{id}", ct);
        if (await FindRequest(db, tenant, id, ct) is not { } request || !actor.CanRead(request))
            return ApiProblem.NotFound();
        var tag = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(tag))
            return ApiProblem.Error(428, "precondition_required", "Bitte laden Sie den aktuellen Stand des Vorgangs.");
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (!Guid.TryParseExact(key, "D", out _))
            return ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");
        var operation = $"request:{id}";
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { body, tag }, Json));
        if (await db.Commands.FindAsync([tenant.Id, actor.Key, operation, key], ct) is { } previous)
        {
            // State-changing retries still require a current write privilege on the resource.
            if (!(actor.IsCompany || actor.IsTechnician && actor.Membership!.Id == request.TechnicianId ||
                (body.Action is "resume" or "confirm_appointment") && request.CustomerKey == actor.Key))
                return ApiProblem.Denied();
            return Replay(http, previous, fingerprint);
        }
        if (tag != Tag(request.Revision))
            return ApiProblem.Error(412, "stale_revision", "Der Vorgang wurde inzwischen geändert.");
        var appointment = await db.Appointments.SingleOrDefaultAsync(x =>
            x.TenantId == tenant.Id && x.RequestId == id && !x.Cancelled, ct);
        if (!Workflow.Actions(request, actor, appointment).Contains(body.Action))
            return ApiProblem.Error(409, "invalid_transition", "Diese Änderung ist im aktuellen Zustand nicht möglich.");
        switch (body.Action)
        {
            case "triage": request.State = "triage"; break;
            case "request_information":
                request.ResumeState = request.State;
                request.State = "awaiting_customer";
                break;
            case "resume":
                request.State = request.ResumeState!;
                request.ResumeState = null;
                break;
            case "assign":
                if (!await db.Members.AnyAsync(x => x.TenantId == tenant.Id && x.Id == body.TechnicianId &&
                    x.Role == "technician" && x.Active, ct))
                    return ApiProblem.Error(422, "invalid_technician", "Bitte wählen Sie eine aktive Fachkraft.");
                if (appointment is not null)
                    appointment.Cancelled = true;
                request.TechnicianId = body.TechnicianId;
                request.State = "assigned";
                break;
            case "schedule":
                var useBerlin = body.StartsAtLocal is not null || body.EndsAtLocal is not null;
                var startSlot = useBerlin ? Workflow.ResolveBerlinSlot(body.StartsAtLocal, body.StartsAtFold) : body.StartsAt;
                var endSlot = useBerlin ? Workflow.ResolveBerlinSlot(body.EndsAtLocal, body.EndsAtFold) : body.EndsAt;
                if (startSlot is not { } starts || endSlot is not { } ends ||
                    starts <= DateTimeOffset.UtcNow || ends <= starts || ends - starts > TimeSpan.FromDays(1))
                    return ApiProblem.Error(422, "invalid_slot",
                        "Wählen Sie ein gültiges zukünftiges Zeitfenster. Bei der Zeitumstellung geben Sie bei doppelten Uhrzeiten die erste oder zweite Stunde an.");
                // Keep same-technician writers ordered in addition to the database exclusion constraint.
                await Lock(db, $"{tenant.Id}:technician:{request.TechnicianId}", ct);
                if (appointment is not null)
                {
                    appointment.Cancelled = true;
                    await db.SaveChangesAsync(ct);
                }
                if (body.Note is { Length: > 0 } && !ValidMultilineText(body.Note, 500))
                    return ApiProblem.Error(422, "invalid_appointment_note", "Der Terminhinweis darf höchstens 500 Zeichen umfassen.");
                db.Appointments.Add(new(tenant.Id, Guid.NewGuid(), id, request.TechnicianId!.Value,
                    starts.ToUniversalTime(), ends.ToUniversalTime())
                {
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CustomerNote = body.Note?.Trim()
                });
                request.State = "scheduled";
                break;
            case "arrival":
                if (appointment is null || body.ArrivalStatus is not ("departed" or "delayed" or "arrived"))
                    return ApiProblem.Error(422, "invalid_arrival", "Bitte wählen Sie einen gültigen Einsatzstatus.");
                appointment.ArrivalStatus = body.ArrivalStatus;
                appointment.UpdatedAt = DateTimeOffset.UtcNow;
                break;
            case "appointment_note":
                if (appointment is null || body.Note is { Length: > 0 } && !ValidMultilineText(body.Note, 500))
                    return ApiProblem.Error(422, "invalid_appointment_note", "Der Terminhinweis darf höchstens 500 Zeichen umfassen.");
                appointment.CustomerNote = body.Note?.Trim();
                appointment.UpdatedAt = DateTimeOffset.UtcNow;
                break;
            case "confirm_appointment":
                appointment!.CustomerConfirmedAt = DateTimeOffset.UtcNow;
                appointment.UpdatedAt = appointment.CustomerConfirmedAt;
                break;
            case "start": request.State = "in_progress"; break;
            case "complete":
                if (!await db.Reports.AnyAsync(x => x.TenantId == tenant.Id && x.RequestId == id &&
                    x.PublishedVersion != null && x.CurrentVersion == x.PublishedVersion, ct))
                    return ApiProblem.Error(409, "report_publication_required", "Veröffentlichen Sie zuerst den aktuellen Arbeitsbericht.");
                request.State = "completed";
                break;
            case "cancel":
            case "reopen":
                if (body.Action == "reopen" && !ValidMultilineText(body.Note, 500))
                    return ApiProblem.Error(422, "missing_reopen_reason", "Bitte geben Sie einen Grund für die Wiedereröffnung an.");
                if (appointment is not null)
                    appointment.Cancelled = true;
                request.State = body.Action == "cancel" ? "cancelled" : "triage";
                request.ResumeState = null;
                request.TechnicianId = null;
                break;
        }

        request.Revision++;
        Workflow.Record(db, request, actor, $"request.{body.Action}", body.Action == "reopen" ? body.Note!.Trim() : null);
        if (request.CustomerKey is not null && body.Action is "schedule" or "assign" or "cancel" &&
            Uri.TryCreate(config["PublicBaseUrl"], UriKind.Absolute, out var publicBase) && publicBase.Scheme == "https" &&
            !string.IsNullOrWhiteSpace(config["Mail:Host"]))
        {
            var link = new Uri(publicBase, $"/unternehmen/{slug}/anfragen/{id}");
            Workflow.QueueMail(db, tenant.Id, protection, request.ContactEmail, "Ihre Serviceanfrage wurde aktualisiert",
                $"Melden Sie sich an, um die Änderung einzusehen: {link}");
        }
        var command = new Command(tenant.Id, actor.Key, operation, key, fingerprint, id,
            JsonSerializer.Serialize(new { id, revision = request.Revision }, Json), null, request.Revision, 200);
        db.Commands.Add(command);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.Error(412, "stale_revision", "Der Vorgang wurde inzwischen geändert.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation })
        {
            return ApiProblem.Error(409, "appointment_conflict", "Die Fachkraft ist in diesem Zeitfenster bereits eingeplant.");
        }
        return Replay(http, command, fingerprint);
    }

    private static async Task<IResult> Messages(ServiceDb db, string slug, string channel, Guid? requestId, long? after,
        HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (!await CanReadRoom(db, tenant, actor, channel, requestId, ct))
            return ApiProblem.NotFound();
        var rows = await db.Messages.AsNoTracking()
            .Where(x => x.TenantId == tenant.Id && x.RequestId == requestId && x.Channel == channel && x.Sequence > (after ?? 0))
            .OrderBy(x => x.Sequence).Take(101)
            .Select(x => new { x.Sequence, x.Author, x.Body, x.SentAt }).ToListAsync(ct);
        return Results.Ok(new { items = rows.Take(100), next = rows.Count == 101 ? rows[99].Sequence : (long?)null });
    }

    private static async Task<IResult> PostMessage(ServiceDb db, string slug, string channel, Guid? requestId,
        NewMessage body, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        if (!Valid(body.Body, 2000))
            return ApiProblem.Error(422, "invalid_message", "Bitte geben Sie eine Nachricht ein.");
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (!await CanReadRoom(db, tenant, actor, channel, requestId, ct))
            return ApiProblem.NotFound();
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (!Guid.TryParseExact(key, "D", out _))
            return ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");
        var operation = $"room:{channel}:{requestId}";
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, Json));
        await Lock(db, $"{tenant.Id}:{operation}", ct);
        if (await db.Commands.FindAsync([tenant.Id, actor.Key, operation, key], ct) is { } previous)
            return Replay(http, previous, fingerprint);
        var message = new Message(tenant.Id, requestId, channel, actor.Membership?.Name ?? "Kunde",
            body.Body.Trim(), DateTimeOffset.UtcNow);
        db.Messages.Add(message);
        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, "message.created", requestId ?? tenant.Id, message.SentAt));
        await db.SaveChangesAsync(ct);
        var command = new Command(tenant.Id, actor.Key, operation, key, fingerprint, requestId ?? tenant.Id,
            JsonSerializer.Serialize(new { message.Sequence }, Json), null, null, 201);
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Replay(http, command, fingerprint);
    }

    internal static async Task<bool> CanReadRoom(ServiceDb db, Tenant tenant, Actor actor, string channel, Guid? requestId,
        CancellationToken ct)
    {
        if (channel == "company")
            return requestId is null && actor.Membership is not null;
        if (channel is not ("customer" or "internal") || requestId is null ||
            channel == "internal" && actor.Membership is null)
            return false;
        return await FindRequest(db, tenant, requestId.Value, ct) is { } request && actor.CanRead(request) &&
            (channel != "internal" || actor.IsCompany || actor.IsTechnician && request.TechnicianId == actor.Membership!.Id);
    }

    private static Task<ServiceRequest?> FindRequest(ServiceDb db, Tenant tenant, Guid id, CancellationToken ct) =>
        db.Requests.SingleOrDefaultAsync(x => x.TenantId == tenant.Id && x.Id == id, ct);

    private static Task<int> Lock(ServiceDb db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);

    private static bool Valid(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Contains('\0');

    private static bool ValidMultilineText(string? value, int maximum) =>
        Valid(value, maximum) && !value!.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    private static string Tag(int revision) => $"\"r{revision.ToString(CultureInfo.InvariantCulture)}\"";

    private static IResult Replay(HttpContext http, Command previous, byte[] fingerprint)
    {
        if (!CryptographicOperations.FixedTimeEquals(previous.Fingerprint, fingerprint))
            return ApiProblem.Error(409, "idempotency_conflict", "Dieser Schlüssel wurde bereits für eine andere Anfrage verwendet.");
        if (previous.Revision is int revision)
            http.Response.Headers.ETag = Tag(revision);
        if (previous.Location is { } location)
            http.Response.Headers.Location = location;
        return Results.Content(previous.Response, "application/json", statusCode: previous.Status);
    }
}

public sealed record NewRequest(string Title, string Description, string Email);
public sealed record ClaimBody(string Token);
public sealed record RequestCommand(string Action, Guid? TechnicianId, DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt, string? ArrivalStatus, string? StartsAtLocal, string? EndsAtLocal, string? StartsAtFold,
    string? EndsAtFold, string? Note);
public sealed record NewMessage(string Body);
