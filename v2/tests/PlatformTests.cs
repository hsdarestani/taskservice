using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TaskService.Database;
using Xunit;

namespace TaskService.Tests;

public sealed class PlatformTests(Lab lab) : IClassFixture<Lab>
{
    [Fact]
    public async Task DatabaseIsolationFailsClosedAndRolesCannotOverrideTenant()
    {
        await using var db = new NpgsqlConnection(lab.Runtime);
        await db.OpenAsync();
        Assert.Equal(0L, await Lab.Read<long>(db, "select count(*) from ts.requests"));
        await using (var tx = await db.BeginTransactionAsync())
        {
            await Lab.Scope(db, lab.TenantA);
            Assert.Equal(0L, await Lab.Read<long>(db, $"select count(*) from ts.requests where tenant_id='{lab.TenantB}'"));
            Assert.True(await Lab.Read<long>(db, "select count(*) from ts.requests") > 0);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Lab.Execute(db, $"""
                INSERT INTO ts.requests (tenant_id,id,title,description,contact_email,state,revision,created_at)
                VALUES ('{lab.TenantB}',gen_random_uuid(),'cross','test','qa@example.invalid','received',1,now())
                """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
            await tx.RollbackAsync();
        }
        Assert.Equal(0L, await Lab.Read<long>(db, "select count(*) from ts.requests"));
        Assert.False(await Lab.Read<bool>(db, "select rolbypassrls or rolsuper from pg_roles where rolname=current_user"));
    }

    [Fact]
    public async Task RejectsOtherCompanyOtherCustomerAndUnassignedTechnician()
    {
        using var customer = lab.Client("customer-a");
        using var wrongCustomer = lab.Client("nobody");
        using var technician = lab.Client("tech-a");
        using var admin = lab.Client("admin-a", mfa: true);
        using var plainAdmin = lab.Client("admin-a");
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(lab.Request(lab.FixtureA))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(lab.Base() + "/requests?cursor=" + Guid.Empty)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await wrongCustomer.GetAsync(lab.Request(lab.FixtureA))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await technician.GetAsync(lab.Request(lab.FixtureA))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(lab.Request(lab.FixtureB, "other"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plainAdmin.GetAsync(lab.Base() + "/technicians")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(lab.Base() + "/technicians")).StatusCode);
    }

    [Fact]
    public async Task GuestClaimBindsOnlyOneRequestToAuthenticatedSubject()
    {
        using var guest = lab.Client();
        using var customer = lab.Client("claimant");
        using var wrongCustomer = lab.Client("customer-a");
        var submitted = await lab.Submit(guest, "Arbeitsauftrag");
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        var id = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var token = await lab.TokenFor(id);
        var missing = await lab.Post(customer, lab.Request(id) + "/claim", new { token = new string('0', 64) });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await lab.Post(customer, lab.Request(id) + "/claim", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await lab.Post(wrongCustomer, lab.Request(id) + "/claim", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(lab.Request(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await wrongCustomer.GetAsync(lab.Request(id))).StatusCode);
    }

    [Fact]
    public async Task RequestPhotosAreTransactionalQuarantinedAndPrivateAfterScan()
    {
        await using var guestHost = lab.IsolatedHost();
        using var guest = guestHost.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var owner = guestHost.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        owner.DefaultRequestHeaders.Add("X-Test-Subject", "photo-owner");
        using var stranger = lab.Client("photo-stranger");
        using var admin = lab.Client("admin-a", mfa: true);
        var original = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+lpb8AAAAASUVORK5CYII=");
        var form = new NewRequest("Garten prüfen", "Synthetisches Anliegen mit einem Foto.", "qa@example.invalid",
            "garden", "Test Kunde", "+49 1234567", [new("image/png", original)]);
        var endpoint = lab.Base() + "/requests";
        var bad = await lab.Post(guest, endpoint, form with { Photos = [new("image/webp", original)] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        var key = Guid.NewGuid().ToString();
        var first = await lab.Post(guest, endpoint, form, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var replay = await lab.Post(guest, endpoint, form, key);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var path = lab.Request(id);
        var details = await admin.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal("garden", details.GetProperty("serviceCategory").GetString());
        Assert.Equal("Test Kunde", details.GetProperty("contactName").GetString());
        var file = Assert.Single(details.GetProperty("photos").EnumerateArray());
        var download = path + "/photos/" + file.GetProperty("id").GetGuid();
        Assert.Equal("pending", file.GetProperty("scanStatus").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(download)).StatusCode);

        var token = await lab.TokenFor(id, guestHost.Services);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.Post(owner, path + "/claim", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(download)).StatusCode);

        await using var runtime = new NpgsqlConnection(lab.Runtime);
        await runtime.OpenAsync();
        await using (var tx = await runtime.BeginTransactionAsync())
        {
            await Lab.Scope(runtime, lab.TenantA);
            Assert.Equal(0L, await Lab.Read<long>(runtime, "select count(*) from ts.request_photo_files"));
        }
        Assert.True(await lab.ProcessPhoto(Lab.LocalClamAv() ?? new FixtureInvoiceScanner(clean: true)));
        var fetched = await owner.GetAsync(download);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(original, await fetched.Content.ReadAsByteArrayAsync());
        Assert.True(fetched.Headers.CacheControl?.NoStore);
        Assert.Contains("attachment", fetched.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(download)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(lab.Request(id, "other") +
            "/photos/" + file.GetProperty("id").GetGuid())).StatusCode);

        if (Environment.GetEnvironmentVariable("TASKSERVICE_GUEST_BROWSER") is { Length: > 0 } browserScript)
            await lab.RunBrowser(browserScript, invoice: false);
    }

    [Fact]
    public async Task GuestPostIsIdempotentAndRequiresOriginAndToken()
    {
        using var guest = lab.Client();
        var key = Guid.NewGuid().ToString();
        var first = await lab.Submit(guest, "Idempotenztest", key);
        var replay = await lab.Submit(guest, "Idempotenztest", key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Submit(guest, "Geänderter Inhalt", key)).StatusCode);
        var uncheckedSend = await guest.PostAsJsonAsync(lab.Base() + "/requests",
            new { title = "Ungültig", description = "Ohne Token", email = "qa@example.invalid" });
        Assert.Equal(HttpStatusCode.Forbidden, uncheckedSend.StatusCode);
    }

    [Fact]
    public async Task InvitedEmployeeBindsVerifiedAddressAndCanOnlyReceiveRoleScopedPermissions()
    {
        await using var host = lab.IsolatedHost();
        HttpClient Local(string subject, string? email = null, bool verified = false, bool mfa = false)
        {
            var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
            if (email is not null)
                client.DefaultRequestHeaders.Add("X-Test-Email", email);
            if (verified)
                client.DefaultRequestHeaders.Add("X-Test-Email-Verified", "1");
            if (mfa)
                client.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
            return client;
        }

        using var admin = Local("admin-a", mfa: true);
        using var unauthorised = Local("tech-a", mfa: true);
        using var invited = Local("invited-technician", "invited@example.invalid", verified: true);
        using var unverified = Local("unverified", "invited@example.invalid");
        using var wrong = Local("wrong-person", "another@example.invalid", verified: true);
        var root = lab.Base() + "/team";
        var draft = new { name = "Prüfpersonal", email = "invited@example.invalid", role = "technician" };
        Assert.Equal(HttpStatusCode.Forbidden, (await unauthorised.GetAsync(root)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Post(unauthorised, root + "/invitations", draft)).StatusCode);
        var key = Guid.NewGuid().ToString();
        var issued = await lab.Post(admin, root + "/invitations", draft, key);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        Assert.Equal(await issued.Content.ReadAsStringAsync(),
            await (await lab.Post(admin, root + "/invitations", draft, key)).Content.ReadAsStringAsync());
        var id = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var token = await lab.TokenFor(id, host.Services);
        var acceptance = root + "/invitations/" + id + "/accept";
        Assert.Equal(HttpStatusCode.NotFound, (await lab.Post(unverified, acceptance, new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lab.Post(wrong, acceptance, new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(invited, acceptance, new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lab.Post(invited, acceptance, new { token })).StatusCode);
        var directory = await admin.GetFromJsonAsync<JsonElement>(root);
        var member = directory.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("name").GetString() == draft.name);
        var memberId = member.GetProperty("id").GetGuid();
        var commands = root + "/members/" + memberId + "/commands";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lab.Post(admin, commands,
            new { active = true, permissions = new[] { "invoice.approve" } }, revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(admin, commands,
            new { active = true, permissions = new[] { "invoice.upload" } }, revision: 1)).StatusCode);
        var item = await lab.Insert("assigned", memberId);
        Assert.Equal(HttpStatusCode.OK, (await invited.GetAsync(lab.Request(item))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await lab.Upload(invited, lab.Request(item) + "/invoices",
            """<Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"></Invoice>"""u8.ToArray())).StatusCode);
        Assert.True(await lab.ProcessInvoice(Lab.LocalClamAv() ?? new FixtureInvoiceScanner(clean: true)));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await lab.Post(admin, commands,
            new { active = false, permissions = Array.Empty<string>() }, revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(admin, commands,
            new { active = false, permissions = Array.Empty<string>() }, revision: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await invited.GetAsync(lab.Request(item))).StatusCode);
    }

    [Fact]
    public async Task PlatformIssuesOnlyTheFirstAdministratorInviteAndCannotReadInvitationRecords()
    {
        await using var host = lab.IsolatedHost();
        using var platform = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        platform.DefaultRequestHeaders.Add("X-Test-Subject", "platform-admin");
        platform.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        using var worker = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        worker.DefaultRequestHeaders.Add("X-Test-Subject", "company-initial-admin");
        worker.DefaultRequestHeaders.Add("X-Test-Email", "initial@example.invalid");
        worker.DefaultRequestHeaders.Add("X-Test-Email-Verified", "1");
        const string slug = "bootstrap-check";
        var setup = "/api/v1/platform/companies/" + slug + "/setup";
        Assert.Equal(HttpStatusCode.Created, (await lab.Post(platform, "/api/v1/platform/companies",
            new { slug, name = "Prüfunternehmen", accent = "#1064e8" }, revision: 0)).StatusCode);
        var status = await platform.GetFromJsonAsync<JsonElement>(setup);
        Assert.Equal("missing", status.GetProperty("state").GetString());
        var form = new { name = "Erstadmin Test", email = "initial@example.invalid" };
        var issued = await lab.Post(platform, setup, form);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(platform, setup, form)).StatusCode);
        Assert.Equal("pending", (await platform.GetFromJsonAsync<JsonElement>(setup)).GetProperty("state").GetString());
        var id = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var token = await lab.TokenFor(id, host.Services);
        var accept = Lab.TenantUrl(slug) + "/team/invitations/" + id + "/accept";
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Post(worker, accept, new { token })).StatusCode);
        worker.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(worker, accept, new { token })).StatusCode);
        Assert.Equal("active", (await platform.GetFromJsonAsync<JsonElement>(setup)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(platform, setup, form)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync(Lab.TenantUrl(slug) + "/team")).StatusCode);
        await using var platformDb = new NpgsqlConnection(lab.PlatformConnection);
        await platformDb.OpenAsync();
        var denied = await Assert.ThrowsAsync<PostgresException>(() => Lab.Read<long>(platformDb, "select count(*) from ts.member_invites"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task OidcPkceUsesSeparateCustomerAndWorkforceCookiesAndLogout()
    {
        const string issuer = "https://identity.example.invalid";
        const string clientId = "taskservice-test-client";
        using var provider = new FakeOidcProvider(issuer, clientId);
        using var environment = new TestEnvironment(new()
        {
            ["Identity__Authority"] = issuer, ["Identity__ClientId"] = clientId,
            ["Identity__ClientSecret"] = "fixture-only", ["Identity__PrivilegedAcr"] = "verified-mfa"
        });
        await using var host = lab.IsolatedHost().WithWebHostBuilder(web =>
        {
            web.ConfigureServices(services =>
            {
                services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = IdentitySessions.Selector);
                foreach (var scheme in new[] { OpenIdConnectDefaults.AuthenticationScheme, IdentitySessions.WorkforceOidc })
                    services.PostConfigure<OpenIdConnectOptions>(scheme, options =>
                    {
                        options.Backchannel = new HttpClient(provider, disposeHandler: false);
                        var metadata = new OpenIdConnectConfiguration
                        {
                            Issuer = issuer, AuthorizationEndpoint = issuer + "/authorize", TokenEndpoint = issuer + "/token"
                        };
                        metadata.SigningKeys.Add(provider.Key);
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                    });
            });
        });
        using var browser = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        async Task Login(string subject, string target, bool workforce)
        {
            provider.Subject = subject;
            var query = "/api/v1/login?returnTo=" + Uri.EscapeDataString(target) + (workforce ? "&mode=workforce" : "");
            var entry = await browser.GetAsync(query);
            Assert.Equal(HttpStatusCode.Redirect, entry.StatusCode);
            Assert.Equal(issuer + "/authorize", entry.Headers.Location!.GetLeftPart(UriPartial.Path));
            var redirect = QueryHelpers.ParseQuery(entry.Headers.Location.Query);
            Assert.Equal("code", redirect["response_type"].ToString());
            Assert.Equal("S256", redirect["code_challenge_method"].ToString());
            Assert.Equal(workforce ? "verified-mfa" : "", redirect.GetValueOrDefault("acr_values").ToString());
            provider.Challenge = redirect["code_challenge"].ToString();
            provider.Nonce = redirect["nonce"].ToString();
            var callback = new Uri(redirect["redirect_uri"].ToString()).AbsolutePath;
            Assert.Equal(workforce ? "/signin-oidc-workforce" : "/signin-oidc", callback);
            var returned = await browser.PostAsync(callback, new FormUrlEncodedContent(new Dictionary<string, string>
                { ["code"] = "synthetic-signed-identity", ["state"] = redirect["state"].ToString() }));
            Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
            Assert.Equal(target, returned.Headers.Location?.OriginalString);
            var cookie = workforce ? "__Host-taskservice-workforce=" : "__Host-taskservice=";
            Assert.Contains(returned.Headers.GetValues("Set-Cookie"), header => header.StartsWith(cookie, StringComparison.Ordinal)
                && header.Contains("secure", StringComparison.OrdinalIgnoreCase) && header.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        }

        await Login("admin-a", "/unternehmen/lab/betrieb", workforce: true);
        Assert.False((await browser.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("signedIn").GetBoolean());
        Assert.True((await browser.GetFromJsonAsync<JsonElement>("/api/v1/session?session=workforce")).GetProperty("signedIn").GetBoolean());
        using var team = await browser.GetAsync(lab.Base() + "/team?session=workforce");
        Assert.Equal(HttpStatusCode.OK, team.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync(lab.Base() + "/team")).StatusCode);

        await Login("customer-a", "/unternehmen/lab/konto", workforce: false);
        Assert.True((await browser.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("signedIn").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(lab.Request(lab.FixtureA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync(lab.Base() + "/team")).StatusCode);

        var staffSession = await browser.GetFromJsonAsync<JsonElement>("/api/v1/session?session=workforce");
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/logout");
        logout.Headers.Add("Origin", "https://localhost");
        logout.Headers.Add(IdentitySessions.ScopeHeader, "workforce");
        logout.Headers.Add("X-CSRF-Token", staffSession.GetProperty("csrfToken").GetString());
        Assert.True((await browser.SendAsync(logout)).IsSuccessStatusCode);
        Assert.False((await browser.GetFromJsonAsync<JsonElement>("/api/v1/session?session=workforce")).GetProperty("signedIn").GetBoolean());
        Assert.True((await browser.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task SimultaneousBookingsAreRejectedAndWorkRequiresReportBeforeCompletion()
    {
        using var admin = lab.Client("admin-a", mfa: true);
        using var tech = lab.Client("tech-a");
        using var secondDispatcher = lab.Client("admin-a", mfa: true);
        var first = await lab.Insert("assigned", lab.TechA);
        var second = await lab.Insert("assigned", lab.TechA);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var body = new { action = "schedule", startsAt = start, endsAt = start.AddHours(1) };
        var result = await Task.WhenAll(
            lab.Post(admin, lab.Request(first) + "/commands", body, revision: 1),
            lab.Post(secondDispatcher, lab.Request(second) + "/commands", body, revision: 1));
        Assert.Single(result, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(result, r => r.StatusCode == HttpStatusCode.Conflict);
        var winner = result[0].IsSuccessStatusCode ? first : second;
        var stale = await lab.Post(admin, lab.Request(winner) + "/commands", new { action = "complete" }, revision: 1);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(tech, lab.Request(winner) + "/commands",
            new { action = "arrival", arrivalStatus = "departed" }, revision: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(tech, lab.Request(winner) + "/commands",
            new { action = "start" }, revision: 3)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(admin, lab.Request(winner) + "/commands",
            new { action = "complete" }, revision: 4)).StatusCode);
    }

    [Fact]
    public async Task OnlyAssignedCustomerCanConfirmCurrentWindowAndReschedulingClearsConfirmation()
    {
        using var staff = lab.Client("admin-a", mfa: true);
        using var customer = lab.Client("customer-a");
        using var otherCustomer = lab.Client("customer-b");
        using var technician = lab.Client("tech-a");
        var id = await lab.Insert("assigned", lab.TechA);
        var request = lab.Request(id);
        var commands = request + "/commands";
        var start = DateTimeOffset.UtcNow.AddDays(8);
        var slot = new { action = "schedule", startsAt = start, endsAt = start.AddHours(2), note = "Bitte Zugang freihalten." };
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(staff, commands, slot, revision: 1)).StatusCode);

        var ownerView = await customer.GetFromJsonAsync<JsonElement>(request);
        Assert.Contains("confirm_appointment", ownerView.GetProperty("actions").EnumerateArray().Select(x => x.GetString()));
        var confirmation = new { action = "confirm_appointment" };
        Assert.Equal(HttpStatusCode.NotFound, (await lab.Post(otherCustomer, commands, confirmation, revision: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(technician, commands, confirmation, revision: 2)).StatusCode);
        var confirmationKey = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(customer, commands, confirmation, confirmationKey, revision: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(customer, commands, confirmation, confirmationKey, revision: 2)).StatusCode);
        var confirmed = (await customer.GetFromJsonAsync<JsonElement>(request)).GetProperty("appointment");
        Assert.Equal("Europe/Berlin", confirmed.GetProperty("timeZone").GetString());
        Assert.Equal(JsonValueKind.String, confirmed.GetProperty("customerConfirmedAt").ValueKind);
        Assert.Equal("Bitte Zugang freihalten.", confirmed.GetProperty("customerNote").GetString());

        var changed = new { action = "schedule", startsAt = start.AddDays(1), endsAt = start.AddDays(1).AddHours(2) };
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(staff, commands, changed, revision: 3)).StatusCode);
        var fresh = (await customer.GetFromJsonAsync<JsonElement>(request)).GetProperty("appointment");
        Assert.Equal(JsonValueKind.Null, fresh.GetProperty("customerConfirmedAt").ValueKind);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await lab.Post(customer, commands, confirmation, revision: 3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(customer, commands, confirmation, revision: 4)).StatusCode);
    }

    [Fact]
    public async Task ReopeningRecordsAReasonOnlyCompanyStaffCanRead()
    {
        using var staff = lab.Client("admin-a", mfa: true);
        using var customer = lab.Client("customer-a");
        var id = await lab.Insert("completed", lab.TechA);
        var request = lab.Request(id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lab.Post(staff, request + "/commands",
            new { action = "reopen" }, revision: 1)).StatusCode);
        const string reason = "Interner Prüfbedarf";
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(staff, request + "/commands",
            new { action = "reopen", note = reason }, revision: 1)).StatusCode);
        Assert.Contains(reason, await (await staff.GetAsync(request)).Content.ReadAsStringAsync());
        Assert.DoesNotContain(reason, await (await customer.GetAsync(request)).Content.ReadAsStringAsync());
    }

    [Fact]
    public void BerlinTimesRejectSkippedHoursAndRequireAChoiceForAmbiguousHours()
    {
        Assert.Null(Workflow.ResolveBerlinSlot("2027-03-28T02:30", null));
        Assert.Null(Workflow.ResolveBerlinSlot("2027-10-31T02:30", null));
        var first = Workflow.ResolveBerlinSlot("2027-10-31T02:30", "earlier");
        var second = Workflow.ResolveBerlinSlot("2027-10-31T02:30", "later");
        Assert.Equal(TimeSpan.FromHours(2), first!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(1), second!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(1), second - first);
    }

    [Fact]
    public async Task ReportReviewRequiresSeparatePermissionsAndDownloadsRespectRecipientVersionAndRevocation()
    {
        using var technician = lab.Client("tech-a");
        using var guest = lab.Client();
        using var unassigned = lab.Client("tech-chat");
        using var approver = lab.Client("report-reviewer", mfa: true);
        using var publisher = lab.Client("report-publisher", mfa: true);
        using var administrator = lab.Client("admin-a", mfa: true);
        using var owner = lab.Client("customer-a");
        using var otherCustomer = lab.Client("customer-b");
        var ticket = await lab.Insert("in_progress", lab.TechA);
        var url = lab.Request(ticket) + "/report";
        var firstPdf = url + "/versions/1/pdf";
        var secondPdf = url + "/versions/2/pdf";
        var form = new { action = "submit", activities = new[] { new { description = "Gerät geprüft", minutes = 45 } },
            result = "Funktion wiederhergestellt.Überprüfung abgeschlossen.", materials = "Dichtung" };

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await unassigned.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherCustomer.GetAsync(url)).StatusCode);
        var empty = await technician.GetAsync(url);
        Assert.Equal("\"r0\"", empty.Headers.ETag?.Tag);
        Assert.Contains("submit", await empty.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Post(owner, url + "/commands", form, revision: 0)).StatusCode);
        var key = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(technician, url + "/commands", form, key, revision: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(technician, url + "/commands", form, key, revision: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await lab.Post(technician, url + "/commands", form, revision: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Post(publisher, url + "/commands",
            new { action = "approve", version = 1 }, revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(approver, url + "/commands",
            new { action = "reject", version = 1, reason = "Dauer überprüfen." }, revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(technician, url + "/commands", form, revision: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(secondPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(approver, url + "/commands",
            new { action = "approve", version = 2 }, revision: 3)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Post(approver, url + "/commands",
            new { action = "publish", version = 2 }, revision: 4)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(publisher, url + "/commands",
            new { action = "publish", version = 2 }, revision: 4)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(secondPdf)).StatusCode);
        Assert.True(await lab.ProcessReport());
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(publisher, url + "/commands",
            new { action = "publish", version = 2 }, revision: 4)).StatusCode);

        var download = await owner.GetAsync(secondPdf);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.True(download.Headers.CacheControl?.NoStore);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString((await download.Content.ReadAsByteArrayAsync())[..5]));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(firstPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherCustomer.GetAsync(secondPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(lab.Request(ticket, "other") + "/report/versions/2/pdf")).StatusCode);
        Assert.DoesNotContain("Dauer überprüfen.", await (await owner.GetAsync(url)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(administrator, lab.Request(ticket) + "/commands",
            new { action = "complete" }, revision: 1)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await lab.Post(publisher, url + "/commands",
            new { action = "revoke", version = 2 }, revision: 5)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(secondPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await approver.GetAsync(secondPdf)).StatusCode);
        await using var isolated = new NpgsqlConnection(lab.Runtime);
        await isolated.OpenAsync();
        Assert.Equal(0L, await Lab.Read<long>(isolated, "select count(*) from ts.report_files"));
        await using var tx = await isolated.BeginTransactionAsync();
        await Lab.Scope(isolated, lab.TenantB);
        Assert.Equal(0L, await Lab.Read<long>(isolated, "select count(*) from ts.report_versions"));
        await tx.RollbackAsync();
        if (Environment.GetEnvironmentVariable("TASKSERVICE_REPORT_BROWSER") is { Length: > 0 } browserScript)
            await lab.RunBrowser(browserScript, invoice: false);
    }

    [Fact]
    public async Task AStaffAuthorCannotApproveTheirOwnReportByDefault()
    {
        using var author = lab.Client("report-author", mfa: true);
        var ticket = await lab.Insert("in_progress", lab.TechA);
        var url = lab.Request(ticket) + "/report";
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(author, url + "/commands",
            new { action = "submit", activities = new[] { new { description = "Gerät geprüft", minutes = 45 } },
                result = "Funktion wiederhergestellt." }, revision: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(author, url + "/commands",
            new { action = "approve", version = 1 }, revision: 1)).StatusCode);
    }

    [Fact]
    public async Task InvoiceOriginalWaitsForScanAndSeparationOfDutiesAndIsRevocable()
    {
        using var uploader = lab.Client("invoice-uploader", mfa: true);
        using var reviewer = lab.Client("report-reviewer", mfa: true);
        using var publisher = lab.Client("report-publisher", mfa: true);
        using var owner = lab.Client("customer-a");
        using var stranger = lab.Client("customer-b");
        var ticket = await lab.Insert("in_progress", lab.TechA);
        var url = lab.Request(ticket) + "/invoices";
        var original = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"><Synthetic>Prüfung</Synthetic></Invoice>
            """u8.ToArray();

        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Upload(owner, url, original)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await lab.Upload(uploader, url, "<Invoice />"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            (await lab.Upload(uploader, url, original, name: "../invoice.xml")).StatusCode);
        var key = Guid.NewGuid().ToString();
        var response = await lab.Upload(uploader, url, original, key: key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = result.GetProperty("id").GetGuid();
        var visibleBefore = await lab.VisibleInvoiceFiles(lab.TenantA);
        var item = url + "/" + id;
        var pdf = item + "/versions/1/download";
        Assert.Equal(response.Headers.Location, (await lab.Upload(uploader, url, original, key: key)).Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await uploader.GetAsync(url)).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>(url)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await reviewer.GetAsync(pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(reviewer, item + "/commands",
            new { action = "approve", version = 1 }, revision: 1)).StatusCode);
        Assert.Equal(visibleBefore, await lab.VisibleInvoiceFiles(lab.TenantA));

        var cleanScanner = Lab.LocalClamAv() ?? new FixtureInvoiceScanner(clean: true);
        Assert.True(await lab.ProcessInvoice(cleanScanner));
        Assert.Equal(visibleBefore + 1, await lab.VisibleInvoiceFiles(lab.TenantA));
        Assert.Equal(0L, await lab.VisibleInvoiceFiles(lab.TenantB));
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(uploader, item + "/commands",
            new { action = "approve", version = 1 }, revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(reviewer, item + "/commands",
            new { action = "approve", version = 1 }, revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Post(reviewer, item + "/commands",
            new { action = "publish", version = 1 }, revision: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(publisher, item + "/commands",
            new { action = "publish", version = 1 }, revision: 2)).StatusCode);

        var data = await owner.GetFromJsonAsync<JsonElement>(url);
        Assert.Single(data.GetProperty("items").EnumerateArray());
        var download = await owner.GetAsync(pdf);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/xml", download.Content.Headers.ContentType?.MediaType);
        Assert.True(download.Headers.CacheControl?.NoStore);
        Assert.Equal(original, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(lab.Request(ticket, "other") + $"/invoices/{id}/versions/1/download")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(publisher, item + "/commands",
            new { action = "revoke", version = 1 }, revision: 3)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(pdf)).StatusCode);

        var marker = """
            <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"><Synthetic>TASKSERVICE-QA-SCAN-MARKER</Synthetic></Invoice>
            """u8.ToArray();
        Assert.Equal(HttpStatusCode.Created, (await lab.Upload(uploader, url, marker, id: id, revision: 4)).StatusCode);
        Assert.True(await lab.ProcessInvoice(Lab.LocalClamAv() ?? new FixtureInvoiceScanner(clean: false)));
        Assert.Contains("quarantined", await (await uploader.GetAsync(item)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await reviewer.GetAsync(item + "/versions/2/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(reviewer, item + "/commands",
            new { action = "approve", version = 2 }, revision: 5)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await lab.Upload(uploader, url, original, id: id, revision: 5)).StatusCode);
        Assert.True(await lab.ProcessInvoice(new UnavailableInvoiceScanner()));
        Assert.Contains("pending", await (await uploader.GetAsync(item)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await reviewer.GetAsync(item + "/versions/3/download")).StatusCode);
        if (Environment.GetEnvironmentVariable("TASKSERVICE_INVOICE_BROWSER") is { Length: > 0 } browserScript)
            await lab.RunBrowser(browserScript, invoice: true);
    }

    [Fact]
    public async Task ClamAvAdapterStreamsBoundedFramesAndAcceptsOnlyRecognizedVerdicts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var scanner = new ClamAvScanner(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ClamAv:Host"] = "127.0.0.1", ["ClamAv:Port"] = port.ToString()
        }).Build());
        var bytes = RandomNumberGenerator.GetBytes(80000);
        foreach (var (response, expected) in new[] { ("stream: OK", true), ("stream: TestMarker FOUND", false) })
        {
            var exchange = Receive(response);
            Assert.Equal(expected, await scanner.IsClean(bytes, timeout.Token));
            await exchange;
        }
        var failure = Receive("stream: internal error ERROR");
        await Assert.ThrowsAsync<IOException>(() => scanner.IsClean(bytes, timeout.Token));
        await failure;

        async Task Receive(string response)
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            var command = new byte[10];
            await stream.ReadExactlyAsync(command, timeout.Token);
            Assert.Equal("zINSTREAM\0"u8.ToArray(), command);
            using var received = new MemoryStream();
            var header = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(header, timeout.Token);
                var size = BinaryPrimitives.ReadInt32BigEndian(header);
                if (size == 0) break;
                Assert.InRange(size, 1, 64 * 1024);
                var block = new byte[size];
                await stream.ReadExactlyAsync(block, timeout.Token);
                received.Write(block);
            }
            Assert.Equal(bytes, received.ToArray());
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response + "\0"), timeout.Token);
        }
    }

    [Fact]
    public async Task ChatPersistsSeparatesInternalNotesAndRevalidatesMembership()
    {
        using var admin = lab.Client("admin-a", mfa: true);
        using var customer = lab.Client("customer-a");
        using var tech = lab.Client("tech-chat");
        var ticket = await lab.Insert("assigned", lab.TechChat);
        var internalRoom = lab.Base() + "/rooms/internal?requestId=" + ticket;
        var customerRoom = lab.Base() + "/rooms/customer?requestId=" + ticket;
        Assert.Equal(HttpStatusCode.Created,
            (await lab.Post(admin, internalRoom, new { body = "Nur intern" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync(internalRoom)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tech.GetAsync(internalRoom)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await lab.Post(customer, customerRoom, new { body = "Nachricht an den Betrieb" })).StatusCode);
        Assert.Contains("Nachricht an den Betrieb", await customer.GetStringAsync(customerRoom));
        Assert.DoesNotContain("Nur intern", await customer.GetStringAsync(customerRoom));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var hub = lab.ChatConnection("tech-chat");
        await hub.StartAsync(timeout.Token);
        await using var watch = hub.StreamAsync<long>("Watch", "lab", "internal", ticket, 0L, timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await watch.MoveNextAsync());
        Assert.True(watch.Current > 0);
        await lab.Revoke(lab.TechChat);
        await Assert.ThrowsAsync<HubException>(async () => { await watch.MoveNextAsync(); });
        Assert.Equal(HttpStatusCode.NotFound, (await tech.GetAsync(internalRoom)).StatusCode);
    }

    [Fact]
    public async Task PlatformProvisioningSuspensionAndAuditKeepBusinessDataBehindSeparateDatabaseRole()
    {
        using var platform = lab.Client("platform-admin", mfa: true);
        using var noMfa = lab.Client("platform-admin");
        using var company = lab.Client("admin-a", mfa: true);
        using var guest = lab.Client();
        using var customer = lab.Client("customer-a");
        const string slug = "qa-platform-company";
        const string root = "/api/v1/platform";
        var companyPath = root + "/companies/" + slug + "/commands";
        var create = new { slug, name = "Werkstatt Prüfbetrieb", accent = "#0A6159" };
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Post(noMfa, root + "/companies", create, revision: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.Post(company, root + "/companies", create, revision: 0)).StatusCode);
        var key = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await lab.Post(platform, root + "/companies", create, key, revision: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await lab.Post(platform, root + "/companies", create, key, revision: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.Post(platform, root + "/companies", create, revision: 0)).StatusCode);
        var tenant = await lab.TenantId(slug);
        var request = await lab.Insert("verified", tenant: tenant);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(Lab.TenantUrl(slug))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(lab.Request(request, slug))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync(lab.Request(request, slug))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await lab.Post(platform, companyPath,
            new { action = "brand", name = "Werkstatt Prüfung", accent = "#1A6159" }, revision: 1)).StatusCode);
        var publicCompany = await guest.GetFromJsonAsync<JsonElement>(Lab.TenantUrl(slug));
        Assert.Equal("#1a6159", publicCompany.GetProperty("accent").GetString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await lab.Post(platform, companyPath,
            new { action = "suspend", reason = "Geplante Prüfung" }, revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(platform, companyPath,
            new { action = "suspend", reason = "Geplante Prüfung" }, revision: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(Lab.TenantUrl(slug))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync(lab.Request(request, slug))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(platform, companyPath,
            new { action = "activate", reason = "Prüfung abgeschlossen" }, revision: 3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(lab.Request(request, slug))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(platform, companyPath,
            new { action = "deactivate", reason = "Prüfmandant beendet" }, revision: 4)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync(lab.Request(request, slug))).StatusCode);

        var audit = (await platform.GetFromJsonAsync<JsonElement>(root + "/audit")).GetProperty("items").EnumerateArray();
        Assert.Equal(5, audit.Count(x => x.GetProperty("slug").GetString() == slug));
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(root + "/audit")).StatusCode);
        await using var privileged = new NpgsqlConnection(lab.PlatformConnection);
        await privileged.OpenAsync();
        var blocked = await Assert.ThrowsAsync<PostgresException>(() => Lab.Read<long>(privileged, "select count(*) from ts.requests"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, blocked.SqlState);
        await using var normal = new NpgsqlConnection(lab.Runtime);
        await normal.OpenAsync();
        var blockedAudit = await Assert.ThrowsAsync<PostgresException>(() => Lab.Read<long>(normal, "select count(*) from ts.platform_audit"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, blockedAudit.SqlState);
        var blockedWrite = await Assert.ThrowsAsync<PostgresException>(() =>
            Lab.Execute(normal, "update ts.tenants set name='Forbidden'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, blockedWrite.SqlState);
    }

    [Fact]
    public async Task OpenApi31IsServedAndPlatformAccessRequiresAssurance()
    {
        using var guest = lab.Client();
        using var platform = lab.Client("platform-admin", mfa: true);
        var api = await guest.GetFromJsonAsync<JsonElement>("/api/openapi/v1.json");
        Assert.StartsWith("3.1.", api.GetProperty("openapi").GetString());
        Assert.True(api.GetProperty("paths").TryGetProperty("/api/v1/tenants/{slug}/requests", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/platform/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await platform.GetAsync("/api/v1/platform/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync(lab.Request(lab.FixtureA))).StatusCode);
    }
}

public sealed class Lab : IAsyncLifetime
{
    private const string Issuer = "https://identity.example.invalid";
    private string admin = "";
    private WebApplicationFactory<Program> factory = null!;
    public string Runtime { get; private set; } = "";
    public string PlatformConnection { get; private set; } = "";
    private string worker = "";
    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid TechA { get; } = Guid.NewGuid();
    public Guid TechChat { get; } = Guid.NewGuid();
    public Guid FixtureA { get; private set; }
    public Guid FixtureB { get; private set; }
    public static string TenantUrl(string slug = "lab") => $"/api/v1/tenants/{slug}";
    public static string RequestUrl(Guid id, string slug = "lab") => TenantUrl(slug) + $"/requests/{id}";
    public string Base() => TenantUrl("lab");
    public string Request(Guid id, string? slug = null) => RequestUrl(id, slug ?? "lab");

    public async Task InitializeAsync()
    {
        var bootstrap = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TASKSERVICE_TEST_ADMIN_DB")
            ?? throw new InvalidOperationException("Provide a disposable localhost PostgreSQL server."));
        if (bootstrap.Host is not ("127.0.0.1" or "localhost"))
            throw new InvalidOperationException("Tests only run against a disposable local PostgreSQL server.");
        var dbName = "ts_test_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(bootstrap.ConnectionString))
        {
            await conn.OpenAsync();
            await Execute(conn, $"""
                DO $$ BEGIN CREATE ROLE taskservice_api LOGIN PASSWORD 'local-test-only'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
                DO $$ BEGIN CREATE ROLE taskservice_worker LOGIN PASSWORD 'local-test-only'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
                DO $$ BEGIN CREATE ROLE taskservice_platform LOGIN PASSWORD 'local-test-only'; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
                """);
            await Execute(conn, $"CREATE DATABASE {dbName}");
        }
        bootstrap.Database = dbName;
        admin = bootstrap.ConnectionString;
        var runtime = new NpgsqlConnectionStringBuilder(admin)
        {
            Username = "taskservice_api", Password = "local-test-only", MaxPoolSize = 10
        };
        Runtime = runtime.ConnectionString;
        worker = new NpgsqlConnectionStringBuilder(Runtime) { Username = "taskservice_worker" }.ConnectionString;
        PlatformConnection = new NpgsqlConnectionStringBuilder(Runtime) { Username = "taskservice_platform" }.ConnectionString;
        await using (var db = new ServiceDb(new DbContextOptionsBuilder<ServiceDb>().UseNpgsql(admin).Options))
            await db.Database.MigrateAsync();
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await Execute(conn, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "grants.sql")));
            await Execute(conn, $"""
                INSERT INTO ts.tenants VALUES ('{TenantA}','lab','Werkstatt Nord','#0a6159',true),
                                             ('{TenantB}','other','Werkstatt Süd','#0a6159',true);
                """);
            foreach (var (subject, id, role) in new[] { ("admin-a", Guid.NewGuid(), "admin"),
                ("tech-a", TechA, "technician"), ("tech-chat", TechChat, "technician"),
                ("report-reviewer", Guid.NewGuid(), "dispatcher"), ("report-publisher", Guid.NewGuid(), "dispatcher"),
                ("report-author", Guid.NewGuid(), "admin"), ("invoice-uploader", Guid.NewGuid(), "dispatcher") })
                await Execute(conn, $"""
                    INSERT INTO ts.members VALUES ('{TenantA}','{id}','{Actor.SubjectKey(Issuer, subject)}','{subject}','{role}',true)
                    """);
            await Execute(conn, $"""
                INSERT INTO ts.report_permissions
                SELECT m.tenant_id, m.id, p.permission FROM ts.members m JOIN (VALUES
                    ('report-reviewer', 'report.approve'), ('report-publisher', 'report.publish'),
                    ('report-author', 'report.create'), ('report-author', 'report.approve')
                ) p(name, permission) ON p.name = m.name WHERE m.tenant_id = '{TenantA}'
                """);
            await Execute(conn, $"""
                INSERT INTO ts.invoice_permissions
                SELECT m.tenant_id, m.id, p.permission FROM ts.members m JOIN (VALUES
                    ('report-reviewer', 'invoice.approve'), ('report-publisher', 'invoice.publish'),
                    ('invoice-uploader', 'invoice.upload'), ('invoice-uploader', 'invoice.approve')
                ) p(name, permission) ON p.name = m.name WHERE m.tenant_id = '{TenantA}'
                """);
        }
        FixtureA = await Insert("verified");
        FixtureB = await Insert("verified", tenant: TenantB);
        Environment.SetEnvironmentVariable("ConnectionStrings__Runtime", Runtime);
        Environment.SetEnvironmentVariable("ConnectionStrings__Platform", PlatformConnection);
        Environment.SetEnvironmentVariable("PublicBaseUrl", "https://localhost");
        Environment.SetEnvironmentVariable("Mail__Host", "mail.example.invalid");
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Identity:PrivilegedAcr"] = "verified-mfa" }));
            b.ConfigureServices(s =>
            {
                s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestIdentity>("test", _ => { });
                s.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = "test");
            });
        });
    }

    public WebApplicationFactory<Program> IsolatedHost() => factory.WithWebHostBuilder(_ => { });

    public HttpClient Client(string? subject = null, bool mfa = false)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        if (subject is not null)
            client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        if (mfa)
            client.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        return client;
    }

    public HubConnection ChatConnection(string subject)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["Origin"] = "https://localhost";
            request.Headers["X-Test-Subject"] = subject;
        };
        return new HubConnectionBuilder().WithUrl("https://localhost/hubs/conversations", options =>
        {
            options.SkipNegotiation = true;
            options.Transports = HttpTransportType.WebSockets;
            options.WebSocketFactory = async (context, ct) => await client.ConnectAsync(context.Uri, ct);
        }).Build();
    }

    public Task<HttpResponseMessage> Submit(HttpClient c, string title, string? key = null) =>
        Post(c, Base() + "/requests", new { title, description = "Synthetischer Test", email = "qa@example.invalid" }, key);

    public async Task<HttpResponseMessage> Post(HttpClient c, string path, object body, string? key = null, int? revision = null)
    {
        var state = await c.GetFromJsonAsync<JsonElement>("/api/v1/session");
        using var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        r.Headers.Add("X-CSRF-Token", state.GetProperty("csrfToken").GetString());
        r.Headers.Add("Origin", "https://localhost");
        r.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        if (revision is not null)
            r.Headers.TryAddWithoutValidation("If-Match", $"\"r{revision}\"");
        return await c.SendAsync(r);
    }

    public async Task<HttpResponseMessage> Upload(HttpClient client, string path, byte[] content,
        string name = "Rechnung.xml", Guid? id = null, int revision = 0, string? key = null)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/v1/session");
        using var data = new ByteArrayContent(content);
        data.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        var query = id is null ? "" : $"?invoiceId={id}";

        using var request = new HttpRequestMessage(HttpMethod.Post, path + "/upload" + query) { Content = data };
        request.Headers.Add("Origin", "https://localhost");
        request.Headers.Add("X-Invoice-Name", Uri.EscapeDataString(name));
        request.Headers.Add("X-CSRF-Token", session.GetProperty("csrfToken").GetString());
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("If-Match", $"\"r{revision}\"");
        return await client.SendAsync(request);
    }

    public async Task<long> VisibleInvoiceFiles(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(Runtime);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Scope(connection, tenant);
        return await Read<long>(connection, "select count(*) from ts.invoice_files");
    }

    public static InvoiceScanner? LocalClamAv() =>
        int.TryParse(Environment.GetEnvironmentVariable("TASKSERVICE_TEST_CLAMAV_PORT"), out var port)
            ? new ClamAvScanner(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ClamAv:Host"] = "127.0.0.1", ["ClamAv:Port"] = port.ToString() }).Build())
            : null;

    public async Task<bool> ProcessInvoice(InvoiceScanner scanner)
    {
        await using var db = new ServiceDb(new DbContextOptionsBuilder<ServiceDb>().UseNpgsql(worker).Options);
        return await InvoiceScanWorker.ProcessNext(db, TenantA, scanner, NullLogger.Instance, CancellationToken.None);
    }

    public async Task<bool> ProcessPhoto(InvoiceScanner scanner)
    {
        await using var db = new ServiceDb(new DbContextOptionsBuilder<ServiceDb>().UseNpgsql(worker).Options);
        return await RequestPhotoScan.ProcessNext(db, TenantA, scanner, NullLogger.Instance, CancellationToken.None);
    }

    public async Task<string> TokenFor(Guid request, IServiceProvider? services = null)
    {
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT payload FROM ts.outbox WHERE kind='mail'", conn);
        await using var rows = await command.ExecuteReaderAsync();
        var protector = (services ?? factory.Services).GetRequiredService<IDataProtectionProvider>().CreateProtector("taskservice-outbox-mail-v1");
        while (await rows.ReadAsync())
        {
            var content = JsonSerializer.Deserialize<MailPayload>(protector.Unprotect(rows.GetString(0)))!.Content;
            if (content.Contains(request.ToString()))
                return content[(content.LastIndexOf('#') + 1)..];
        }
        throw new InvalidOperationException("No verification message for test request.");
    }

    public async Task<Guid> TenantId(string slug)
    {
        await using var db = new NpgsqlConnection(admin);
        await db.OpenAsync();
        await using var query = new NpgsqlCommand("select id from ts.tenants where slug=$1", db);
        query.Parameters.Add(new NpgsqlParameter { Value = slug });
        return (Guid)(await query.ExecuteScalarAsync())!;
    }

    public async Task<Guid> Insert(string state, Guid? technician = null, Guid? tenant = null)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        var tech = technician is { } t ? $"'{t}'" : "null";
        await Execute(conn, $"""
            INSERT INTO ts.requests (tenant_id,id,title,description,contact_email,customer_key,technician_id,state,revision,created_at)
            VALUES ('{tenant ?? TenantA}','{id}','Testvorgang','Synthetisch','qa@example.invalid',
                    '{Actor.SubjectKey(Issuer, "customer-a")}',{tech},'{state}',1,now())
            """);
        return id;
    }

    public async Task RunBrowser(string script, bool invoice)
    {
        var webRoot = Environment.GetEnvironmentVariable("TASKSERVICE_BROWSER_WEBROOT")
            ?? throw new InvalidOperationException("Provide the absolute path to the built web application.");
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        await using var browserHost = factory.WithWebHostBuilder(builder =>
        {
            builder.UseWebRoot(webRoot);
            builder.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        });
        browserHost.UseKestrel();
        browserHost.StartServer();
        var origin = browserHost.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single(x => x.StartsWith("https://127.0.0.1:", StringComparison.Ordinal));
        var id = await Insert(invoice ? "in_progress" : "assigned", TechA);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var scanner = LocalClamAv() ?? new FixtureInvoiceScanner(clean: true);
        var dispatch = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                if (invoice)
                    await ProcessInvoice(scanner);
                else
                    await ProcessReport();
                await Task.Delay(500, timeout.Token);
            }
        });

        var args = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        args.ArgumentList.Add(script);
        args.Environment["TASKSERVICE_BROWSER_BASE"] = origin;
        args.Environment["TASKSERVICE_BROWSER_REQUEST"] = id.ToString();
        using var browser = Process.Start(args) ?? throw new InvalidOperationException("Could not start Playwright.");
        var stdout = browser.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = browser.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await browser.WaitForExitAsync(timeout.Token);
            Assert.True(browser.ExitCode == 0, $"{await stdout}\n{await stderr}");
            Console.WriteLine(await stdout);
        }
        finally
        {
            if (!browser.HasExited)
                browser.Kill(entireProcessTree: true);
            await timeout.CancelAsync();
            try { await dispatch; } catch (OperationCanceledException) { }
        }
    }

    public async Task<bool> ProcessReport()
    {
        await using var db = new ServiceDb(new DbContextOptionsBuilder<ServiceDb>().UseNpgsql(worker).Options);
        return await ReportWorker.ProcessNext(db, TenantA, NullLogger.Instance, CancellationToken.None);
    }

    public async Task Revoke(Guid id)
    {
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        await Execute(conn, $"UPDATE ts.members SET active=false WHERE id='{id}' AND tenant_id='{TenantA}'");
    }

    public static async Task Execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> Read<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public static Task Scope(NpgsqlConnection conn, Guid id) =>
        Execute(conn, $"SELECT set_config('taskservice.tenant','{id}',true)");

    public async Task DisposeAsync()
    {
        if (factory is not null)
            await factory.DisposeAsync();
        if (admin.Length == 0)
            return;
        var database = new NpgsqlConnectionStringBuilder(admin);
        var name = database.Database;
        database.Database = "postgres";
        await using var conn = new NpgsqlConnection(database.ConnectionString);
        await conn.OpenAsync();
        await Execute(conn, $"DROP DATABASE {name} WITH (FORCE)");
    }

    public sealed class TestIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory log, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, log, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var sub = Request.Headers["X-Test-Subject"].ToString();
            if (sub.Length == 0)
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<System.Security.Claims.Claim>
                { new("sub", sub), new("ts:issuer", Issuer), new("ts:realm", IdentitySessions.WorkforceClaim) };
            if (Request.Headers["X-Test-Email"].ToString() is { Length: > 0 } email)
                claims.Add(new("email", email));
            if (Request.Headers["X-Test-Email-Verified"] == "1")
                claims.Add(new("email_verified", "true"));
            if (Request.Headers["X-Test-Mfa"] == "1")
                claims.Add(new(IdentitySessions.AssuranceClaim, "verified-mfa"));
            if (sub == "platform-admin")
                claims.Add(new("taskservice_role", "platform_admin"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}

public sealed class FixtureInvoiceScanner(bool clean) : InvoiceScanner
{
    public Task<bool> IsClean(byte[] content, CancellationToken ct) => Task.FromResult(clean);
}

public sealed class UnavailableInvoiceScanner : InvoiceScanner
{
    public Task<bool> IsClean(byte[] content, CancellationToken ct) => throw new IOException("Synthetic scanner outage.");
}

internal sealed class FakeOidcProvider(string issuer, string clientId) : HttpMessageHandler
{
    public SymmetricSecurityKey Key { get; } = new(RandomNumberGenerator.GetBytes(32));
    public string Challenge { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Subject { get; set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(issuer + "/token", request.RequestUri?.AbsoluteUri);
        var body = QueryHelpers.ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken));
        Assert.Equal("authorization_code", body["grant_type"].ToString());
        Assert.Equal("synthetic-signed-identity", body["code"].ToString());
        Assert.Equal(Challenge, Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(body["code_verifier"].ToString()))));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(issuer, clientId,
        [
            new System.Security.Claims.Claim("sub", Subject),
            new System.Security.Claims.Claim("nonce", Nonce),
            new System.Security.Claims.Claim("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new System.Security.Claims.Claim("acr", "verified-mfa")
        ], now.AddSeconds(-5), now.AddMinutes(10), new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                token_type = "Bearer", access_token = "fixture-access", expires_in = 600,
                id_token = new JwtSecurityTokenHandler().WriteToken(token)
            })
        };
    }
}


internal sealed class TestEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> previous;

    public TestEnvironment(Dictionary<string, string> variables)
    {
        previous = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in variables)
            Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, value) in previous)
            Environment.SetEnvironmentVariable(key, value);
    }
}
