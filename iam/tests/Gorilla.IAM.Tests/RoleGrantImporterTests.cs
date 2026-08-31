using Gorilla.IAM.Auth;
using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using Gorilla.IAM.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Tests;

public class RoleGrantImporterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IamDbContext _db;

    public RoleGrantImporterTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<IamDbContext>().UseSqlite(_connection).Options;
        _db = new IamDbContext(options);
        _db.Database.EnsureCreated();

        // role_grants.AppKey FKs to consumer_apps.Key with Restrict, and
        // GrantRoleAsync validates the role against consumer_app_roles — so the
        // "ats" app and its vocabulary must exist before any grant can land.
        _db.ConsumerApps.Add(new ConsumerApp
        {
            Key = RoleGrantImporter.AtsAppKey,
            Name = "Recruitment.Gorilla",
            Roles =
            [
                new ConsumerAppRole { AppKey = RoleGrantImporter.AtsAppKey, Role = "SuperAdmin" },
                new ConsumerAppRole { AppKey = RoleGrantImporter.AtsAppKey, Role = "Admin" },
                new ConsumerAppRole { AppKey = RoleGrantImporter.AtsAppKey, Role = "Recruiter" },
                new ConsumerAppRole { AppKey = RoleGrantImporter.AtsAppKey, Role = "Interviewer" },
            ],
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SeedSubjectAsync(string email)
    {
        _db.Subjects.Add(new Subject
        {
            Email = email,
            Name = "A",
            IsActive = true,
            Credential = new Credential { Algorithm = CredentialAlgorithm.Bcrypt, Hash = BcryptPasswordHasher.Hash("x") },
        });
        await _db.SaveChangesAsync();
    }

    private static SubjectImportPlan Plan(string email, params string[] roles) =>
        new(email, "A", true, CredentialAlgorithm.Pbkdf2Sha256, "hash", ImportSource.RgOnly, roles);

    private async Task<string[]> GrantedRolesAsync(string email) =>
        await _db.RoleGrants
            .Where(g => g.Subject.Email == email && g.AppKey == RoleGrantImporter.AtsAppKey)
            .Select(g => g.Role)
            .OrderBy(r => r)
            .ToArrayAsync();

    [Fact]
    public async Task Creates_an_ats_grant_for_each_planned_role()
    {
        await SeedSubjectAsync("a@example.com");

        var result = await RoleGrantImporter.ApplyAsync(_db, [Plan("a@example.com", "Recruiter")]);

        Assert.Equal((1, 0, 0), (result.Granted, result.AlreadyGranted, result.Rejected));
        Assert.Equal(["Recruiter"], await GrantedRolesAsync("a@example.com"));
    }

    /// <summary>RG's multi-role users must get one grant row each, never a single
    /// "primary" role — the unique (SubjectId, AppKey, Role) index maps 1:1 onto
    /// RG's own unique (UserId, Role).</summary>
    [Fact]
    public async Task A_multi_role_subject_gets_one_grant_per_role()
    {
        await SeedSubjectAsync("multi@example.com");

        var result = await RoleGrantImporter.ApplyAsync(_db, [Plan("multi@example.com", "Recruiter", "Interviewer")]);

        Assert.Equal(2, result.Granted);
        Assert.Equal(["Interviewer", "Recruiter"], await GrantedRolesAsync("multi@example.com"));
    }

    /// <summary>Re-running the import is expected (it's how a cutover gets retried),
    /// so a second pass must report AlreadyGranted rather than duplicating rows or
    /// blowing up on the unique index.</summary>
    [Fact]
    public async Task Re_running_grants_nothing_new_and_creates_no_duplicates()
    {
        await SeedSubjectAsync("a@example.com");
        var plans = new[] { Plan("a@example.com", "Recruiter", "Interviewer") };

        await RoleGrantImporter.ApplyAsync(_db, plans);
        var second = await RoleGrantImporter.ApplyAsync(_db, plans);

        Assert.Equal((0, 2, 0), (second.Granted, second.AlreadyGranted, second.Rejected));
        Assert.Equal(2, await _db.RoleGrants.CountAsync());
    }

    /// <summary>There is no FK from role_grants.Role to consumer_app_roles, so
    /// GrantRoleAsync's vocabulary check is the only thing stopping a typo from
    /// becoming a real grant. Rejected, counted, and not written.</summary>
    [Fact]
    public async Task A_role_outside_the_ats_vocabulary_is_rejected_not_written()
    {
        await SeedSubjectAsync("a@example.com");

        var result = await RoleGrantImporter.ApplyAsync(_db, [Plan("a@example.com", "Wizard")]);

        Assert.Equal((0, 0, 1), (result.Granted, result.AlreadyGranted, result.Rejected));
        Assert.Empty(await GrantedRolesAsync("a@example.com"));
    }

    [Fact]
    public async Task A_subject_with_no_planned_roles_is_a_no_op()
    {
        await SeedSubjectAsync("a@example.com");

        var result = await RoleGrantImporter.ApplyAsync(_db, [Plan("a@example.com")]);

        Assert.Equal((0, 0, 0), (result.Granted, result.AlreadyGranted, result.Rejected));
        Assert.Empty(await _db.RoleGrants.ToListAsync());
    }

    /// <summary>Shouldn't happen (SubjectImporter runs first, keyed by the same
    /// normalized email), but a missing subject must skip rather than throw and
    /// leave the rest of an import half-applied.</summary>
    [Fact]
    public async Task A_plan_whose_subject_does_not_exist_is_skipped_not_thrown()
    {
        var result = await RoleGrantImporter.ApplyAsync(_db, [Plan("ghost@example.com", "Recruiter")]);

        Assert.Equal((0, 0, 0), (result.Granted, result.AlreadyGranted, result.Rejected));
        Assert.Empty(await _db.RoleGrants.ToListAsync());
    }
}
