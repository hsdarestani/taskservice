using System.Buffers.Binary;
using System.Globalization;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static class NativeIdentity
{
    internal const string Issuer = "urn:taskservice:local:v1";
    internal const string MfaAssurance = "local-totp";
    internal const string DemoPasswordOnlyAssurance = "demo-password-only";
    private const string SessionClaim = "ts:session";
    private const string RecoveryMessage = "Diese Funktion ist in der Demoversion noch nicht verfügbar.";
    private static readonly SemaphoreSlim PasswordSlots = new(2);
    private static readonly PasswordHasher<NativeAccount> Hasher = new(
        Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
    private static readonly NativeAccount Unlisted = new() { Email = "", PasswordHash = "" };
    private static readonly string DummyHash = Hasher.HashPassword(Unlisted, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    internal static bool Enabled(IConfiguration config) => config["Identity:Mode"] == "native";
    internal static bool Demo(IConfiguration config) => config.GetValue<bool>("Demo:Enabled");
    // Deliberately limited to synthetic demo accounts; never applies to real staff or customers.
    internal static bool DemoPasswordOnly(IConfiguration config, string? email) =>
        Demo(config) && config.GetValue<bool>("Identity:DemoPasswordOnly") &&
        email is not null && email.EndsWith("@taskservice-demo.invalid", StringComparison.OrdinalIgnoreCase);
    internal static bool MailHeld(IConfiguration config) => config["Mail:DeliveryMode"] == "disabled";
    internal static bool RecoveryAvailable(IConfiguration config) => config["Identity:RecoveryMode"] == "external" &&
        Uri.TryCreate(config["Identity:RecoveryUrl"], UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);

    public static void MapNativeIdentity(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth").RequireRateLimiting("native-auth")
            .AddEndpointFilter<CsrfFilter>();
        auth.MapPost("/register", Register).WithMetadata(new RequestSizeLimitAttribute(4096));
        auth.MapPost("/login", Login).WithMetadata(new RequestSizeLimitAttribute(4096));
        auth.MapGet("/recovery", (IConfiguration config) => RecoveryAvailable(config)
            ? Results.Ok(new { url = config["Identity:RecoveryUrl"] })
            : Results.Ok(new { message = RecoveryMessage }));
    }

    private static async Task<IResult> Register(NativeCredentials input, HttpContext http, ServiceDb db, CancellationToken ct)
    {
        if (!http.Request.IsHttps)
            return ApiProblem.Denied();
        if (NormalizeEmail(input.Email) is not { } email || !ValidPassword(input.Password))
            return ApiProblem.Error(422, "invalid_registration", "Bitte geben Sie eine gültige E-Mail-Adresse und ein Passwort mit 12 bis 128 Zeichen ein.");
        if (!PasswordSlots.Wait(0))
            return Results.StatusCode(429);
        try
        {
            var account = new NativeAccount { Id = Guid.NewGuid(), Email = email, PasswordHash = "" };
            var hash = HashPassword(account, input.Password!);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO ts.native_accounts (id,email,password_hash) VALUES ({account.Id},{email},{hash}) ON CONFLICT (email) DO NOTHING", ct);
            return Results.NoContent();
        }
        finally
        {
            PasswordSlots.Release();
        }
    }

    private static async Task<IResult> Login(NativeCredentials input, HttpContext http, ServiceDb db,
        IDataProtectionProvider protection, IConfiguration config, CancellationToken ct)
    {
        if (!http.Request.IsHttps)
            return ApiProblem.Denied();
        if (NormalizeEmail(input.Email) is not { } email || input.Password is not { Length: >= 1 and <= 128 })
            return InvalidCredentials();
        if (!PasswordSlots.Wait(0))
            return Results.StatusCode(429);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var matches = await db.NativeAccounts.FromSqlInterpolated(
                $"SELECT * FROM ts.native_accounts WHERE email={email} FOR UPDATE").ToListAsync(ct);
            var account = matches.SingleOrDefault();
            var password = Hasher.VerifyHashedPassword(account ?? Unlisted, account?.PasswordHash ?? DummyHash, input.Password);
            if (account is null || account.LockUntil > now)
                return InvalidCredentials();

            var passwordValid = password != PasswordVerificationResult.Failed;
            var allowedRealm = !input.Workforce || account.Workforce;
            var demoPasswordOnly = input.Workforce && !account.PlatformAdmin && DemoPasswordOnly(config, account.Email);
            long? matchedStep = null;
            if (input.Workforce && !demoPasswordOnly && account.TotpCipher is { } encrypted && passwordValid && allowedRealm)
                matchedStep = VerifyTotp(protection, encrypted, input.Code, account.LastTotpStep, now);
            var mfaRequired = input.Workforce && account.TotpCipher is not null && !demoPasswordOnly;
            if (!passwordValid || !allowedRealm || mfaRequired && matchedStep is null)
            {
                if (account.LockUntil is not null)
                    account.Failures = 0;
                if (++account.Failures >= 5)
                    account.LockUntil = now.AddMinutes(10);
                else
                    account.LockUntil = null;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return InvalidCredentials();
            }

            account.Failures = 0;
            account.LockUntil = null;
            if (matchedStep is not null)
                account.LastTotpStep = matchedStep;
            if (password == PasswordVerificationResult.SuccessRehashNeeded)
                account.PasswordHash = HashPassword(account, input.Password);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM ts.native_sessions WHERE id IN (SELECT id FROM ts.native_sessions WHERE expires_at < {now} LIMIT 100)", ct);
            var expires = now.AddHours(input.Workforce ? 1 : 4);
            var session = new NativeSession(Guid.NewGuid(), account.Id, input.Workforce, expires);
            db.NativeSessions.Add(session);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var scheme = input.Workforce ? IdentitySessions.WorkforceCookie : CookieAuthenticationDefaults.AuthenticationScheme;
            List<System.Security.Claims.Claim> claims =
            [
                new("sub", account.Id.ToString("D")), new("ts:issuer", Issuer), new(SessionClaim, session.Id.ToString("D")),
                new("ts:realm", input.Workforce ? IdentitySessions.WorkforceClaim : "customer"),
                new("email", account.Email), new("email_verified", "false")
            ];
            if (input.Workforce && (matchedStep is not null || demoPasswordOnly))
            {
                // Never label password-only access as verified MFA.
                claims.Add(new(IdentitySessions.AssuranceClaim,
                    demoPasswordOnly ? DemoPasswordOnlyAssurance : MfaAssurance));
                if (account.PlatformAdmin)
                    claims.Add(new("taskservice_role", "platform_admin"));
            }
            await http.SignInAsync(scheme, new ClaimsPrincipal(new ClaimsIdentity(claims, scheme)),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = expires });
            return Results.NoContent();
        }
        finally
        {
            PasswordSlots.Release();
        }
    }

    internal static async Task ValidateCookie(CookieValidatePrincipalContext context, bool workforce)
    {
        var principal = context.Principal;
        var db = context.HttpContext.RequestServices.GetRequiredService<ServiceDb>();
        if (principal is null || !await IsCurrent(db, principal, workforce, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }

    internal static Task<bool> IsCurrent(ServiceDb db, ClaimsPrincipal principal, bool workforce, CancellationToken ct)
    {
        if (principal.FindFirstValue("ts:issuer") != Issuer ||
            !Guid.TryParse(principal.FindFirstValue(SessionClaim), out var id) ||
            !Guid.TryParse(principal.FindFirstValue("sub"), out var subject))
            return Task.FromResult(false);
        var now = DateTimeOffset.UtcNow;
        return db.NativeSessions.AnyAsync(x => x.Id == id && x.AccountId == subject && x.Workforce == workforce &&
            x.RevokedAt == null && x.ExpiresAt > now, ct);
    }

    internal static Task RevokeSession(ServiceDb db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (principal.FindFirstValue("ts:issuer") != Issuer ||
            !Guid.TryParse(principal.FindFirstValue(SessionClaim), out var id) ||
            !Guid.TryParse(principal.FindFirstValue("sub"), out var subject))
            return Task.CompletedTask;
        var now = DateTimeOffset.UtcNow;
        return db.NativeSessions.Where(x => x.Id == id && x.AccountId == subject)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    internal static string? NormalizeEmail(string? value)
    {
        var email = value?.Trim();
        if (email is not { Length: > 0 and <= 254 } || !MailAddress.TryCreate(email, out var address) || address.Address != email)
            return null;
        return email.ToLowerInvariant();
    }

    internal static bool ValidPassword(string? password) => password is { Length: >= 12 and <= 128 } && !password.Contains('\0');
    internal static string HashPassword(NativeAccount account, string password) => Hasher.HashPassword(account, password);
    internal static IDataProtector TotpProtector(IDataProtectionProvider protection) =>
        protection.CreateProtector("taskservice-native-totp-v1");

    private static long? VerifyTotp(IDataProtectionProvider protection, string encrypted, string? code, long? lastStep,
        DateTimeOffset now)
    {
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit))
            return null;
        var secret = Convert.FromHexString(TotpProtector(protection).Unprotect(encrypted));
        try
        {
            var current = now.ToUnixTimeSeconds() / 30;
            for (var step = current - 1; step <= current + 1; step++)
                if ((lastStep is null || step > lastStep) &&
                    CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(CodeAt(secret, step))))
                    return step;
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static string CodeAt(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> count = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(count, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, count, hash);
        var offset = hash[^1] & 0xf;
        var number = (BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7fffffff) % 1_000_000;
        return number.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static IResult InvalidCredentials() =>
        ApiProblem.Error(401, "invalid_credentials", "Anmeldung fehlgeschlagen. Prüfen Sie Ihre Zugangsdaten.");
}

public sealed record NativeCredentials(string? Email, string? Password, bool Workforce = false, string? Code = null);
