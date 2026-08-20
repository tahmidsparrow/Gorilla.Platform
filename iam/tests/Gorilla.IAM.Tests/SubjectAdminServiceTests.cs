using Gorilla.IAM.Auth;
using Gorilla.IAM.Console;
using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Tests;

public class SubjectAdminServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IamDbContext _db;
    private readonly SubjectAdminService _sut;

    public SubjectAdminServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<IamDbContext>().UseSqlite(_connection).Options;
        _db = new IamDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new SubjectAdminService(_db);

        _db.ConsumerApps.Add(new ConsumerApp
        {
            Key = "hr",
            Name = "GorillaHR",
            Roles = [new ConsumerAppRole { AppKey = "hr", Role = "Employee" }, new ConsumerAppRole { AppKey = "hr", Role = "Admin" }],
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Subject> SeedSubjectAsync(string email = "a@example.com", bool active = true)
    {
        var subject = new Subject
        {
            Email = email,
            Name = "A",
            IsActive = active,
            Credential = new Credential { Algorithm = CredentialAlgorithm.Bcrypt, Hash = BcryptPasswordHasher.Hash("x") },
        };
        _db.Subjects.Add(subject);
        await _db.SaveChangesAsync();
        return subject;
    }

    [Fact]
    public async Task Lists_subjects_with_their_grants()
    {
        var subject = await SeedSubjectAsync();
        await _sut.GrantRoleAsync(subject.Id, "hr", "Employee", grantedBySubjectId: null);

        var list = await _sut.ListSubjectsAsync();

        var summary = Assert.Single(list);
        Assert.Equal("a@example.com", summary.Email);
        Assert.Equal(("hr", "Employee"), Assert.Single(summary.Grants));
    }

    [Fact]
    public async Task ListGrantableRoles_reflects_the_seeded_vocabulary()
    {
        var roles = await _sut.ListGrantableRolesAsync();

        var hr = Assert.Single(roles);
        Assert.Equal("hr", hr.AppKey);
        Assert.Equal(["Admin", "Employee"], hr.Roles);
    }

    [Fact]
    public async Task GrantRoleAsync_grants_a_role_that_exists_in_the_vocabulary()
    {
        var subject = await SeedSubjectAsync();

        var result = await _sut.GrantRoleAsync(subject.Id, "hr", "Employee", grantedBySubjectId: null);

        Assert.Equal(GrantRoleResult.Granted, result);
        Assert.True(await _db.RoleGrants.AnyAsync(g => g.SubjectId == subject.Id && g.Role == "Employee"));
    }

    /// <summary>A grant can only target a role that's actually in a
    /// ConsumerApp's vocabulary — this is the validation spec section 3.1
    /// describes ("Role vocabulary per app | Identity consumer_apps table |
    /// Validation"), enforced here rather than left to a caller.</summary>
    [Fact]
    public async Task GrantRoleAsync_refuses_a_role_that_does_not_exist_for_that_app()
    {
        var subject = await SeedSubjectAsync();

        var result = await _sut.GrantRoleAsync(subject.Id, "hr", "NotARealRole", grantedBySubjectId: null);

        Assert.Equal(GrantRoleResult.UnknownAppOrRole, result);
        Assert.False(await _db.RoleGrants.AnyAsync(g => g.SubjectId == subject.Id));
    }

    [Fact]
    public async Task GrantRoleAsync_refuses_an_app_that_does_not_exist_at_all()
    {
        var subject = await SeedSubjectAsync();

        var result = await _sut.GrantRoleAsync(subject.Id, "nonexistent-app", "Anything", grantedBySubjectId: null);

        Assert.Equal(GrantRoleResult.UnknownAppOrRole, result);
    }

    [Fact]
    public async Task GrantRoleAsync_reports_AlreadyGranted_instead_of_duplicating()
    {
        var subject = await SeedSubjectAsync();
        await _sut.GrantRoleAsync(subject.Id, "hr", "Employee", grantedBySubjectId: null);

        var result = await _sut.GrantRoleAsync(subject.Id, "hr", "Employee", grantedBySubjectId: null);

        Assert.Equal(GrantRoleResult.AlreadyGranted, result);
        Assert.Equal(1, await _db.RoleGrants.CountAsync());
    }

    [Fact]
    public async Task RevokeRoleAsync_removes_an_existing_grant()
    {
        var subject = await SeedSubjectAsync();
        await _sut.GrantRoleAsync(subject.Id, "hr", "Employee", grantedBySubjectId: null);

        await _sut.RevokeRoleAsync(subject.Id, "hr", "Employee");

        Assert.False(await _db.RoleGrants.AnyAsync());
    }

    [Fact]
    public async Task RevokeRoleAsync_on_a_grant_that_does_not_exist_is_a_no_op_not_an_error()
    {
        var subject = await SeedSubjectAsync();

        await _sut.RevokeRoleAsync(subject.Id, "hr", "Employee"); // does not throw

        Assert.False(await _db.RoleGrants.AnyAsync());
    }

    [Fact]
    public async Task SetActiveAsync_can_deactivate_and_reactivate()
    {
        var subject = await SeedSubjectAsync(active: true);

        await _sut.SetActiveAsync(subject.Id, false);
        Assert.False((await _db.Subjects.FindAsync(subject.Id))!.IsActive);

        await _sut.SetActiveAsync(subject.Id, true);
        Assert.True((await _db.Subjects.FindAsync(subject.Id))!.IsActive);
    }

    [Fact]
    public async Task SetActiveAsync_throws_for_an_unknown_subject()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _sut.SetActiveAsync(Guid.NewGuid(), false));
    }
}
