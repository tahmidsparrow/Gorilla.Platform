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

    private async Task<Subject> SeedAdminAsync(string email = "admin@example.com", string password = "s3cret!", bool active = true)
    {
        var subject = new Subject
        {
            Email = email,
            Name = "Admin",
            IsActive = active,
            Credential = new Credential { Algorithm = CredentialAlgorithm.Bcrypt, Hash = BcryptPasswordHasher.Hash(password) },
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

    /// <summary>The whole point of the console's own access gate — a correct
    /// password alone must not be enough. Spec section 3.1: "gated on a
    /// dedicated iam:admin grant held by one or two people."</summary>
    [Fact]
    public async Task Fails_for_a_correct_password_with_no_iam_admin_grant()
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

        Assert.Equal(LoginFailureReason.MissingIamAdminGrant, Assert.IsType<LoginResult.Failure>(result).Reason);
    }

    /// <summary>A grant for some other app does not confer console access.</summary>
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

        Assert.Equal(LoginFailureReason.MissingIamAdminGrant, Assert.IsType<LoginResult.Failure>(result).Reason);
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
}
