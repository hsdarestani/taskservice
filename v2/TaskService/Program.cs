using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskService.Database;
using TaskService;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

if (args is ["migrate"])
{
    var migration = builder.Configuration.GetConnectionString("Migration")
        ?? throw new InvalidOperationException("A separate Migration connection string is required.");
    await using var db = new ServiceDb(new DbContextOptionsBuilder<ServiceDb>().UseNpgsql(migration).Options);
    await db.Database.MigrateAsync();
    return;
}

var protection = builder.Services.AddDataProtection().SetApplicationName("TaskServiceV2");
if (builder.Configuration["DataProtection:KeyDirectory"] is { Length: > 0 } keys)
    protection.PersistKeysToFileSystem(new DirectoryInfo(keys));

string Connection(string name) => builder.Configuration.GetConnectionString(name)
    ?? throw new InvalidOperationException($"Missing {name} connection string.");

if (args is ["native-account"])
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["DataProtection:KeyDirectory"]))
        throw new InvalidOperationException("The shared Data Protection key directory is required.");
    await using var operatorHost = builder.Build();
    await using var db = new ServiceDb(new DbContextOptionsBuilder<ServiceDb>().UseNpgsql(Connection("Migration")).Options);
    try
    {
        await NativeAccountOperator.Run(db, operatorHost.Services.GetRequiredService<IDataProtectionProvider>(),
            Console.In, CancellationToken.None);
        Console.WriteLine("Account operation completed.");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Account operation stopped ({error.GetType().Name}).");
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["worker"])
{
    builder.Services.AddDbContext<ServiceDb>(options => options.UseNpgsql(Connection("Worker")));
    if (!NativeIdentity.MailHeld(builder.Configuration))
        builder.Services.AddHostedService<MailWorker>();
    builder.Services.AddHostedService<ReportWorker>();
    builder.Services.AddSingleton<InvoiceScanner, ClamAvScanner>();
    builder.Services.AddHostedService<InvoiceScanWorker>();
    await builder.Build().RunAsync();
    return;
}

builder.Services.AddDbContext<ServiceDb>(options => options.UseNpgsql(Connection("Runtime")));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSignalR(options =>
{
    options.MaximumParallelInvocationsPerClient = 1;
    options.MaximumReceiveMessageSize = 4 * 1024;
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = "__Host-taskservice-csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.Path = "/";
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddConcurrencyLimiter("invoice-upload", limiter => { limiter.PermitLimit = 3; limiter.QueueLimit = 0; });
    options.AddPolicy("native-auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 16, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("invitations", http => RateLimitPartition.GetFixedWindowLimiter(
        Actor.Subject(http.User) ?? http.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0
        }));
    options.AddPolicy("incoming-webhook", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 90,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("guest", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0
        }));
});
var nativeIdentity = NativeIdentity.Enabled(builder.Configuration);
var identityConfigured = !nativeIdentity && new[] { "Authority", "ClientId", "ClientSecret" }
    .All(key => !string.IsNullOrWhiteSpace(builder.Configuration[$"Identity:{key}"]));

var authentication = builder.Services.AddAuthentication(IdentitySessions.Selector)
    .AddPolicyScheme(IdentitySessions.Selector, "TaskService session", options => options.ForwardDefaultSelector = IdentitySessions.CookieFor)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options => ConfigureCookie(options, workforce: false))
    .AddCookie(IdentitySessions.WorkforceCookie, options => ConfigureCookie(options, workforce: true));
void ConfigureCookie(CookieAuthenticationOptions options, bool workforce)
{
    IdentitySessions.ConfigureCookie(options, workforce);
    if (nativeIdentity)
        options.Events.OnValidatePrincipal = context => NativeIdentity.ValidateCookie(context, workforce);
}
if (identityConfigured)
{
    authentication.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme,
        options => IdentitySessions.ConfigureOidc(options, builder.Configuration, workforce: false));
    authentication.AddOpenIdConnect(IdentitySessions.WorkforceOidc,
        options => IdentitySessions.ConfigureOidc(options, builder.Configuration, workforce: true));
}
builder.Services.AddAuthorization();

var app = builder.Build();
if (ProxyTrust.Options(app.Configuration) is { } proxyOptions)
    app.UseForwardedHeaders(proxyOptions);
if (app.Environment.IsProduction())
    app.UseHsts();
app.UseExceptionHandler();
app.Use(async (http, next) =>
{
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    http.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    if (http.Request.Path.StartsWithSegments("/api"))
        http.Response.Headers.CacheControl = "no-store";
    if (http.Request.Path.StartsWithSegments("/hubs"))
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (!string.Equals(origin, $"{http.Request.Scheme}://{http.Request.Host}", StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = 403;
            return;
        }
        http.Response.Headers.CacheControl = "no-store";
    }
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
var enforceRelease = app.Configuration.GetValue<bool>("Operations:RequireProductionReadiness");
app.MapGet("/health/ready", async (ServiceDb db, IOptionsMonitor<OpenIdConnectOptions> oidc, CancellationToken ct) =>
    (nativeIdentity || identityConfigured) && (enforceRelease ? await ReleaseGate.IsReady(db, app.Configuration, oidc, app.Logger, ct)
        : await db.Database.CanConnectAsync(ct))
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(503));
app.MapGet("/api/v1/session", (HttpContext http, IAntiforgery antiforgery) =>
    Results.Ok(new
    {
        signedIn = Actor.Subject(http.User) is not null,
        csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken,
        email = nativeIdentity ? http.User.FindFirst("email")?.Value : null,
        native = nativeIdentity,
        demo = NativeIdentity.Demo(app.Configuration),
        mailEnabled = !NativeIdentity.MailHeld(app.Configuration)
    }));
app.MapGet("/api/v1/login", (string? returnTo, string? mode) =>
{
    if (nativeIdentity)
        return Results.LocalRedirect("/anmelden?returnTo=" + Uri.EscapeDataString(SafeReturnPath(returnTo)) +
            (mode == IdentitySessions.WorkforceClaim ? "&mode=workforce" : ""));
    if (!identityConfigured)
        return ApiProblem.Error(503, "identity_unavailable", "Die Anmeldung ist noch nicht konfiguriert.");
    var properties = new AuthenticationProperties { RedirectUri = SafeReturnPath(returnTo) };
    var scheme = mode == IdentitySessions.WorkforceClaim ? IdentitySessions.WorkforceOidc : OpenIdConnectDefaults.AuthenticationScheme;
    return Results.Challenge(properties, [scheme]);
});
app.MapPost("/api/v1/logout", async (HttpContext http, ServiceDb db, CancellationToken ct) =>
{
    if (nativeIdentity)
        await NativeIdentity.RevokeSession(db, http.User, ct);
    // Cookie sign-out clears the selected realm only.
    await http.SignOutAsync(IdentitySessions.CookieFor(http));
    return Results.NoContent();
}).AddEndpointFilter<CsrfFilter>();
if (nativeIdentity)
    app.MapNativeIdentity();
app.MapTaskService();
app.MapHub<ConversationHub>("/hubs/conversations", options =>
{
    options.CloseOnAuthenticationExpiration = true;
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
});
if (app.Configuration["Routing:DefaultTenantSlug"] is { Length: > 0 and <= 63 } defaultTenant)
    app.MapGet("/", async (ServiceDb db, CancellationToken ct) =>
        await db.Tenants.AnyAsync(t => t.Active && t.Slug == defaultTenant, ct)
            ? Results.Redirect("/unternehmen/" + Uri.EscapeDataString(defaultTenant))
            : Results.StatusCode(503));
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
await app.RunAsync();

static string SafeReturnPath(string? value)
{
    if (!string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value[0] == '/' &&
        !value.StartsWith("//", StringComparison.Ordinal) && !value.Contains('\\') &&
        !value.Contains('%') && value.All(c => char.IsAsciiLetterOrDigit(c) || "/-".Contains(c)))
        return value;
    return "/";
}

public partial class Program;
