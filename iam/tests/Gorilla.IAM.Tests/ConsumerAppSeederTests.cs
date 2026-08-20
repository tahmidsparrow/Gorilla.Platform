using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Tests;

/// <summary>
/// In-memory SQLite, matching GorillaHR's own testing convention (see
/// backend/tests/conftest.py) — no MySQL needed to verify the seeder's
/// actual database behaviour, not just its static data.
/// </summary>
public class ConsumerAppSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IamDbContext _db;

    public ConsumerAppSeederTests()
    {
        // The connection must stay open for the lifetime of the test: SQLite's
        // ":memory:" database is destroyed the moment its one connection closes.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<IamDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new IamDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Seeds_both_apps_with_their_full_role_vocabulary()
    {
        await ConsumerAppSeeder.SeedAsync(_db);

        var apps = await _db.ConsumerApps.Include(a => a.Roles).ToListAsync();

        Assert.Equal(2, apps.Count);
        var hr = apps.Single(a => a.Key == "hr");
        Assert.Equal(["Employee", "Line Manager", "HR", "Admin"], hr.Roles.Select(r => r.Role));
    }

    [Fact]
    public async Task Running_it_twice_does_not_duplicate_rows()
    {
        await ConsumerAppSeeder.SeedAsync(_db);
        await ConsumerAppSeeder.SeedAsync(_db);

        Assert.Equal(2, await _db.ConsumerApps.CountAsync());
        Assert.Equal(8, await _db.ConsumerAppRoles.CountAsync()); // 4 + 4
    }

    [Fact]
    public async Task Leaves_an_already_seeded_app_untouched_when_seeding_a_new_one()
    {
        // Simulates the real scenario the per-app AnyAsync check exists for:
        // "hr" already seeded (and, hypothetically, since hand-edited by an
        // admin — e.g. a role appended for a new module), a fresh boot must
        // not reset it back to the static list.
        _db.ConsumerApps.Add(new Data.Entities.ConsumerApp
        {
            Key = "hr",
            Name = "GorillaHR (customized)",
            Roles = [new Data.Entities.ConsumerAppRole { AppKey = "hr", Role = "Employee" }],
        });
        await _db.SaveChangesAsync();

        await ConsumerAppSeeder.SeedAsync(_db);

        var hr = await _db.ConsumerApps.Include(a => a.Roles).SingleAsync(a => a.Key == "hr");
        Assert.Equal("GorillaHR (customized)", hr.Name);
        Assert.Single(hr.Roles);

        Assert.True(await _db.ConsumerApps.AnyAsync(a => a.Key == "ats"));
    }
}
