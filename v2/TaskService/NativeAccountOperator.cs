using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static class NativeAccountOperator
{
    public static async Task Run(ServiceDb db, IDataProtectionProvider protection, TextReader input, CancellationToken ct)
    {
        var command = await JsonSerializer.DeserializeAsync<AccountCommand>(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(await input.ReadToEndAsync(ct))),
            new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
        if (command is null || NativeIdentity.NormalizeEmail(command.Email) is not { } email ||
            !NativeIdentity.ValidPassword(command.Password) || command.Action is not ("provision" or "recover"))
            throw new InvalidOperationException("Invalid operator input.");

        var now = DateTimeOffset.UtcNow;
        if (command.Action == "recover")
        {
            await using var recovery = await db.Database.BeginTransactionAsync(ct);
            var accounts = await db.NativeAccounts.FromSqlInterpolated(
                $"SELECT * FROM ts.native_accounts WHERE email={email} FOR UPDATE").ToListAsync(ct);
            var existing = accounts.SingleOrDefault() ?? throw new InvalidOperationException("Account not found.");
            if (command.TotpSecretHex is not null)
            {
                if (!existing.Workforce)
                    throw new InvalidOperationException("This account has no workforce enrollment.");
                existing.TotpCipher = ProtectTotp(command.TotpSecretHex, protection);
                existing.LastTotpStep = null;
            }
            existing.PasswordHash = NativeIdentity.HashPassword(existing, command.Password!);
            existing.Failures = 0;
            existing.LockUntil = null;
            await db.NativeSessions.Where(x => x.AccountId == existing.Id && x.RevokedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, now), ct);
            db.NativeAccountAudit.Add(new(Guid.NewGuid(), existing.Id, "recovered", now));
            await db.SaveChangesAsync(ct);
            await recovery.CommitAsync(ct);
            return;
        }

        if (command.Role is not ("customer" or "platform" or "admin" or "dispatcher" or "technician") ||
            command.Role is ("platform" or "admin" or "dispatcher") && command.TotpSecretHex is null ||
            command.Role == "customer" && command.TotpSecretHex is not null)
            throw new InvalidOperationException("A recognized role and applicable MFA enrollment are required.");

        var tenantRole = command.Role is "admin" or "dispatcher" or "technician";
        var tenant = tenantRole
            ? await db.Tenants.SingleAsync(t => t.Slug == command.Tenant && t.Active, ct) : null;
        await using var tx = tenant is null ? await db.Database.BeginTransactionAsync(ct) : await db.ForTenant(tenant.Id, ct);
        var account = new NativeAccount
        {
            Id = Guid.NewGuid(), Email = email, PasswordHash = "", Workforce = command.Role != "customer",
            PlatformAdmin = command.Role == "platform",
            TotpCipher = command.TotpSecretHex is null ? null : ProtectTotp(command.TotpSecretHex, protection)
        };
        account.PasswordHash = NativeIdentity.HashPassword(account, command.Password!);
        db.NativeAccounts.Add(account);
        if (tenant is not null)
        {
            var displayName = command.Name?.Trim();
            if (displayName is not { Length: >= 1 and <= 120 } || displayName.Any(char.IsControl))
                throw new InvalidOperationException("A valid workforce display name is required.");
            var subject = Actor.SubjectKey(NativeIdentity.Issuer, account.Id.ToString("D"));
            db.Members.Add(new(tenant.Id, Guid.NewGuid(), subject, displayName, command.Role, true));
        }
        db.NativeAccountAudit.Add(new(Guid.NewGuid(), account.Id, "provisioned", now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string ProtectTotp(string input, IDataProtectionProvider protection)
    {
        if (input.Length != 40 || !input.All(Uri.IsHexDigit))
            throw new InvalidOperationException("A 160-bit generated TOTP secret is required.");
        return NativeIdentity.TotpProtector(protection).Protect(input.ToUpperInvariant());
    }

    private sealed record AccountCommand(string Action, string? Email, string? Password, string? Role,
        string? Tenant, string? Name, string? TotpSecretHex);
}
