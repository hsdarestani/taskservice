using TaskService.Database;
using Microsoft.EntityFrameworkCore;

namespace TaskService;

// Keys are stable stored identifiers. Legacy category identifiers retain their original meaning.
public sealed record ElectricalService(string Id, string Label, string English, string SearchTerms, bool DefaultEnabled)
{
    public string Key => "service." + Id;
}

public static class ElectricalFeatures
{
    public const string Intake = "requests.intake";
    public static IReadOnlyList<ElectricalService> Services { get; } = [
        new("installation", "Elektroinstallation", "Electrical installation", "Steckdose Leitung Neubau", true),
        new("fault", "Störungsbehebung", "Fault diagnosis", "Fehlersuche Stromausfall Sicherung", true),
        new("inspection", "Elektroprüfung", "Electrical inspection", "Anlagenprüfung Messung Prüfprotokoll", true),
        new("lighting", "Beleuchtungstechnik", "Lighting", "Leuchten Licht LED", true),
        new("e-check", "E-CHECK", "E-CHECK", "Elektroprüfung", false),
        new("dguv", "DGUV-Prüfung", "DGUV inspection", "Betriebsmittel Wiederholungsprüfung", false),
        new("building", "Gebäudetechnik", "Building systems", "Gebäudeautomation Steuerung", false),
        new("network", "Netzwerktechnik", "Network cabling", "LAN Datenverkabelung", false),
        new("smart-home", "Smart Home", "Smart home", "KNX Hausautomation", false),
        new("solar", "Photovoltaik", "Solar photovoltaics", "PV Solaranlage Wechselrichter", false),
        new("wallbox", "Wallbox-Installation", "EV charger installation", "Ladestation Elektromobilität", false),
        new("distribution", "Stromverteilung", "Power distribution", "Verteiler Unterverteilung", false),
        new("switchgear", "Schaltschrankbau", "Control panel construction", "Steuerung Schaltanlage", false),
        new("maintenance", "Elektrowartung", "Electrical maintenance", "Instandhaltung Wartung", false),
        new("emergency", "Elektro-Notdienst", "Emergency electrical service", "Notfall Bereitschaft", false),
        new("industrial", "Industrieelektrik", "Industrial electrical services", "Produktion Maschinen", false),
        new("commercial", "Gewerbeelektrik", "Commercial electrical services", "Büro Gewerbe Geschäft", false),
        new("residential", "Wohngebäudeinstallation", "Residential electrical services", "Wohnung Wohnhaus", false)
    ];

    public static bool IsKnown(string key) => key == Intake || Services.Any(x => x.Key == key);

    public static bool Configured(Tenant tenant, string key, bool fallback) =>
        tenant.FeatureOverrides.Contains("+" + key) || (!tenant.FeatureOverrides.Contains("-" + key) && fallback);

    public static async Task<FeaturePolicy> LoadPolicy(ServiceDb db, Tenant tenant, CancellationToken ct)
    {
        var rows = await db.Set<FeaturePlan>().AsNoTracking()
            .Where(p => p.Code == "global" || p.Code == tenant.PlanCode).ToDictionaryAsync(p => p.Code, ct);
        return new(tenant, rows["global"].Withheld, rows[tenant.PlanCode].Withheld);
    }

    public static string? CategoryLabel(string? id) => Services.FirstOrDefault(s => s.Id == id)?.Label ?? id switch
    {
        "repair" => "Reparaturen", "cleaning" => "Reinigung", "electric" => "Elektro & Sanitär",
        "garden" => "Haus & Garten", "furniture" => "Möbelmontage", "other" => "Weitere Services", _ => id
    };
}

public sealed record FeaturePolicy(Tenant Tenant, string[] GloballyDisabled, string[] Unlicensed)
{
    public bool Available(string key, bool fallback) => !GloballyDisabled.Contains(key) && !Unlicensed.Contains(key) &&
        ElectricalFeatures.Configured(Tenant, key, fallback);
    public bool IntakeEnabled => Available(ElectricalFeatures.Intake, true);
    public bool Offered(ElectricalService service) => IntakeEnabled && Available(service.Key, service.DefaultEnabled);
    public bool CanSubmit(string? category) => ElectricalFeatures.Services.Any(
        service => (category is null || service.Id == category) && Offered(service));
}
