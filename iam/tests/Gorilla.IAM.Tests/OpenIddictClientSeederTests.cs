using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Gorilla.IAM.Tests;

/// <summary>
/// Unlike the other Seeding tests, this can't just `new IamDbContext(options)`
/// directly — IOpenIddictApplicationManager is resolved through OpenIddict's
/// own DI registration (AddOpenIddict().AddCore()...), not constructed by
/// hand, so this builds a minimal real service provider wired the same way
/// Program.cs wires the full app, just against SQLite instead of MySQL.
/// </summary>
public class OpenIddictClientSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly IamDbContext _db;

    public OpenIddictClientSeederTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var collection = new ServiceCollection();
        collection.AddDbContext<IamDbContext>(options => options.UseSqlite(_connection));
        collection.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<IamDbContext>());
        _services = collection.BuildServiceProvider();

        _db = _services.GetRequiredService<IamDbContext>();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private IOpenIddictApplicationManager Manager => _services.GetRequiredService<IOpenIddictApplicationManager>();

    [Fact]
    public async Task Warns_and_registers_nothing_when_redirect_uris_are_not_configured()
    {
        var warning = await OpenIddictClientSeeder.SeedAsync(Manager, redirectUrisCsv: null);

        Assert.NotNull(warning);
        Assert.Null(await Manager.FindByClientIdAsync(OpenIddictClientSeeder.AtsClientId));
    }

    [Fact]
    public async Task Registers_a_public_PKCE_client_with_the_configured_redirect_uris()
    {
        var warning = await OpenIddictClientSeeder.SeedAsync(
            Manager, "http://localhost:5173/callback, http://127.0.0.1:9999/callback");

        Assert.Null(warning);
        var app = await Manager.FindByClientIdAsync(OpenIddictClientSeeder.AtsClientId);
        Assert.NotNull(app);
        Assert.Equal(OpenIddictConstants.ClientTypes.Public, await Manager.GetClientTypeAsync(app));

        var redirectUris = await Manager.GetRedirectUrisAsync(app);
        Assert.Contains("http://localhost:5173/callback", redirectUris.Select(u => u.ToString()));
        Assert.Contains("http://127.0.0.1:9999/callback", redirectUris.Select(u => u.ToString()));

        var permissions = await Manager.GetPermissionsAsync(app);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, permissions);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.RefreshToken, permissions);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.GrantTypes.Password, permissions);

        var requirements = await Manager.GetRequirementsAsync(app);
        Assert.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange, requirements);
    }

    [Fact]
    public async Task Running_it_twice_does_not_duplicate_or_error()
    {
        await OpenIddictClientSeeder.SeedAsync(Manager, "http://localhost:5173/callback");
        var warning = await OpenIddictClientSeeder.SeedAsync(Manager, "http://localhost:5173/callback");

        Assert.Null(warning);
        var count = 0;
        await foreach (var _ in Manager.ListAsync()) count++;
        Assert.Equal(1, count);
    }

    /// <summary>The whole point of the "different table" doc comment — this
    /// seeder must never accidentally touch ConsumerApps.</summary>
    [Fact]
    public async Task Does_not_write_anything_to_ConsumerApps()
    {
        await OpenIddictClientSeeder.SeedAsync(Manager, "http://localhost:5173/callback");

        Assert.Equal(0, await _db.ConsumerApps.CountAsync());
    }
}
