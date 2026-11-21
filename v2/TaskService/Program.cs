using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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

if (args is ["worker"])
{
    builder.Services.AddDbContext<ServiceDb>(options => options.UseNpgsql(Connection("Worker")));
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
    options.AddPolicy("invitations", http => RateLimitPartition.GetFixedWindowLimiter(
        Actor.Subject(http.User) ?? http.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(10),
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
var identityConfigured = new[] { "Authority", "ClientId", "ClientSecret" }
    .All(key => !string.IsNullOrWhiteSpace(builder.Configuration[$"Identity:{key}"]));

var authentication = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-taskservice";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(4);
        options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
    });
if (identityConfigured)
{
    authentication.AddOpenIdConnect(options =>
    {
        options.Authority = builder.Configuration["Identity:Authority"];
        options.ClientId = builder.Configuration["Identity:ClientId"];
        options.ClientSecret = builder.Configuration["Identity:ClientSecret"];
        options.ResponseType = "code";
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.Scope.Add("email");
        options.GetClaimsFromUserInfoEndpoint = builder.Configuration.GetValue<bool>("Identity:UseUserInfo");
        if (options.GetClaimsFromUserInfoEndpoint)
            options.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                if (context.Properties.Items.TryGetValue("taskservice:workforce", out var workforce) && workforce == "yes" &&
                    builder.Configuration["Identity:PrivilegedAcr"] is { Length: > 0 } acr)
                    context.ProtocolMessage.AcrValues = acr;
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is not ClaimsIdentity identity ||
                    context.Principal.FindFirst("sub") is not { Value.Length: > 0 })
                    context.Fail("OIDC subject required.");
                else
                    identity.AddClaim(new System.Security.Claims.Claim("ts:issuer", context.SecurityToken.Issuer));
                return Task.CompletedTask;
            }
        };
    });
}
builder.Services.AddAuthorization();

var app = builder.Build();
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
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (ServiceDb db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) && identityConfigured
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(503));
app.MapGet("/api/v1/session", (HttpContext http, IAntiforgery antiforgery) =>
    Results.Ok(new { signedIn = Actor.Subject(http.User) is not null, csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken }));
app.MapGet("/api/v1/login", (string? returnTo, string? mode) =>
{
    if (!identityConfigured)
        return ApiProblem.Error(503, "identity_unavailable", "Die Anmeldung ist noch nicht konfiguriert.");
    var properties = new AuthenticationProperties { RedirectUri = SafeReturnPath(returnTo) };
    if (mode == "workforce")
        properties.Items["taskservice:workforce"] = "yes";
    return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
});
app.MapPost("/api/v1/logout", () => Results.SignOut(authenticationSchemes:
    [CookieAuthenticationDefaults.AuthenticationScheme])).AddEndpointFilter<CsrfFilter>();
app.MapTaskService();
app.MapHub<ConversationHub>("/hubs/conversations", options =>
{
    options.CloseOnAuthenticationExpiration = true;
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
});
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
