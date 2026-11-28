using System.Security.Cryptography;
using System.Text.Json;
using TaskService.Database;

namespace TaskService;

public sealed record IntakeField(string Key, string Label, string Kind, bool Required, string? WhenYes);
public sealed record ServiceForm(string Instructions, int MinimumPhotos, IntakeField[] Fields);
public sealed record FormChange(string Service, ServiceForm Form);
public sealed record RecordedAnswer(string Label, string Value);

public static class ServiceForms
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly ServiceForm Empty = new("", 0, []);
    public static string Version(ServiceForm form) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(form, Json)));

    public static Dictionary<string, ServiceForm> Read(Tenant tenant) =>
        JsonSerializer.Deserialize<Dictionary<string, ServiceForm>>(tenant.ServiceFormsJson, Json)!;

    public static bool Valid(FormChange? change)
    {
        if (change?.Form is not { Instructions: { Length: <= 500 }, MinimumPhotos: >= 0 and <= 3, Fields.Length: <= 8 } form ||
            !ElectricalFeatures.Services.Any(s => s.Id == change.Service)) return false;
        var keys = new HashSet<string>();
        var visibleSwitches = new HashSet<string>();
        foreach (var f in form.Fields)
        {
            if (f is not { Key.Length: > 0 and <= 40, Label.Length: > 0 and <= 100, Kind: "text" or "yesno" } ||
                f.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || !keys.Add(f.Key) ||
                string.IsNullOrWhiteSpace(f.Label) || f.Label.Any(char.IsControl) ||
                f.WhenYes is not null && !visibleSwitches.Contains(f.WhenYes)) return false;
            if (f.Kind == "yesno" && f.WhenYes is null) visibleSwitches.Add(f.Key);
        }
        return true;
    }

    public static void Save(Tenant tenant, FormChange change)
    {
        var forms = Read(tenant);
        if (change.Form.Instructions.Length == 0 && change.Form.MinimumPhotos == 0 && change.Form.Fields.Length == 0)
            forms.Remove(change.Service);
        else forms[change.Service] = change.Form;
        tenant.ServiceFormsJson = JsonSerializer.Serialize(forms, Json);
    }

    public static bool TryRecord(FeaturePolicy policy, NewRequest request, out RecordedAnswer[] recorded)
    {
        recorded = [];
        var forms = Read(policy.Tenant);
        // A legacy client cannot bypass a newly configured intake contract by omitting its category.
        if (request.Category is null && ElectricalFeatures.Services.Where(policy.Offered).Any(s =>
            forms.TryGetValue(s.Id, out var f) && (f.MinimumPhotos > 0 || f.Fields.Any(field => field.Required))))
            return false;
        var form = request.Category is { } id ? forms.GetValueOrDefault(id, Empty) : Empty;
        if (request.FormVersion is not null && request.FormVersion != Version(form)) return false;
        if (form != Empty && request.FormVersion is null) return false;
        var answers = request.Answers ?? [];
        if ((request.Photos?.Length ?? 0) < form.MinimumPhotos || answers.Count > form.Fields.Length) return false;
        var values = new List<RecordedAnswer>();
        var accepted = new HashSet<string>();
        foreach (var field in form.Fields)
        {
            if (field.WhenYes is not null && answers.GetValueOrDefault(field.WhenYes) != "yes") continue;
            if (!answers.TryGetValue(field.Key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                if (field.Required) return false;
                accepted.Add(field.Key);
                continue;
            }
            var value = raw.Trim();
            if (value.Length > 500 || value.Any(char.IsControl) || field.Kind == "yesno" && value is not ("yes" or "no"))
                return false;
            accepted.Add(field.Key);
            values.Add(new(field.Label, field.Kind == "yesno" ? value == "yes" ? "Ja" : "Nein" : value));
        }
        recorded = values.ToArray();
        return answers.Keys.All(accepted.Contains);
    }
}
