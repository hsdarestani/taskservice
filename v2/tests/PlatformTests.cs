using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public async Task SimultaneousBookingsAreRejectedAndWorkflowReachesCompletion()
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
        Assert.Equal(HttpStatusCode.OK, (await lab.Post(admin, lab.Request(winner) + "/commands",
            new { action = "complete" }, revision: 4)).StatusCode);
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
                ("tech-a", TechA, "technician"), ("tech-chat", TechChat, "technician") })
                await Execute(conn, $"""
                    INSERT INTO ts.members VALUES ('{TenantA}','{id}','{Actor.SubjectKey(Issuer, subject)}','{subject}','{role}',true)
                    """);
        }
        FixtureA = await Insert("verified");
        FixtureB = await Insert("verified", tenant: TenantB);
        Environment.SetEnvironmentVariable("ConnectionStrings__Runtime", Runtime);
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

    public async Task<string> TokenFor(Guid request)
    {
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT payload FROM ts.outbox WHERE kind='mail'", conn);
        await using var rows = await command.ExecuteReaderAsync();
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("taskservice-outbox-mail-v1");
        while (await rows.ReadAsync())
        {
            var content = JsonSerializer.Deserialize<MailPayload>(protector.Unprotect(rows.GetString(0)))!.Content;
            if (content.Contains(request.ToString()))
                return content[(content.LastIndexOf('#') + 1)..];
        }
        throw new InvalidOperationException("No verification message for test request.");
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
            var claims = new List<System.Security.Claims.Claim> { new("sub", sub), new("ts:issuer", Issuer) };
            if (Request.Headers["X-Test-Mfa"] == "1")
                claims.Add(new("acr", "verified-mfa"));
            if (sub == "platform-admin")
                claims.Add(new("taskservice_role", "platform_admin"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
