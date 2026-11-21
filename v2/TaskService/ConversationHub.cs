using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

[Authorize]
public sealed class ConversationHub(IServiceScopeFactory scopes) : Hub
{
    private static readonly ConcurrentDictionary<string, byte> ActiveStreams = new();
    public async IAsyncEnumerable<long> Watch(string slug, string channel, Guid? requestId, long cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var http = Context.GetHttpContext() ?? throw new HubException("Sitzung fehlt.");
        if (Actor.Subject(http.User) is null || cursor < 0 || !ActiveStreams.TryAdd(Context.ConnectionId, 0))
            throw new HubException("Kein Zugriff auf diesen Chat.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var latest = await CurrentSequence(http, slug, channel, requestId, cancellationToken);
                if (latest > cursor)
                {
                    cursor = latest;
                    yield return latest;
                }

                // REST rechecks access before returning any content.
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }
        }
        finally
        {
            ActiveStreams.TryRemove(Context.ConnectionId, out _);
        }
    }

    private async Task<long> CurrentSequence(HttpContext http, string slug, string channel, Guid? requestId,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDb>();
        if (NativeIdentity.Enabled(scope.ServiceProvider.GetRequiredService<IConfiguration>()) &&
            !await NativeIdentity.IsCurrent(db, http.User, IdentitySessions.IsWorkforce(http.User), ct))
            throw new HubException("Kein Zugriff auf diesen Chat.");
        var tenant = await Endpoints.FindTenant(db, slug, ct);
        if (tenant is null)
            throw new HubException("Kein Zugriff auf diesen Chat.");
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (!await Endpoints.CanReadRoom(db, tenant, actor, channel, requestId, ct))
            throw new HubException("Kein Zugriff auf diesen Chat.");
        return await db.Messages.Where(x => x.TenantId == tenant.Id && x.RequestId == requestId && x.Channel == channel)
            .Select(x => (long?)x.Sequence).MaxAsync(ct) ?? 0;
    }
}
