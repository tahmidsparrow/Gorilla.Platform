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

    [Fact]
    public async Task ResetPasswordAsync_sets_a_bcrypt_credential_and_flags_MustChangePassword()
    {
        var subject = await SeedSubjectAsync();

        var result = await _sut.ResetPasswordAsync(subject.Id, "newValidPass2");

        Assert.Equal(ResetPasswordResult.Reset, result);
        var credential = await _db.Credentials.SingleAsync(c => c.SubjectId == subject.Id);
        Assert.Equal(CredentialAlgorithm.Bcrypt, credential.Algorithm);
        Assert.True(credential.MustChangePassword);
        Assert.True(BcryptPasswordHasher.Verify("newValidPass2", credential.Hash));
    }

    /// <summary>A PBKDF2 credential (RG-only person) resets to bcrypt too —
    /// the reset always writes fresh, never preserves the old algorithm.</summary>
    [Fact]
    public async Task ResetPasswordAsync_converts_a_pbkdf2_credential_to_bcrypt()
    {
        var subject = new Subject { Email = "legacy@example.com", Name = "L", IsActive = true };
        subject.Credential = new Credential { SubjectId = subject.Id, Algorithm = CredentialAlgorithm.Pbkdf2Sha256, Hash = "100000.x.y" };
        _db.Subjects.Add(subject);
        await _db.SaveChangesAsync();

        await _sut.ResetPasswordAsync(subject.Id, "newValidPass2");

        var credential = await _db.Credentials.SingleAsync(c => c.SubjectId == subject.Id);
        Assert.Equal(CredentialAlgorithm.Bcrypt, credential.Algorithm);
    }

    [Fact]
    public async Task ResetPasswordAsync_rejects_a_password_that_fails_policy_and_leaves_the_credential_untouched()
    {
        var subject = await SeedSubjectAsync();
        var originalHash = (await _db.Credentials.SingleAsync(c => c.SubjectId == subject.Id)).Hash;

        var result = await _sut.ResetPasswordAsync(subject.Id, "short1");

        Assert.Equal(ResetPasswordResult.PolicyViolation, result);
        Assert.Equal(originalHash, (await _db.Credentials.SingleAsync(c => c.SubjectId == subject.Id)).Hash);
    }

    [Fact]
    public async Task ResetPasswordAsync_throws_for_an_unknown_subject()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _sut.ResetPasswordAsync(Guid.NewGuid(), "newValidPass2"));
    }

    // ----- Creating a person who exists in neither HR nor RG -----

    [Fact]
    public async Task Create_makes_an_active_subject_that_must_change_its_password()
    {
        var result = await _sut.CreateSubjectAsync("new@example.com", "New Person", "Temp@12345");

        Assert.Equal(CreateSubjectResult.Created, result);
        var subject = await _db.Subjects.Include(s => s.Credential).SingleAsync(s => s.Email == "new@example.com");
        Assert.True(subject.IsActive);
        Assert.Equal("New Person", subject.Name);
        Assert.Equal(CredentialAlgorithm.Bcrypt, subject.Credential!.Algorithm);
        Assert.True(subject.Credential.MustChangePassword);
        Assert.True(BcryptPasswordHasher.Verify("Temp@12345", subject.Credential.Hash));
    }

    /// <summary>The main correctness risk in this feature: BreakGlassAuthenticator
    /// looks a subject up by the normalized email, so anything stored in another
    /// shape is an account nobody can ever sign into. MySQL's case-insensitive
    /// collation would hide this locally — SQLite here does not.</summary>
    [Fact]
    public async Task Create_stores_the_email_normalized_even_when_the_form_supplies_mixed_case_and_spaces()
    {
        await _sut.CreateSubjectAsync("  New.Person@Example.COM  ", "New Person", "Temp@12345");

        Assert.True(await _db.Subjects.AnyAsync(s => s.Email == "new.person@example.com"));
    }

    /// <summary>...and the account created that way is genuinely usable — the real
    /// point of normalizing, asserted through the login path rather than trusting
    /// the stored string alone.</summary>
    [Fact]
    public async Task A_subject_created_from_a_mixed_case_email_can_actually_sign_in()
    {
        await _sut.CreateSubjectAsync("New.Person@Example.COM", "New Person", "Temp@12345");

        var result = await new BreakGlassAuthenticator(_db).AuthenticateAsync("new.person@example.com", "Temp@12345");

        // MustChangePassword, not Success — created subjects land in the same state
        // an admin-initiated reset produces, on purpose.
        Assert.IsType<LoginResult.MustChangePassword>(result);
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_email_rather_than_hitting_the_unique_index()
    {
        await _sut.CreateSubjectAsync("dupe@example.com", "First", "Temp@12345");

        var result = await _sut.CreateSubjectAsync("dupe@example.com", "Second", "Temp@12345");

        Assert.Equal(CreateSubjectResult.EmailAlreadyExists, result);
        Assert.Equal(1, await _db.Subjects.CountAsync(s => s.Email == "dupe@example.com"));
    }

    /// <summary>Two addresses differing only by case are the same person, so this
    /// must be caught by the duplicate check and not slip past it.</summary>
    [Fact]
    public async Task Create_rejects_a_duplicate_that_differs_only_by_case()
    {
        await _sut.CreateSubjectAsync("dupe@example.com", "First", "Temp@12345");

        var result = await _sut.CreateSubjectAsync("DUPE@Example.com", "Second", "Temp@12345");

        Assert.Equal(CreateSubjectResult.EmailAlreadyExists, result);
        Assert.Equal(1, await _db.Subjects.CountAsync());
    }

    [Fact]
    public async Task Create_rejects_a_weak_password_and_writes_nothing()
    {
        var result = await _sut.CreateSubjectAsync("weak@example.com", "Weak", "short");

        Assert.Equal(CreateSubjectResult.PolicyViolation, result);
        Assert.Empty(await _db.Subjects.ToListAsync());
    }

    [Theory]
    [InlineData("", "Name")]
    [InlineData("not-an-email", "Name")]
    [InlineData("ok@example.com", "")]
    [InlineData("ok@example.com", "   ")]
    public async Task Create_rejects_a_missing_or_malformed_email_or_name(string email, string name)
    {
        var result = await _sut.CreateSubjectAsync(email, name, "Temp@12345");

        Assert.Equal(CreateSubjectResult.InvalidEmail, result);
        Assert.Empty(await _db.Subjects.ToListAsync());
    }

    /// <summary>Creation deliberately grants nothing — app roles come from the
    /// dashboard's own per-row control afterwards.</summary>
    [Fact]
    public async Task Create_grants_no_roles()
    {
        await _sut.CreateSubjectAsync("new@example.com", "New Person", "Temp@12345");

        Assert.Empty(await _db.RoleGrants.ToListAsync());
    }
}
