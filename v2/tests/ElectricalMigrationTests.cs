using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskService.Database;
using Xunit;

namespace TaskService.Tests;

public static class MigrationChecks
{
    // Called from the existing PostgreSQL fixture after restricted runtime roles have been provisioned.
    public static async Task ElectricalUpgradeAndRollback()
    {
        var bootstrap = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TASKSERVICE_TEST_ADMIN_DB"));
        if (bootstrap.Host is not ("localhost" or "127.0.0.1"))
            throw new InvalidOperationException("A disposable loopback database is required.");
        var name = "ts_upgrade_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(bootstrap.ConnectionString);
        await admin.OpenAsync();
        await Lab.Execute(admin, $"CREATE DATABASE {name}");
        try
        {
            bootstrap.Database = name;
            await using var db = new ServiceDb(new DbContextOptionsBuilder<ServiceDb>().UseNpgsql(bootstrap.ConnectionString).Options);
            const string previous = "20261121000900_NativeIdentity";
            await db.Database.MigrateAsync(previous);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO ts.tenants (id,slug,name,accent,active) VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','legacy','Altbestand','#123456',true);
                INSERT INTO ts.requests (tenant_id,id,title,description,contact_email,state,revision,created_at,service_category)
                VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',gen_random_uuid(),'Bestand','Historisch','qa@example.invalid','received',1,now(),'garden');
                """);
            await db.Database.MigrateAsync();
            Assert.Equal("garden", await db.Requests.Select(r => r.ServiceCategory).SingleAsync());
            Assert.Equal("custom", await db.Tenants.Select(t => t.PlanCode).SingleAsync());
            await db.Database.ExecuteSqlRawAsync("UPDATE ts.requests SET service_category='installation'");
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync(previous));
            Assert.Equal("installation", await db.Requests.Select(r => r.ServiceCategory).SingleAsync());
            await db.Database.ExecuteSqlRawAsync("UPDATE ts.requests SET service_category='garden'");
            await db.Database.MigrateAsync(previous);
            Assert.Equal("garden", await db.Requests.Select(r => r.ServiceCategory).SingleAsync());
            await db.Database.MigrateAsync();
            Assert.Equal(5, await db.Set<FeaturePlan>().CountAsync());
        }
        finally
        {
            await Lab.Execute(admin, $"DROP DATABASE {name} WITH (FORCE)");
        }
    }
}
