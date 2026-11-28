using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private static async Task<IResult> CompanyFeatures(string slug, HttpContext http, IConfiguration config, CancellationToken ct)
    {
        if (Operator(http, config) is null)
            return ApiProblem.Denied();
        await using var db = OpenPlatform(config);
        if (db is null)
            return PlatformUnavailable();
        if (await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, ct) is not { } tenant)
            return ApiProblem.NotFound();

        var policy = await ElectricalFeatures.LoadPolicy(db, tenant, ct);
        FeatureView Describe(string key, string label, string english, bool defaultEnabled, string? parent)
        {
            var state = tenant.FeatureOverrides.Contains("+" + key) ? "enabled" :
                tenant.FeatureOverrides.Contains("-" + key) ? "disabled" : "default";
            return new(key, label, english, defaultEnabled, state,
                policy.Available(key, defaultEnabled) && (parent is null || policy.IntakeEnabled), parent,
                policy.GloballyDisabled.Contains(key), policy.Unlicensed.Contains(key));
        }
        var items = ElectricalFeatures.Registry.Select(f => Describe(f.Key, f.Label, f.English, f.DefaultEnabled, f.Parent));
        return Results.Ok(new { tenant.Revision, Items = items });
    }

    private static void SetFeatureOverride(Tenant tenant, FeatureChange change)
    {
        var next = tenant.FeatureOverrides.Where(value => value[1..] != change.Key);
        if (change.State != "default")
            next = next.Append((change.State == "enabled" ? "+" : "-") + change.Key);
        tenant.FeatureOverrides = next.Order(StringComparer.Ordinal).ToArray();
    }
}

public sealed record FeatureView(string Key, string Label, string English, bool DefaultEnabled,
    string State, bool Effective, string? Parent, bool GloballyDisabled, bool Unlicensed);
public sealed record FeatureChange(string Key, string State);
