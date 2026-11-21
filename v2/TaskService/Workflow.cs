using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TaskService.Database;

namespace TaskService;

public static class Workflow
{
    public static IReadOnlyList<string> Actions(ServiceRequest request, Actor actor, Appointment? appointment)
    {
        var owns = actor.Key != Actor.Guest.Key && actor.Key == request.CustomerKey;
        var assigned = actor.IsTechnician && actor.Membership!.Id == request.TechnicianId;
        IReadOnlyList<string> actions = request.State switch
        {
            "verified" when actor.IsCompany => ["triage", "cancel"],
            "triage" when actor.IsCompany => ["request_information", "assign", "cancel"],
            "assigned" when actor.IsCompany => ["request_information", "assign", "schedule", "cancel"],
            "scheduled" when actor.IsCompany => ["assign", "schedule", "appointment_note", "start", "cancel"],
            "scheduled" when assigned => ["start", "arrival"],
            "in_progress" when actor.IsCompany => ["complete", "cancel"],
            "awaiting_customer" when actor.IsCompany || owns => ["resume"],
            "completed" or "cancelled" when actor.IsCompany => ["reopen"],
            _ => []
        };
        return owns && request.State == "scheduled" &&
            appointment is { CustomerConfirmedAt: null } && appointment.StartsAt > DateTimeOffset.UtcNow
            ? [.. actions, "confirm_appointment"]
            : actions;
    }

    public static DateTimeOffset? ResolveBerlinSlot(string? value, string? fold)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        if (zone.IsInvalidTime(local))
            return null;
        if (!zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        var offsets = zone.GetAmbiguousTimeOffsets(local);
        return fold switch
        {
            "earlier" => new DateTimeOffset(local, offsets.Max()),
            "later" => new DateTimeOffset(local, offsets.Min()),
            _ => null
        };
    }

    public static string CustomerState(string state) => state switch
    {
        "received" or "verified" => "received",
        "triage" or "assigned" => "under_review",
        _ => state
    };

    public static void Record(ServiceDb db, ServiceRequest request, Actor actor, string action, string? privateNote = null)
    {
        var now = DateTimeOffset.UtcNow;
        db.History.Add(new(request.TenantId, request.Id, request.Revision, request.State, now) { PrivateNote = privateNote });
        db.Audit.Add(new(request.TenantId, Guid.NewGuid(), actor.Key, action, request.Id, now));
    }

    public static void QueueMail(ServiceDb db, Guid tenant, IDataProtectionProvider provider, string to,
        string subject, string content)
    {
        db.Outbox.Add(new(tenant, Guid.NewGuid(), "mail", ProtectMail(provider, to, subject, content), DateTimeOffset.UtcNow));
    }

    internal static string ProtectMail(IDataProtectionProvider provider, string to, string subject, string content) =>
        provider.CreateProtector("taskservice-outbox-mail-v1").Protect(JsonSerializer.Serialize(new MailPayload(to, subject, content)));
}

public sealed record MailPayload(string To, string Subject, string Content);
