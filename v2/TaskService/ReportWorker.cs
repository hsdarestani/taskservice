using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using TaskService.Database;

namespace TaskService;

public sealed class ReportWorker(IServiceScopeFactory scopes, ILogger<ReportWorker> logger) : BackgroundService
{
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
                    await ProcessNext(db, tenant, logger, ct);
                    db.ChangeTracker.Clear();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError("Report worker iteration failed ({ErrorType})", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public static async Task<bool> ProcessNext(ServiceDb db, Guid tenant, ILogger logger, CancellationToken ct)
    {
        await using var tx = await db.ForTenant(tenant, ct);
        var pending = await db.Outbox.FromSqlInterpolated($"""
            SELECT * FROM ts.outbox WHERE tenant_id = {tenant} AND kind = 'report_pdf'
              AND completed_at IS NULL AND dead_at IS NULL AND available_at <= now()
            ORDER BY available_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (pending.SingleOrDefault() is not { } job)
            return false;

        job.Attempts++;
        var now = DateTimeOffset.UtcNow;
        try
        {
            var render = JsonSerializer.Deserialize<ReportRenderJob>(job.Payload, ReportPdf.Json)
                ?? throw new InvalidOperationException("Invalid report job.");
            var version = await db.ReportVersions.FindAsync([tenant, render.RequestId, render.Version], ct);
            if (version is null || version.Status is "submitted" or "rejected")
                throw new InvalidOperationException("Unapproved report job.");
            if (!await db.ReportFiles.AnyAsync(x => x.TenantId == tenant && x.RequestId == render.RequestId &&
                x.Version == render.Version, ct))
            {
                var pdf = ReportPdf.Render(version, render.CompanyName);
                if (pdf.Length is < 1 or > 1048576)
                    throw new InvalidOperationException("Generated report exceeds document size limit.");
                db.ReportFiles.Add(new(tenant, render.RequestId, render.Version, render.CompanyName,
                    SHA256.HashData(pdf), pdf, now));
            }
            job.CompletedAt = now;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (job.Attempts >= 8)
                job.DeadAt = now;
            else
                job.AvailableAt = now.AddSeconds(Math.Min(1800, 10 * Math.Pow(2, job.Attempts)) + Random.Shared.Next(10));
            logger.LogWarning("Report job {JobId} failed on attempt {Attempt} ({ErrorType})",
                job.Id, job.Attempts, ex.GetType().Name);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}

internal static class ReportPdf
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    static ReportPdf() => GlobalFontSettings.FontResolver = new ReportFonts();

    internal static byte[] Render(ReportVersion version, string company)
    {
        var report = new Document();
        report.Info.Title = "Arbeitsbericht";
        report.Styles["Normal"]!.Font.Name = "DejaVu Sans";
        report.Styles["Normal"]!.Font.Size = 10;
        report.Styles["Normal"]!.ParagraphFormat.SpaceAfter = Unit.FromPoint(7);
        var page = report.AddSection();
        page.PageSetup.PageFormat = PageFormat.A4;
        page.AddParagraph(company).Format.Font.Bold = true;
        var title = page.AddParagraph("Arbeitsbericht");
        title.Format.Font.Size = 24;
        title.Format.Font.Bold = true;
        page.AddParagraph($"Vorgang {version.RequestId:N} · Version {version.Version}");
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        page.AddParagraph($"Datum: {TimeZoneInfo.ConvertTime(version.SubmittedAt, berlin).ToString("d", German)}");

        page.AddParagraph("Tätigkeiten und Arbeitszeit").Format.Font.Bold = true;
        var activities = JsonSerializer.Deserialize<ReportActivity[]>(version.Activities, Json)!;
        foreach (var item in activities)
            page.AddParagraph($"{item.Minutes.ToString(German)} Min. · {item.Description}");
        page.AddParagraph($"Gesamtzeit: {activities.Sum(x => x.Minutes).ToString(German)} Min.");
        page.AddParagraph("Ergebnis").Format.Font.Bold = true;
        foreach (var line in version.Result.ReplaceLineEndings("\n").Split('\n'))
            page.AddParagraph(line);
        if (version.Materials is { } materials)
        {
            page.AddParagraph("Material").Format.Font.Bold = true;
            foreach (var line in materials.ReplaceLineEndings("\n").Split('\n'))
                page.AddParagraph(line);
        }
        var footer = page.Footers.Primary.AddParagraph();
        footer.AddText("Arbeitsbericht · Seite ");
        footer.AddPageField();
        footer.AddText(" von ");
        footer.AddNumPagesField();

        var renderer = new PdfDocumentRenderer { Document = report };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, false);
        return output.ToArray();
    }

    private sealed class ReportFonts : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
            new(bold ? "DejaVuSans-Bold" : "DejaVuSans", false, italic);

        public byte[] GetFont(string faceName) => File.ReadAllBytes(
            $"/usr/share/fonts/truetype/dejavu/{faceName}.ttf");
    }
}
