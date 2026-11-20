using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public sealed class MailWorker(IServiceScopeFactory scopes, IDataProtectionProvider protection,
    IConfiguration config, ILogger<MailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(config["Mail:Host"]) || !MailAddress.TryCreate(config["Mail:From"], out _))
            throw new InvalidOperationException("A mail host and sender are required by the verification worker.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                await Dispatch(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Provider exception messages can contain recipient addresses or message content.
                logger.LogError("Mail worker iteration failed ({ErrorType})", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task Dispatch(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDb>();
        var tenants = await db.Tenants.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Id)
            .Select(x => x.Id).ToArrayAsync(ct);
        foreach (var tenant in tenants)
        {
            ct.ThrowIfCancellationRequested();
            var lease = Guid.NewGuid();
            Outbox? item;
            await using (var tx = await db.ForTenant(tenant, ct))
            {
                var due = await db.Outbox.FromSqlInterpolated($"""
                    SELECT * FROM ts.outbox
                    WHERE tenant_id = {tenant} AND kind = 'mail' AND completed_at IS NULL AND dead_at IS NULL
                      AND available_at <= now() AND (leased_until IS NULL OR leased_until <= now())
                    ORDER BY available_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).ToListAsync(ct);
                item = due.SingleOrDefault();
                if (item is null)
                    continue;
                item.LeaseId = lease;
                item.LeasedUntil = DateTimeOffset.UtcNow.AddMinutes(2);
                item.Attempts++;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            var sent = await Send(item, ct);
            db.ChangeTracker.Clear();
            await using var resultTx = await db.ForTenant(tenant, ct);
            var now = DateTimeOffset.UtcNow;
            var retryAt = now.AddSeconds(Math.Min(1800, 10 * Math.Pow(2, Math.Min(item.Attempts, 7))) + Random.Shared.Next(10));
            var dead = !sent && item.Attempts >= 8;
            await db.Outbox.Where(x => x.TenantId == tenant && x.Id == item.Id && x.LeaseId == lease)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.LeaseId, (Guid?)null)
                    .SetProperty(x => x.LeasedUntil, (DateTimeOffset?)null)
                    .SetProperty(x => x.CompletedAt, sent ? now : (DateTimeOffset?)null)
                    .SetProperty(x => x.DeadAt, dead ? now : (DateTimeOffset?)null)
                    .SetProperty(x => x.AvailableAt, retryAt), ct);
            await resultTx.CommitAsync(ct);
            if (dead)
                logger.LogWarning("Mail outbox entry {EventId} entered the dead-letter state", item.Id);
        }
    }

    private async Task<bool> Send(Outbox item, CancellationToken ct)
    {
        try
        {
            var json = protection.CreateProtector("taskservice-outbox-mail-v1").Unprotect(item.Payload);
            var payload = JsonSerializer.Deserialize<MailPayload>(json)!;
            using var message = new MailMessage(config["Mail:From"]!, payload.To, payload.Subject, payload.Content);
            using var smtp = new SmtpClient(config["Mail:Host"]!, config.GetValue("Mail:Port", 587)) { EnableSsl = true };
            if (config["Mail:Username"] is { Length: > 0 } username)
                smtp.Credentials = new NetworkCredential(username, config["Mail:Password"]);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            await smtp.SendMailAsync(message, timeout.Token);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Mail outbox entry {EventId} could not be delivered ({ErrorType})",
                item.Id, ex.GetType().Name);
            return false;
        }
    }
}
