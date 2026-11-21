using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private static async Task<IResult> AdminSetupStatus(string slug, HttpContext http, IConfiguration config, CancellationToken ct)
    {
        if (Operator(http, config) is null)
            return ApiProblem.Denied();
        await using var db = OpenPlatform(config);
        if (db is null)
            return PlatformUnavailable();
        if (await db.Tenants.SingleOrDefaultAsync(x => x.Slug == slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        var state = await db.Database.SqlQuery<string>($"SELECT ts.initial_admin_state({tenant.Id}) AS \"Value\"").SingleAsync(ct);
        return Results.Ok(new { state });
    }

    private static async Task<IResult> InviteInitialAdmin(string slug, InitialAdminInvite body, HttpContext http,
        IConfiguration config, IDataProtectionProvider protection, CancellationToken ct)
    {
        if (Operator(http, config) is not { } actor)
            return ApiProblem.Denied();
        if (!IdentitySessions.HasPrivilegedSettings(config) || !Valid(body.Name, 120) || body.Name.Any(char.IsControl) ||
            !Valid(body.Email, 254) || !MailAddress.TryCreate(body.Email, out var address) || address.Address != body.Email)
            return ApiProblem.Error(422, "invalid_invitation", "Bitte prüfen Sie Name und E-Mail-Adresse.");
        if (!Uri.TryCreate(config["PublicBaseUrl"], UriKind.Absolute, out var publicBase) || publicBase.Scheme != "https" ||
            !NativeIdentity.MailHeld(config) && string.IsNullOrWhiteSpace(config["Mail:Host"]))
            return ApiProblem.Error(503, "mail_unavailable", "Einladungen per E-Mail sind derzeit nicht verfügbar.");
        if (!Guid.TryParseExact(http.Request.Headers["Idempotency-Key"], "D", out var key))
            return ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");
        await using var db = OpenPlatform(config);
        if (db is null)
            return PlatformUnavailable();
        if (await db.Tenants.SingleOrDefaultAsync(x => x.Slug == slug, ct) is not { Active: true } tenant)
            return ApiProblem.NotFound();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, $"platform:{slug}", ct);
        if (NativeIdentity.MailHeld(config))
            await db.Database.ExecuteSqlRawAsync("SELECT set_config('taskservice.mail_held','on',true)", ct);
        var operation = $"bootstrap:{tenant.Id}";
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, Json));
        if (await db.PlatformCommands.FindAsync([actor, operation, key], ct) is { } existing)
            return PlatformResponse(http, existing, fingerprint);

        var id = Guid.NewGuid();
        var raw = RandomNumberGenerator.GetBytes(32);
        var expires = DateTimeOffset.UtcNow.AddHours(48);
        var link = new Uri(publicBase, $"/unternehmen/{tenant.Slug}/einladungen/{id}#{Convert.ToHexStringLower(raw)}");
        var mail = Workflow.ProtectMail(protection, address.Address, "Einladung zur Unternehmensverwaltung",
            $"Melden Sie sich mit Ihrem eingeladenen Konto und dem zweiten Faktor an: {link}");
        var email = address.Address.ToLowerInvariant();
        var name = body.Name.Trim();
        var digest = SHA256.HashData(raw);
        if (!await db.Database.SqlQuery<bool>
            ($"SELECT ts.issue_initial_admin({tenant.Id},{id},{name},{email},{digest},{expires},{mail},{actor}) AS \"Value\"").SingleAsync(ct))
            return ApiProblem.Error(409, "admin_setup_unavailable", "Es gibt bereits eine aktive Administration oder Einladung.");

        var command = new PlatformCommand(actor, operation, key, fingerprint,
            JsonSerializer.Serialize(new { id, expiresAt = expires }, Json), tenant.Revision, 201);
        db.PlatformCommands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PlatformResponse(http, command, fingerprint);
    }
}

public sealed record InitialAdminInvite(string Name, string Email);
