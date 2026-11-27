using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private static readonly string[] PlanScopes = ["global", "essential", "professional", "enterprise", "custom"];

    private static async Task<IResult> ReadPolicy(string scope, HttpContext http, IConfiguration config, CancellationToken ct)
    {
        if (Operator(http, config) is null)
            return ApiProblem.Denied();
        await using var db = OpenPlatform(config);
        if (db is null)
            return PlatformUnavailable();
        var plan = await db.Set<FeaturePlan>().FindAsync([scope], ct);
        if (plan is null)
            return ApiProblem.NotFound();
        var history = await db.Set<FeaturePolicyHistory>().Where(h => h.Scope == scope)
            .OrderByDescending(h => h.Revision).Take(20).ToArrayAsync(ct);
        return Results.Ok(new { plan.Revision, plan.Withheld, History = history,
            Features = ElectricalFeatures.Services.Select(s => new { s.Key, s.Label })
                .Prepend(new { Key = ElectricalFeatures.Intake, Label = "Kundenanfragen" }) });
    }

    private static async Task<IResult> SavePolicy(string scope, PolicyUpdate body, HttpContext http, IConfiguration config, CancellationToken ct)
    {
        if (Operator(http, config) is not { } actor)
            return ApiProblem.Denied();
        if (!PlanScopes.Contains(scope))
            return ApiProblem.NotFound();
        if (body.Withheld is not { } input || input.Length > ElectricalFeatures.Services.Count + 1 ||
            input.Any(key => key is null || !ElectricalFeatures.IsKnown(key)) || input.Distinct().Count() != input.Length)
            return ApiProblem.Error(422, "invalid_policy", "Die Funktionsauswahl ist ungültig.");
        var withheld = input.Order(StringComparer.Ordinal).ToArray();
        var tag = http.Request.Headers.IfMatch.ToString();
        if (tag.Length == 0)
            return ApiProblem.Error(428, "precondition_required", "Bitte laden Sie die aktuelle Konfiguration.");
        if (!Guid.TryParseExact(http.Request.Headers["Idempotency-Key"], "D", out var key))
            return ApiProblem.Error(400, "invalid_idempotency_key", "Ein gültiger Idempotenzschlüssel ist erforderlich.");
        await using var db = OpenPlatform(config);
        if (db is null)
            return PlatformUnavailable();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, "electrical:policy", ct);
        var operation = "policy:" + scope;
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { scope, withheld, tag }, Json));
        if (await db.PlatformCommands.FindAsync([actor, operation, key], ct) is { } replay)
            return PlatformResponse(http, replay, hash);
        var plan = await db.Set<FeaturePlan>().SingleAsync(p => p.Code == scope, ct);
        if (Tag(plan.Revision) != tag)
            return ApiProblem.Error(412, "stale_revision", "Die Konfiguration wurde inzwischen geändert.");
        plan.Withheld = withheld;
        plan.Revision++;
        db.Add(new FeaturePolicyHistory(scope, plan.Revision, actor, withheld, DateTimeOffset.UtcNow));
        var result = new PlatformCommand(actor, operation, key, hash,
            JsonSerializer.Serialize(new { plan.Revision, plan.Withheld }, Json), plan.Revision, 200);
        db.PlatformCommands.Add(result);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PlatformResponse(http, result, hash);
    }
}

public sealed record PolicyUpdate(string[]? Withheld);
