using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using Gorilla.IAM.Data.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Tests;

public class BootstrapAdminSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IamDbContext _db;

    public BootstrapAdminSeederTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<IamDbContext>().UseSqlite(_connection).Options;
        _db = new IamDbContext(options);
        _db.Database.EnsureCreated();

        _db.ConsumerApps.Add(new ConsumerApp
        {
            Key = IamSelfConsumerApp.AppKey,
            Name = "Gorilla.IAM",
            Roles = [new ConsumerAppRole { AppKey = IamSelfConsumerApp.AppKey, Role = IamSelfConsumerApp.AdminRole }],
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Subject> SeedSubjectAsync(string email)
    {
        var subject = new Subject { Email = email, Name = "N", IsActive = true };
        _db.Subjects.Add(subject);
        await _db.SaveChangesAsync();
        return subject;
    }

    [Fact]
    public async Task Grants_iam_admin_to_the_configured_email_when_no_one_holds_it_yet()
    {
        var subject = await SeedSubjectAsync("boss@example.com");

        var warning = await BootstrapAdminSeeder.SeedAsync(_db, "boss@example.com");

        Assert.Null(warning);
        Assert.True(await _db.RoleGrants.AnyAsync(g =>
            g.SubjectId == subject.Id && g.AppKey == IamSelfConsumerApp.AppKey && g.Role == IamSelfConsumerApp.AdminRole));
    }

    [Fact]
    public async Task Does_nothing_and_warns_when_no_email_is_configured()
    {
        var warning = await BootstrapAdminSeeder.SeedAsync(_db, bootstrapAdminEmail: null);

        Assert.NotNull(warning);
        Assert.False(await _db.RoleGrants.AnyAsync());
    }

    [Fact]
    public async Task Warns_instead_of_throwing_when_the_configured_subject_does_not_exist()
    {
        var warning = await BootstrapAdminSeeder.SeedAsync(_db, "nobody@example.com");

        Assert.NotNull(warning);
        Assert.False(await _db.RoleGrants.AnyAsync());
    }

    /// <summary>Never overwrite an operator's own subsequent grant
    /// decisions — e.g. after revoking the bootstrap admin's access on
    /// purpose, a restart must not silently re-grant it.</summary>
    [Fact]
    public async Task Does_not_regrant_once_someone_already_holds_iam_admin_even_a_different_person()
    {
        var original = await SeedSubjectAsync("original-admin@example.com");
        _db.RoleGrants.Add(new RoleGrant { SubjectId = original.Id, AppKey = IamSelfConsumerApp.AppKey, Role = IamSelfConsumerApp.AdminRole });
        await _db.SaveChangesAsync();
        await SeedSubjectAsync("configured-admin@example.com");

        var warning = await BootstrapAdminSeeder.SeedAsync(_db, "configured-admin@example.com");

        Assert.Null(warning);
        Assert.Equal(1, await _db.RoleGrants.CountAsync());
        Assert.True(await _db.RoleGrants.AnyAsync(g => g.SubjectId == original.Id));
    }

    [Fact]
    public async Task Email_matching_is_case_and_whitespace_insensitive()
    {
        var subject = await SeedSubjectAsync("boss@example.com");

        var warning = await BootstrapAdminSeeder.SeedAsync(_db, " Boss@Example.com ");

        Assert.Null(warning);
        Assert.True(await _db.RoleGrants.AnyAsync(g => g.SubjectId == subject.Id));
    }
}
