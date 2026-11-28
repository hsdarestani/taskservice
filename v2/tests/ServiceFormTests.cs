using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace TaskService.Tests;

public static class ServiceFormChecks
{
    public static async Task Verify(Lab lab)
    {
        using var owner = lab.Client("platform-admin", mfa: true);
        await using var isolated = lab.IsolatedHost();
        using var guest = isolated.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        const string slug = "form-probe";
        const string companies = "/api/v1/platform/companies";
        var commands = companies + "/" + slug + "/commands";
        var api = Lab.TenantUrl(slug);
        var service = new ServiceForm("Foto vom Typenschild beifügen.", 1,
            [new("device", "Gerät vorhanden?", "yesno", true, null), new("serial", "Seriennummer", "text", true, "device")]);
        object Config(ServiceForm form) => new { action = "form", form = new FormChange("inspection", form) };
        Assert.Equal(HttpStatusCode.Created, (await lab.Post(owner, companies,
            new { slug, name = "Prüfbetrieb", accent = "#124578" }, revision: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Post(guest, commands, Config(service), revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(owner, commands, Config(service), revision: 1)).StatusCode);
        var catalog = await guest.GetFromJsonAsync<JsonElement>(api + "/catalog");
        var template = catalog.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "inspection");
        var version = template.GetProperty("formVersion").GetString();
        Assert.DoesNotContain("Seriennummer", await guest.GetStringAsync(lab.Base() + "/catalog"));
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+lpb8AAAAASUVORK5CYII=");
        var form = new NewRequest("Prüfung anfragen", "Bitte ein Angebot senden.", "qa@example.invalid", "inspection",
            "Test Kunde", "+49 123456", [new("image/png", image)], new() { ["device"] = "yes", ["serial"] = "S-204" }, version);
        foreach (var invalid in new[] { form with { Photos = [] }, form with { Answers = new() { ["device"] = "yes" } },
            form with { Answers = new() { ["device"] = "no", ["serial"] = "hidden" } }, form with { FormVersion = "obsolete" } })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lab.Post(guest, api + "/requests", invalid)).StatusCode);
        var key = Guid.NewGuid().ToString();
        var created = await lab.Post(guest, api + "/requests", form, key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(owner, commands, Config(ServiceForms.Empty), revision: 2)).StatusCode);
        Assert.Equal(await created.Content.ReadAsStringAsync(),
            await (await lab.Post(guest, api + "/requests", form, key)).Content.ReadAsStringAsync());
        using var conn = new Npgsql.NpgsqlConnection(lab.Runtime);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await Lab.Scope(conn, await lab.TenantId(slug));
        var recorded = await Lab.Read<string>(conn, $"SELECT intake_answers_json FROM ts.requests WHERE id='{id}'");
        Assert.Contains("Seriennummer", recorded);
        Assert.Contains("S-204", recorded);
    }
}
