using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace TaskService;

public static class IdentitySessions
{
    public const string Selector = "TaskServiceSession";
    public const string WorkforceCookie = "TaskServiceWorkforce";
    public const string WorkforceOidc = "TaskServiceWorkforceOidc";
    public const string WorkforceClaim = "workforce";
    public const string AssuranceClaim = "ts:assurance";
    public const string ScopeHeader = "X-TaskService-Session";

    public static string CookieFor(HttpContext http) =>
        http.Request.Path.StartsWithSegments("/api/v1/platform") ||
        http.Request.Headers[ScopeHeader] == WorkforceClaim ||
        http.Request.Query["session"] == WorkforceClaim
            ? WorkforceCookie
            : CookieAuthenticationDefaults.AuthenticationScheme;

    public static bool IsWorkforce(ClaimsPrincipal principal) => principal.FindFirstValue("ts:realm") == WorkforceClaim;

    public static void ConfigureCookie(CookieAuthenticationOptions options, bool workforce)
    {
        options.Cookie.Name = workforce ? "__Host-taskservice-workforce" : "__Host-taskservice";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(workforce ? 1 : 4);
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
    }

    public static void ConfigureOidc(OpenIdConnectOptions options, IConfiguration config, bool workforce)
    {
        options.Authority = config["Identity:Authority"];
        options.ClientId = config["Identity:ClientId"];
        options.ClientSecret = config["Identity:ClientSecret"];
        options.SignInScheme = workforce ? WorkforceCookie : CookieAuthenticationDefaults.AuthenticationScheme;
        if (workforce)
        {
            options.CallbackPath = "/signin-oidc-workforce";
            options.SignedOutCallbackPath = "/signout-callback-oidc-workforce";
            options.RemoteSignOutPath = "/signout-oidc-workforce";
        }
        options.ResponseType = "code";
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.Scope.Add("email");
        options.GetClaimsFromUserInfoEndpoint = config.GetValue<bool>("Identity:UseUserInfo");
        if (options.GetClaimsFromUserInfoEndpoint)
            options.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                if (workforce && config["Identity:PrivilegedAcr"] is { Length: > 0 } acr)
                    context.ProtocolMessage.AcrValues = acr;
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is not ClaimsIdentity identity ||
                    context.Principal.FindFirst("sub") is not { Value.Length: > 0 })
                {
                    context.Fail("OIDC subject required.");
                    return Task.CompletedTask;
                }
                var assurance = identity.FindFirst("acr")?.Value;
                foreach (var claim in identity.Claims.Where(c => c.Type is "ts:issuer" or "ts:realm" or AssuranceClaim).ToArray())
                    identity.RemoveClaim(claim);
                identity.AddClaim(new("ts:issuer", context.SecurityToken.Issuer));
                identity.AddClaim(new("ts:realm", workforce ? WorkforceClaim : "customer"));
                if (workforce && assurance is { Length: > 0 })
                    identity.AddClaim(new(AssuranceClaim, assurance));
                return Task.CompletedTask;
            }
        };
    }
}
