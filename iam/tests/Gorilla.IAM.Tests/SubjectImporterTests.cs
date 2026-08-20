using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using Gorilla.IAM.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Tests;

public class SubjectImporterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IamDbContext _db;

    public SubjectImporterTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<IamDbContext>().UseSqlite(_connection).Options;
        _db = new IamDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SubjectImportPlan Plan(string email, string name = "Name", bool active = true, string hash = "hash") =>
        new(email, name, active, CredentialAlgorithm.Bcrypt, hash, ImportSource.HrOnly);

    [Fact]
    public async Task First_run_creates_a_subject_and_credential_for_each_planned_person()
    {
        var result = await SubjectImporter.ApplyAsync(_db, [Plan("a@example.com"), Plan("b@example.com")]);

        Assert.Equal((2, 0, 0), (result.Created, result.Updated, result.Unchanged));
        var subject = await _db.Subjects.Include(s => s.Credential).SingleAsync(s => s.Email == "a@example.com");
        Assert.Equal("hash", subject.Credential!.Hash);
        Assert.Equal(CredentialAlgorithm.Bcrypt, subject.Credential.Algorithm);
    }

    [Fact]
    public async Task Running_it_again_with_no_source_changes_touches_nothing()
    {
        await SubjectImporter.ApplyAsync(_db, [Plan("a@example.com")]);

        var result = await SubjectImporter.ApplyAsync(_db, [Plan("a@example.com")]);

        Assert.Equal((0, 0, 1), (result.Created, result.Updated, result.Unchanged));
        Assert.Equal(1, await _db.Subjects.CountAsync());
    }

    /// <summary>The scenario SubjectImporter's docs call out explicitly: during
    /// P1, a changed source-system password must be reflected on re-import.</summary>
    [Fact]
    public async Task A_changed_password_upstream_updates_the_existing_credential_not_a_new_row()
    {
        await SubjectImporter.ApplyAsync(_db, [Plan("a@example.com", hash: "old-hash")]);

        var result = await SubjectImporter.ApplyAsync(_db, [Plan("a@example.com", hash: "new-hash")]);

        Assert.Equal((0, 1, 0), (result.Created, result.Updated, result.Unchanged));
        Assert.Equal(1, await _db.Subjects.CountAsync());
        var subject = await _db.Subjects.Include(s => s.Credential).SingleAsync();
        Assert.Equal("new-hash", subject.Credential!.Hash);
    }

    [Fact]
    public async Task A_changed_active_status_updates_the_subject()
    {
        await SubjectImporter.ApplyAsync(_db, [Plan("a@example.com", active: true)]);

        await SubjectImporter.ApplyAsync(_db, [Plan("a@example.com", active: false)]);

        var subject = await _db.Subjects.SingleAsync();
        Assert.False(subject.IsActive);
    }

    [Fact]
    public async Task A_person_who_disappears_from_the_plan_is_left_alone_not_deleted()
    {
        // The importer only ever adds/updates what it's told about — it does
        // not compute "who's missing this time" and delete them. A one-run
        // gap in source data (a flaky read, a person mid-transfer between
        // systems) must not deactivate or remove anyone.
        await SubjectImporter.ApplyAsync(_db, [Plan("a@example.com"), Plan("b@example.com")]);

        await SubjectImporter.ApplyAsync(_db, [Plan("a@example.com")]);

        Assert.Equal(2, await _db.Subjects.CountAsync());
    }
}
