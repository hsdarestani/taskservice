using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public interface InvoiceScanner
{
    Task<bool> IsClean(byte[] content, CancellationToken ct);
}

public sealed class ClamAvScanner(IConfiguration config) : InvoiceScanner
{
    public async Task<bool> IsClean(byte[] content, CancellationToken ct)
    {
        var host = config["ClamAv:Host"];
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("No private malware scanner is configured.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var socket = new TcpClient();
        await socket.ConnectAsync(host, config.GetValue("ClamAv:Port", 3310), token);
        await using var stream = socket.GetStream();
        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), token);
        var length = new byte[4];
        for (var offset = 0; offset < content.Length; offset += 64 * 1024)
        {
            var frameSize = Math.Min(content.Length - offset, 64 * 1024);
            BinaryPrimitives.WriteInt32BigEndian(length, frameSize);
            await stream.WriteAsync(length, token);
            await stream.WriteAsync(content.AsMemory(offset, frameSize), token);
        }
        Array.Clear(length);
        await stream.WriteAsync(length, token);
        await stream.FlushAsync(token);

        var reply = new byte[1024];
        var filled = 0;
        while (filled < reply.Length)
        {
            var read = await stream.ReadAsync(reply.AsMemory(filled), token);
            if (read == 0)
                break;
            filled += read;
            var end = Array.IndexOf(reply, (byte)0, 0, filled);
            if (end == -1)
                continue;
            var response = Encoding.ASCII.GetString(reply, 0, end);
            if (response == "stream: OK")
                return true;
            if (response.StartsWith("stream: ", StringComparison.Ordinal) &&
                response.EndsWith(" FOUND", StringComparison.Ordinal))
                return false;
            break;
        }
        throw new IOException("The scanner did not return a recognized verdict.");
    }
}

public sealed class InvoiceScanWorker(IServiceScopeFactory scopes, InvoiceScanner scanner, ILogger<InvoiceScanWorker> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ServiceDb>();
                var tenants = await db.Tenants.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Id)
                    .Select(x => x.Id).ToArrayAsync(ct);
                foreach (var tenant in tenants)
                {
                    await ProcessNext(db, tenant, scanner, logger, ct);
                    db.ChangeTracker.Clear();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError("Invoice scanner iteration failed ({ErrorType})", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public static async Task<bool> ProcessNext(ServiceDb db, Guid tenant, InvoiceScanner scanner, ILogger logger, CancellationToken ct)
    {
        await using var tx = await db.ForTenant(tenant, ct);
        var pending = await db.Outbox.FromSqlInterpolated($"""
            SELECT * FROM ts.outbox WHERE tenant_id = {tenant} AND kind = 'invoice_scan'
              AND completed_at IS NULL AND dead_at IS NULL AND available_at <= now()
            ORDER BY available_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (pending.SingleOrDefault() is not { } job)
            return false;

        job.Attempts++;
        InvoiceVersion? version = null;
        try
        {
            var payload = JsonSerializer.Deserialize<InvoiceScanJob>(job.Payload, Json)
                ?? throw new InvalidOperationException("Invalid invoice scan job.");
            version = await db.InvoiceVersions.FindAsync([tenant, payload.InvoiceId, payload.Version], ct)
                ?? throw new InvalidOperationException("No version for invoice scan job.");
            if (version.ScanStatus == "pending")
            {
                var file = await db.InvoiceFiles.FindAsync([tenant, payload.InvoiceId, payload.Version], ct);
                var valid = file is not null && version.Size == file.Content.Length &&
                    CryptographicOperations.FixedTimeEquals(version.Checksum, SHA256.HashData(file.Content));
                version.ScanStatus = valid && await scanner.IsClean(file!.Content, ct) ? "clean" : "quarantined";
                version.ScanAt = DateTimeOffset.UtcNow;
                db.Audit.Add(new(tenant, Guid.NewGuid(), "scanner", $"invoice.scan.{version.ScanStatus}.v{version.Version}",
                    version.InvoiceId, version.ScanAt.Value));
            }
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (job.Attempts >= 8)
            {
                job.DeadAt = now;
                if (version?.ScanStatus == "pending")
                {
                    version.ScanStatus = "failed";
                    version.ScanAt = now;
                    db.Audit.Add(new(tenant, Guid.NewGuid(), "scanner", $"invoice.scan.failed.v{version.Version}", version.InvoiceId, now));
                }
            }
            else
                job.AvailableAt = now.AddSeconds(Math.Min(1800, 10 * Math.Pow(2, job.Attempts)) + Random.Shared.Next(10));
            logger.LogWarning("Invoice scan job {JobId} failed on attempt {Attempt} ({ErrorType})",
                job.Id, job.Attempts, ex.GetType().Name);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
