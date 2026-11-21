using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private static readonly string[] CompanyPermissions =
        ["report.create", "report.approve", "report.publish", "invoice.upload", "invoice.read", "invoice.approve", "invoice.publish"];
    private static readonly string[] TechnicianPermissions = ["invoice.upload", "invoice.read"];

    private static void MapTeam(RouteGroupBuilder group)
    {
        group.MapGet("/team", ReadTeam);
        group.MapPost("/team/invitations", InviteMember).WithMetadata(new RequestSizeLimitAttribute(4096)).RequireRateLimiting("invitations");
        group.MapPost("/team/invitations/{id:guid}/revoke", RevokeInvitation);
        group.MapPost("/team/invitations/{id:guid}/accept", AcceptInvitation)
            .RequireRateLimiting("guest").WithMetadata(new RequestSizeLimitAttribute(1024));
        group.MapPost("/team/members/{id:guid}/commands", ChangeMember).WithMetadata(new RequestSizeLimitAttribute(4096));
    }

    private static async Task<IResult> ReadTeam(ServiceDb db, string slug, Guid? cursor, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        if ((await Actor.Read(db, tenant, http, ct)).Membership?.Role != "admin")
            return ApiProblem.Denied();
        var query = db.Members.AsNoTracking().Where(m => m.TenantId == tenant.Id);
        if (cursor is { } after)
            query = query.Where(x => x.Id.CompareTo(after) > 0);
        var page = await query.OrderBy(m => m.Id).Take(51)
            .Select(m => new { m.Id, m.Name, m.Role, m.Active, m.Revision }).ToArrayAsync(ct);
        var members = page.Take(50).ToArray();
        var ids = members.Select(m => m.Id).ToArray();
        var reports = await db.ReportPermissions.Where(p => p.TenantId == tenant.Id && ids.Contains(p.MemberId))
            .Select(x => new { x.MemberId, x.Permission }).ToArrayAsync(ct);
        var invoices = await db.InvoicePermissions.Where(p => p.TenantId == tenant.Id && ids.Contains(p.MemberId))
            .Select(x => new { x.MemberId, x.Permission }).ToArrayAsync(ct);
        var permissions = reports.Concat(invoices).ToLookup(p => p.MemberId, p => p.Permission);
        var now = DateTimeOffset.UtcNow;
        var pending = await db.MemberInvites.Where(x => x.TenantId == tenant.Id && x.ExpiresAt > now &&
                x.UsedAt == null && x.RevokedAt == null)
            .OrderBy(x => x.ExpiresAt).Take(50).Select(x => new { x.Id, x.Name, x.Email, x.Role, x.ExpiresAt }).ToArrayAsync(ct);
        return Results.Ok(new
        {
            members = members.Select(m => new
            {
                m.Id, m.Name, m.Role, m.Active, m.Revision, Permissions = permissions[m.Id].Order(),
                AllowedPermissions = AllowedPermissions(m.Role)
            }),
            invitations = pending,
            nextCursor = page.Length == 51 ? members[^1].Id : (Guid?)null
        });
    }

    private static async Task<IResult> InviteMember(ServiceDb db, string slug, NewMemberInvite body, HttpContext http,
        IConfiguration config, IDataProtectionProvider protection, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (actor.Membership?.Role != "admin")
            return ApiProblem.Denied();
        if (body.Role is not ("dispatcher" or "technician") || !Valid(body.Name, 120) || body.Name.Any(char.IsControl) ||
            !Valid(body.Email, 254) || !MailAddress.TryCreate(body.Email, out var address) || address.Address != body.Email)
            return ApiProblem.Error(422, "invalid_invitation", "Bitte prüfen Sie Name, E-Mail-Adresse und Rolle.");
        if (!Uri.TryCreate(config["PublicBaseUrl"], UriKind.Absolute, out var publicBase) ||
            publicBase.Scheme != "https" || string.IsNullOrWhiteSpace(config["Mail:Host"]))
            return ApiProblem.Error(503, "mail_unavailable", "Einladungen per E-Mail sind derzeit nicht verfügbar.");

        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (!Guid.TryParseExact(key, "D", out _))
            return ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");
        const string operation = "team.invite";
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, Json));
        await Lock(db, $"{tenant.Id}:team", ct);
        if (await db.Commands.FindAsync([tenant.Id, actor.Key, operation, key], ct) is { } previous)
            return Replay(http, previous, fingerprint);

        var email = address.Address.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var existing = await db.MemberInvites.SingleOrDefaultAsync(x => x.TenantId == tenant.Id && x.Email == email &&
            x.UsedAt == null && x.RevokedAt == null, ct);
        if (existing?.ExpiresAt > now)
            return ApiProblem.Error(409, "invitation_exists", "Für diese E-Mail-Adresse ist bereits eine Einladung offen.");
        if (existing is not null)
        {
            existing.RevokedAt = now;
            await db.SaveChangesAsync(ct);
        }

        var raw = RandomNumberGenerator.GetBytes(32);
        var invite = new MemberInvite(tenant.Id, Guid.NewGuid(), body.Name.Trim(), email, body.Role,
            SHA256.HashData(raw), now.AddHours(48));
        db.MemberInvites.Add(invite);
        var link = new Uri(publicBase, $"/unternehmen/{tenant.Slug}/einladungen/{invite.Id}#{Convert.ToHexStringLower(raw)}");
        Workflow.QueueMail(db, tenant.Id, protection, address.Address, "Einladung zum TaskService-Arbeitsbereich",
            $"Melden Sie sich mit dem eingeladenen Konto an, um die Einladung anzunehmen: {link}");
        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, operation, invite.Id, now));
        var command = new Command(tenant.Id, actor.Key, operation, key, fingerprint, invite.Id,
            JsonSerializer.Serialize(new { invite.Id, invite.ExpiresAt }, Json), null, null, 201);
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Replay(http, command, fingerprint);
    }

    private static async Task<IResult> RevokeInvitation(ServiceDb db, string slug, Guid id, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (actor.Membership?.Role != "admin")
            return ApiProblem.Denied();
        await Lock(db, $"{tenant.Id}:team", ct);
        if (await db.MemberInvites.FindAsync([tenant.Id, id], ct) is not { UsedAt: null } invite || invite.Role == "admin")
            return ApiProblem.NotFound();
        if (invite.RevokedAt is not null)
            return Results.NoContent();
        invite.RevokedAt = DateTimeOffset.UtcNow;
        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, "team.invite.revoke", id, invite.RevokedAt.Value));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AcceptInvitation(ServiceDb db, string slug, Guid id, ClaimBody body, HttpContext http,
        CancellationToken ct)
    {
        var email = http.User.FindFirstValue("email");
        if (!IdentitySessions.IsWorkforce(http.User) || Actor.Subject(http.User) is not { } actorKey || email is not { Length: > 0 and <= 254 } ||
            !bool.TryParse(http.User.FindFirstValue("email_verified"), out var verified) || !verified ||
            body.Token is not { Length: 64 } || !body.Token.All(Uri.IsHexDigit) ||
            await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();

        await using var tx = await db.ForTenant(tenant.Id, ct);
        await Lock(db, $"{tenant.Id}:team", ct);
        var now = DateTimeOffset.UtcNow;
        if (await db.MemberInvites.FindAsync([tenant.Id, id], ct) is not { UsedAt: null, RevokedAt: null } invite ||
            invite.ExpiresAt <= now || !string.Equals(invite.Email, email, StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(invite.Digest, SHA256.HashData(Convert.FromHexString(body.Token))))
            return ApiProblem.NotFound();
        if (invite.Role == "admin")
        {
            var requiredAcr = http.RequestServices.GetRequiredService<IConfiguration>()["Identity:PrivilegedAcr"];
            if (string.IsNullOrEmpty(requiredAcr) || http.User.FindFirstValue(IdentitySessions.AssuranceClaim) != requiredAcr)
                return ApiProblem.Error(403, "assurance_required", "Bestätigen Sie die Anmeldung mit dem zweiten Faktor.");
            if (await db.Members.AnyAsync(x => x.TenantId == tenant.Id && x.Role == "admin" && x.Active, ct))
                return ApiProblem.Error(409, "admin_exists", "Das Unternehmen verfügt bereits über eine Administration.");
        }
        if (await db.Members.AnyAsync(x => x.TenantId == tenant.Id && x.SubjectKey == actorKey, ct))
            return ApiProblem.Error(409, "already_member", "Für dieses Konto besteht bereits eine Mitgliedschaft.");
        db.Members.Add(new(tenant.Id, Guid.NewGuid(), actorKey, invite.Name, invite.Role, true));
        invite.UsedAt = now;
        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actorKey, "team.invite.accept", id, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { invite.Role });
    }

    private static async Task<IResult> ChangeMember(ServiceDb db, string slug, Guid id, MemberChange body, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (actor.Membership?.Role != "admin")
            return ApiProblem.Denied();
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        var tag = http.Request.Headers.IfMatch.ToString();
        if (!Guid.TryParseExact(key, "D", out _))
            return ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");
        if (string.IsNullOrEmpty(tag))
            return ApiProblem.Error(428, "missing_revision", "Laden Sie zuerst die aktuelle Mitgliedschaft.");

        await Lock(db, $"{tenant.Id}:team", ct);
        if (await db.Members.FindAsync([tenant.Id, id], ct) is not { } member)
            return ApiProblem.NotFound();
        var operation = $"team.member:{id}";
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { body, tag }, Json));
        if (await db.Commands.FindAsync([tenant.Id, actor.Key, operation, key], ct) is { } previous)
            return Replay(http, previous, fingerprint);
        if (tag != Tag(member.Revision))
            return ApiProblem.Error(412, "stale_member", "Die Mitgliedschaft wurde zwischenzeitlich geändert.");

        var allowed = AllowedPermissions(member.Role);
        if (body.Permissions is null || body.Permissions.Length > allowed.Length ||
            body.Permissions.Distinct().Count() != body.Permissions.Length ||
            body.Permissions.Any(p => !allowed.Contains(p)) || member.Role == "admin" && !body.Active)
            return ApiProblem.Error(422, "invalid_member_rights", "Diese Rollenrechte oder die Statusänderung sind nicht zulässig.");

        await db.ReportPermissions.Where(x => x.TenantId == tenant.Id && x.MemberId == id).ExecuteDeleteAsync(ct);
        await db.InvoicePermissions.Where(x => x.TenantId == tenant.Id && x.MemberId == id).ExecuteDeleteAsync(ct);
        foreach (var permission in body.Permissions)
        {
            if (permission.StartsWith("report.", StringComparison.Ordinal))
                db.ReportPermissions.Add(new(tenant.Id, id, permission));
            else
                db.InvoicePermissions.Add(new(tenant.Id, id, permission));
        }

        member.Active = body.Active;
        member.Revision++;
        db.Audit.Add(new(tenant.Id, Guid.NewGuid(), actor.Key, "team.member.update", id, DateTimeOffset.UtcNow));
        var command = new Command(tenant.Id, actor.Key, operation, key, fingerprint, id,
            JsonSerializer.Serialize(new { id, member.Active, member.Revision }, Json), null, member.Revision, 200);
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Replay(http, command, fingerprint);
    }

    private static string[] AllowedPermissions(string role) => role == "technician" ? TechnicianPermissions : CompanyPermissions;
}

public sealed record NewMemberInvite(string Name, string Email, string Role);
public sealed record MemberChange(bool Active, string[] Permissions);
