using Gorilla.IAM.Auth;
using Gorilla.IAM.Console;
using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Tests;

public class BreakGlassAuthenticatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IamDbContext _db;
    private readonly BreakGlassAuthenticator _sut;

    public BreakGlassAuthenticatorTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<IamDbContext>().UseSqlite(_connection).Options;
        _db = new IamDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new BreakGlassAuthenticator(_db);

        // RoleGrant.AppKey FKs to ConsumerApp.Key — every test that grants a
        // role needs the app (and, for FK purposes, at least the app row)
        // seeded first, matching the real referential integrity IamDbContext
        // defines, not a simplified test-only shortcut.
        _db.ConsumerApps.AddRange(
            new ConsumerApp { Key = "iam", Name = "Gorilla.IAM", Roles = [new ConsumerAppRole { AppKey = "iam", Role = "admin" }] },
            new ConsumerApp { Key = "hr", Name = "GorillaHR", Roles = [new ConsumerAppRole { AppKey = "hr", Role = "Admin" }] });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Subject> SeedAdminAsync(
        string email = "admin@example.com", string password = "s3cret!", bool active = true, bool mustChangePassword = false)
    {
        var subject = new Subject
        {
            Email = email,
            Name = "Admin",
            IsActive = active,
            Credential = new Credential
            {
                Algorithm = CredentialAlgorithm.Bcrypt,
                Hash = BcryptPasswordHasher.Hash(password),
                MustChangePassword = mustChangePassword,
            },
        };
        _db.Subjects.Add(subject);
        _db.RoleGrants.Add(new RoleGrant { SubjectId = subject.Id, AppKey = "iam", Role = "admin" });
        await _db.SaveChangesAsync();
        return subject;
    }

    [Fact]
    public async Task Succeeds_for_a_correct_password_and_an_iam_admin_grant()
    {
        await SeedAdminAsync(password: "s3cret!");

        var result = await _sut.AuthenticateAsync("admin@example.com", "s3cret!");

        var success = Assert.IsType<LoginResult.Success>(result);
        Assert.Equal("admin@example.com", success.Email);
        Assert.True(success.IsAdmin);
    }

    [Fact]
    public async Task Fails_for_an_email_that_does_not_exist()
    {
        var result = await _sut.AuthenticateAsync("nobody@example.com", "anything");

        Assert.Equal(LoginFailureReason.NoSuchSubject, Assert.IsType<LoginResult.Failure>(result).Reason);
    }

    [Fact]
    public async Task Fails_for_the_wrong_password()
    {
        await SeedAdminAsync(password: "s3cret!");

        var result = await _sut.AuthenticateAsync("admin@example.com", "wrong");

        Assert.Equal(LoginFailureReason.WrongPassword, Assert.IsType<LoginResult.Failure>(result).Reason);
    }

    [Fact]
    public async Task Fails_for_a_deactivated_subject_even_with_the_correct_password()
    {
        await SeedAdminAsync(password: "s3cret!", active: false);

        var result = await _sut.AuthenticateAsync("admin@example.com", "s3cret!");

        Assert.Equal(LoginFailureReason.SubjectInactive, Assert.IsType<LoginResult.Failure>(result).Reason);
    }

    /// <summary>The gate moved: a correct password without an iam:admin grant now
    /// succeeds (this class is the shared sign-in for every OIDC app, not just the
    /// console — see its class doc), but IsAdmin correctly comes back false. The
    /// console's OWN admin-only pages stay gated separately, by ConsoleEndpoints'
    /// RequireRole(admin) group — not exercised by this class at all.</summary>
    [Fact]
    public async Task Succeeds_for_a_correct_password_with_no_iam_admin_grant_but_IsAdmin_is_false()
    {
        var subject = new Subject
        {
            Email = "notadmin@example.com",
            Name = "Not Admin",
            IsActive = true,
            Credential = new Credential { Algorithm = CredentialAlgorithm.Bcrypt, Hash = BcryptPasswordHasher.Hash("s3cret!") },
        };
        _db.Subjects.Add(subject);
        await _db.SaveChangesAsync();

        var result = await _sut.AuthenticateAsync("notadmin@example.com", "s3cret!");

        var success = Assert.IsType<LoginResult.Success>(result);
        Assert.False(success.IsAdmin);
    }

    /// <summary>A grant for some other app succeeds here (this class only checks
    /// credentials, not app access) but still doesn't confer IsAdmin.</summary>
    [Fact]
    public async Task An_hr_admin_grant_does_not_satisfy_the_iam_admin_check()
    {
        var subject = new Subject
        {
            Email = "hradmin@example.com",
            Name = "HR Admin",
            IsActive = true,
            Credential = new Credential { Algorithm = CredentialAlgorithm.Bcrypt, Hash = BcryptPasswordHasher.Hash("s3cret!") },
        };
        _db.Subjects.Add(subject);
        _db.RoleGrants.Add(new RoleGrant { SubjectId = subject.Id, AppKey = "hr", Role = "Admin" });
        await _db.SaveChangesAsync();

        var result = await _sut.AuthenticateAsync("hradmin@example.com", "s3cret!");

        var success = Assert.IsType<LoginResult.Success>(result);
        Assert.False(success.IsAdmin);
    }

    /// <summary>The break-glass login must exercise the exact same
    /// rehash-on-verify path as everything else — a PBKDF2 credential
    /// verifies and gets rehashed to bcrypt, persisted.</summary>
    [Fact]
    public async Task A_pbkdf2_credential_verifies_and_is_rehashed_to_bcrypt_on_login()
    {
        var subject = new Subject
        {
            Email = "legacy@example.com",
            Name = "Legacy Admin",
            IsActive = true,
            Credential = new Credential { Algorithm = CredentialAlgorithm.Pbkdf2Sha256, Hash = Pbkdf2PasswordHasher.Hash("s3cret!") },
        };
        _db.Subjects.Add(subject);
        _db.RoleGrants.Add(new RoleGrant { SubjectId = subject.Id, AppKey = "iam", Role = "admin" });
        await _db.SaveChangesAsync();

        var result = await _sut.AuthenticateAsync("legacy@example.com", "s3cret!");

        Assert.IsType<LoginResult.Success>(result);
        var reloaded = await _db.Credentials.SingleAsync(c => c.SubjectId == subject.Id);
        Assert.Equal(CredentialAlgorithm.Bcrypt, reloaded.Algorithm);
    }

    [Fact]
    public async Task Email_matching_is_case_and_whitespace_insensitive()
    {
        await SeedAdminAsync(email: "admin@example.com", password: "s3cret!");

        var result = await _sut.AuthenticateAsync(" Admin@Example.com ", "s3cret!");

        Assert.IsType<LoginResult.Success>(result);
    }

    /// <summary>The admin-initiated-reset feature's whole point: correct
    /// credentials + a valid admin grant still do not reach the dashboard
    /// while a reset is pending.</summary>
    [Fact]
    public async Task Returns_MustChangePassword_instead_of_Success_when_the_credential_is_flagged()
    {
        await SeedAdminAsync(password: "temp-pass1", mustChangePassword: true);

        var result = await _sut.AuthenticateAsync("admin@example.com", "temp-pass1");

        Assert.IsType<LoginResult.MustChangePassword>(result);
    }

    /// <summary>MustChangePassword is checked before the admin-grant lookup (spec
    /// 3.2: no token/session until the password changes) — applies to a non-admin
    /// exactly the same as an admin, now that both can reach Success at all.</summary>
    [Fact]
    public async Task A_non_admin_with_a_flagged_credential_gets_MustChangePassword_not_Success()
    {
        var subject = new Subject
        {
            Email = "notadmin@example.com",
            Name = "Not Admin",
            IsActive = true,
            Credential = new Credential
            {
                Algorithm = CredentialAlgorithm.Bcrypt,
                Hash = BcryptPasswordHasher.Hash("s3cret!"),
                MustChangePassword = true,
            },
        };
        _db.Subjects.Add(subject);
        await _db.SaveChangesAsync();

        var result = await _sut.AuthenticateAsync("notadmin@example.com", "s3cret!");

        Assert.IsType<LoginResult.MustChangePassword>(result);
    }

    [Fact]
    public async Task ChangePasswordAsync_succeeds_with_the_correct_current_password_and_a_valid_new_one()
    {
        var subject = await SeedAdminAsync(password: "temp-pass1", mustChangePassword: true);

        var result = await _sut.ChangePasswordAsync(subject.Id, "temp-pass1", "newValidPass2");

        Assert.Equal(ChangePasswordResult.Changed, result);
        var reloaded = await _db.Credentials.SingleAsync(c => c.SubjectId == subject.Id);
        Assert.False(reloaded.MustChangePassword);
        Assert.True(BcryptPasswordHasher.Verify("newValidPass2", reloaded.Hash));
    }

    [Fact]
    public async Task ChangePasswordAsync_rejects_the_wrong_current_password()
    {
        var subject = await SeedAdminAsync(password: "temp-pass1", mustChangePassword: true);

        var result = await _sut.ChangePasswordAsync(subject.Id, "wrong-current", "newValidPass2");

        Assert.Equal(ChangePasswordResult.WrongCurrentPassword, result);
        Assert.True((await _db.Credentials.SingleAsync(c => c.SubjectId == subject.Id)).MustChangePassword);
    }

    [Fact]
    public async Task ChangePasswordAsync_rejects_a_new_password_that_fails_policy()
    {
        var subject = await SeedAdminAsync(password: "temp-pass1", mustChangePassword: true);

        var result = await _sut.ChangePasswordAsync(subject.Id, "temp-pass1", "short1");

        Assert.Equal(ChangePasswordResult.PolicyViolation, result);
        Assert.True((await _db.Credentials.SingleAsync(c => c.SubjectId == subject.Id)).MustChangePassword);
    }

    [Fact]
    public async Task ChangePasswordAsync_rejects_reusing_the_current_password_as_the_new_one()
    {
        var subject = await SeedAdminAsync(password: "temp-pass1", mustChangePassword: true);

        var result = await _sut.ChangePasswordAsync(subject.Id, "temp-pass1", "temp-pass1");

        Assert.Equal(ChangePasswordResult.PolicyViolation, result);
    }

    /// <summary>After a successful change, the new password works and
    /// MustChangePassword no longer gates login.</summary>
    [Fact]
    public async Task Can_log_in_normally_after_completing_the_change()
    {
        var subject = await SeedAdminAsync(password: "temp-pass1", mustChangePassword: true);
        await _sut.ChangePasswordAsync(subject.Id, "temp-pass1", "newValidPass2");

        var result = await _sut.AuthenticateAsync("admin@example.com", "newValidPass2");

        Assert.IsType<LoginResult.Success>(result);
    }
}
