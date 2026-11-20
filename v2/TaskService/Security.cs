using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public sealed class CsrfFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method))
            return await next(context);

        var origin = http.Request.Headers.Origin.ToString();
        var expected = $"{http.Request.Scheme}://{http.Request.Host}";
        if (!string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase) ||
            !await antiforgery.IsRequestValidAsync(http))
            return ApiProblem.Error(403, "invalid_request_origin", "Die Anfrage konnte nicht bestätigt werden.");
        return await next(context);
    }
}

public sealed record Actor(string Key, Member? Membership)
{
    public bool IsCompany => Membership?.Role is "admin" or "dispatcher";
    public bool IsTechnician => Membership?.Role == "technician";

    public bool CanRead(ServiceRequest request) =>
        IsCompany || IsTechnician && request.TechnicianId == Membership!.Id ||
        Key != Guest.Key && request.CustomerKey == Key;

    public static Actor Guest { get; } = new("guest", null);

    public static string? Subject(ClaimsPrincipal principal)
    {
        var issuer = principal.FindFirstValue("ts:issuer");
        var subject = principal.FindFirstValue("sub");
        return principal.Identity?.IsAuthenticated == true && issuer is { Length: > 0 } && subject is { Length: > 0 }
            ? SubjectKey(issuer, subject)
            : null;
    }

    public static string SubjectKey(string issuer, string subject) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer.Length}:{issuer}{subject}")));

    public static async Task<Actor> Read(ServiceDb db, Tenant tenant, HttpContext http, CancellationToken ct)
    {
        if (Subject(http.User) is not { } key)
            return Guest;
        var member = await db.Members.AsNoTracking()
            .SingleOrDefaultAsync(m => m.TenantId == tenant.Id && m.SubjectKey == key && m.Active, ct);
        var privilegedAcr = http.RequestServices.GetRequiredService<IConfiguration>()["Identity:PrivilegedAcr"];
        if (member?.Role is "admin" or "dispatcher" &&
            (string.IsNullOrEmpty(privilegedAcr) || http.User.FindFirstValue("acr") != privilegedAcr))
            member = null;

        return new(key, member);
    }
}

public static class ApiProblem
{
    public static IResult Error(int status, string code, string detail) =>
        Results.Problem(statusCode: status, type: $"urn:taskservice:problem:{code}", detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
    public static IResult NotFound() => Error(404, "not_found", "Der angeforderte Vorgang wurde nicht gefunden.");
    public static IResult Denied() => Error(403, "forbidden", "Sie haben keine Berechtigung für diese Aktion.");
}
