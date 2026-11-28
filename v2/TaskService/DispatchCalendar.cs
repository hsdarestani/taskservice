using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static partial class Endpoints
{
    private static async Task<IResult> DispatchCalendar(ServiceDb db, string slug, DateOnly date, string? view,
        DateTimeOffset? after, Guid? cursor, HttpContext http, CancellationToken ct)
    {
        if (await FindTenant(db, slug, ct) is not { } tenant)
            return ApiProblem.NotFound();
        await using var tx = await db.ForTenant(tenant.Id, ct);
        var actor = await Actor.Read(db, tenant, http, ct);
        if (!actor.IsCompany && !actor.IsTechnician)
            return ApiProblem.Denied();
        if (!(await ElectricalFeatures.LoadPolicy(db, tenant, ct)).Available(ElectricalFeatures.Dispatch, true))
            return ApiProblem.NotFound();
        if (view is not (null or "day" or "week" or "month") || date.Year is < 1900 or > 2100 ||
            (after is null) != (cursor is null))
            return ApiProblem.Error(422, "invalid_range", "Bitte wählen Sie einen gültigen Kalenderzeitraum.");

        var first = view switch
        {
            "week" => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
            "month" => new DateOnly(date.Year, date.Month, 1), _ => date
        };
        var last = view == "month" ? first.AddMonths(1) : first.AddDays(view == "week" ? 7 : 1);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        DateTimeOffset Midnight(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone);
        var from = Midnight(first);
        var until = Midnight(last);
        var appointments = db.Appointments.AsNoTracking().Where(a => a.TenantId == tenant.Id && !a.Cancelled &&
            a.StartsAt < until && a.EndsAt > from);
        if (actor.IsTechnician)
            appointments = appointments.Where(a => a.TechnicianId == actor.Membership!.Id);
        if (after is { } timestamp && cursor is { } id)
        {
            var utc = timestamp.ToUniversalTime();
            appointments = appointments.Where(a => a.StartsAt > utc || a.StartsAt == utc && a.Id.CompareTo(id) > 0);
        }
        var rows = await (from a in appointments
            join r in db.Requests on new { a.TenantId, Id = a.RequestId } equals new { r.TenantId, r.Id }
            join m in db.Members on new { a.TenantId, Id = a.TechnicianId } equals new { m.TenantId, m.Id }
            where !actor.IsTechnician || r.TechnicianId == a.TechnicianId
            orderby a.StartsAt, a.Id
            select new { a.Id, a.RequestId, r.Title, a.StartsAt, a.EndsAt, a.ArrivalStatus,
                Technician = m.Name, r.State }).Take(101).ToListAsync(ct);
        return Results.Ok(new { First = first, Until = last, Items = rows.Take(100),
            Next = rows.Count > 100 ? new { after = rows[99].StartsAt, cursor = rows[99].Id } : null });
    }
}
