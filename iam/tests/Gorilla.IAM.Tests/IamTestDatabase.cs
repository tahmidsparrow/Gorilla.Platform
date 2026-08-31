using Gorilla.IAM.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Gorilla.IAM.Tests;

/// <summary>
/// A throwaway MySQL database plus a booted <see cref="WebApplicationFactory{T}"/>
/// pointed at it, dropped on dispose.
///
/// The MySQL-backed tests used to run straight against whatever
/// GORILLA_IAM_TEST_MYSQL_CONNECTION named — in practice the developer's real
/// gorilla_iam — seeding subjects and credentials on every run and never removing
/// them. That left 33 stray oidc-flow-test-* / console-endpoints-test-* accounts
/// behind before anyone noticed. Cleaning up in a finally block would have been the
/// smaller fix and the wrong one: a test that throws part-way, or a run killed
/// mid-flight, still leaks. A database that only ever existed for one test class
/// cannot pollute anything, and it matches how Recruitment.Gorilla's own ApiFixture
/// and IamTestFixture already work (RG_ITest_{guid} + EnsureDeletedAsync).
///
/// The connection string env var is now read as a <b>server</b> to borrow, not a
/// database to write into — only its host/port/credentials are used; the database
/// name is replaced.
///
/// This has to migrate the throwaway database itself, because Gorilla.IAM's
/// Program.cs deliberately does not migrate on boot (see the comment on its startup
/// seeding block — HR's one-shot migrate-service split is the intended model). The
/// seeders in that block do run, so consumer_apps, the bootstrap admin and the "ats"
/// OpenIddict client are all populated once the factory starts.
/// </summary>
public sealed class IamTestDatabase : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly List<string> _envVarsToClear = [];

    private IamTestDatabase(string connectionString, WebApplicationFactory<Program> factory)
    {
        _connectionString = connectionString;
        Factory = factory;
    }

    public WebApplicationFactory<Program> Factory { get; }

    /// <param name="baseConnectionString">Any connection string on the target server —
    /// only its host/port/credentials matter, the database name is replaced.</param>
    /// <param name="extraEnvironment">Configuration this particular test class needs
    /// Program.cs to see at startup, e.g. Iam__AtsClientRedirectUris.</param>
    public static async Task<IamTestDatabase> CreateAsync(
        string baseConnectionString, IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        // Kept short on purpose. Pomelo takes a MySQL user-level lock named
        // "__{database}_EFMigrationsLock" while migrating, and MySQL rejects lock names
        // over 64 characters — so the database name has a hard budget of 45. A
        // full 32-char GUID with a descriptive prefix blows straight through it
        // (confirmed the hard way: every migration failed with "should not exceed 64
        // characters"). 16 hex chars is still ~1 in 10^19 per run.
        var connectionString = new MySqlConnectionStringBuilder(baseConnectionString)
        {
            Database = $"iam_test_{Guid.NewGuid():N}"[..25],
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<IamDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(9, 0, 0)))
            .Options;
        await using (var db = new IamDbContext(options))
            await db.Database.MigrateAsync(); // creates the database too

        // Program.cs reads ConnectionStrings:DefaultConnection synchronously at the top
        // of Main, before WebApplicationBuilder.Build() — earlier than
        // WebApplicationFactory's ConfigureAppConfiguration hook can inject config for a
        // minimal-hosting entry point (confirmed the hard way: that approach left the
        // connection string empty and Program.cs's own guard threw). Real process
        // environment variables, set before the factory ever touches Program.Main, are
        // visible the same way `dotnet run` with real env vars would see them.
        //
        // Being process-global, they are also why the classes using this share one xUnit
        // collection — see IamMySqlCollection.
        var factory = new WebApplicationFactory<Program>();
        var instance = new IamTestDatabase(connectionString, factory);

        instance.SetEnv("ConnectionStrings__DefaultConnection", connectionString);
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
            instance.SetEnv(key, value);

        // Touching .Services is what actually boots the host, so it must happen after
        // the environment is in place.
        _ = factory.WithWebHostBuilder(b => b.UseEnvironment("Development")).Services;
        return instance;
    }

    private void SetEnv(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        _envVarsToClear.Add(key);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            await db.Database.EnsureDeletedAsync();
        }
        catch
        {
            // Best effort via the running host; fall back to dropping it directly so a
            // half-booted factory can't strand a database.
            await using var db = new IamDbContext(new DbContextOptionsBuilder<IamDbContext>()
                .UseMySql(_connectionString, new MySqlServerVersion(new Version(9, 0, 0)))
                .Options);
            await db.Database.EnsureDeletedAsync();
        }
        finally
        {
            await Factory.DisposeAsync();
            foreach (var key in _envVarsToClear)
                Environment.SetEnvironmentVariable(key, null);
        }
    }
}

/// <summary>
/// Serializes every MySQL-backed test class. They each set process-global environment
/// variables (see IamTestDatabase) before booting a host, so running two of them in
/// parallel — xUnit's default across classes — would let one class's connection string
/// clobber another's mid-boot. Same reasoning that put Recruitment.Gorilla's ApiFixture
/// and IamTestFixture into a single collection.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IamMySqlCollection
{
    public const string Name = "iam-mysql";
}
