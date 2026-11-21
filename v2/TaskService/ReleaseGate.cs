using System.Net.Mail;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskService.Database;

namespace TaskService;

public static class ReleaseGate
{
    public static string? PrivacyNotice(IConfiguration config) => HttpsUrl(config["Privacy:NoticeUrl"])?.AbsoluteUri;

    public static bool HasSettings(IConfiguration config) =>
        PrivacyNotice(config) is not null &&
        HttpsUrl(config["PublicBaseUrl"]) is { AbsolutePath: "/", Query.Length: 0, Fragment.Length: 0 } &&
        HttpsUrl(config["Identity:Authority"]) is not null &&
        IdentitySessions.HasPrivilegedSettings(config) &&
        !string.IsNullOrWhiteSpace(config.GetConnectionString("Platform")) &&
        config["Routing:DefaultTenantSlug"] is { Length: > 0 and <= 63 } &&
        !string.IsNullOrWhiteSpace(config["Mail:Host"]) && MailAddress.TryCreate(config["Mail:From"], out _) &&
        !string.IsNullOrWhiteSpace(config["ClamAv:Host"]);

    private static Uri? HttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 ? uri : null;

    public static async Task<bool> IsReady(ServiceDb db, IConfiguration config,
        IOptionsMonitor<OpenIdConnectOptions> oidc, CancellationToken ct)
    {
        if (!HasSettings(config))
            return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var token = deadline.Token;
            var slug = config["Routing:DefaultTenantSlug"];
            if (!await db.Tenants.AnyAsync(t => t.Active && t.Slug == slug, token))
                return false;

            var identities = await oidc.Get(OpenIdConnectDefaults.AuthenticationScheme).ConfigurationManager!
                .GetConfigurationAsync(token).WaitAsync(token);
            if (HttpsUrl(identities.AuthorizationEndpoint) is null || HttpsUrl(identities.TokenEndpoint) is null ||
                identities.SigningKeys.Count == 0)
                return false;

            using var scanner = new TcpClient();
            await scanner.ConnectAsync(config["ClamAv:Host"]!, config.GetValue("ClamAv:Port", 3310), token);
            await using var stream = scanner.GetStream();
            await stream.WriteAsync("zPING\0"u8.ToArray(), token);
            var reply = new byte[5];
            await stream.ReadExactlyAsync(reply, token);
            if (!reply.AsSpan().SequenceEqual("PONG\0"u8))
                return false;
            await ClamAvScanner.RequireCurrentSignatures(config, token);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            return false;
        }
    }
}
