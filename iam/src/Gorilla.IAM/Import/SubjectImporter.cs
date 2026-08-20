using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Import;

public record ImportResult(int Created, int Updated, int Unchanged);

/// <summary>
/// Writes a <see cref="SubjectImportPlan"/> list to the database — the only
/// piece of the import pipeline that touches gorilla_iam at all. Deliberately
/// separate from <see cref="ImportPlanner"/> (pure) and dry-run verification
/// (also no DB writes), so a caller can plan and dry-run-verify freely and
/// only reach this class once actually applying.
///
/// Unlike <c>ConsumerAppSeeder</c>'s insert-only-if-missing, this
/// <b>updates</b> an existing subject's credential on every run. That's
/// deliberate, not an oversight: during P1, before cutover, HR and RG remain
/// the source of truth, and re-running the import is how a changed password
/// or a newly onboarded person gets reflected. (Once P3 cuts HR/RG over to
/// validating IAM's tokens, this import stops running against live
/// credentials — a person who changes their password after that point
/// changes it in IAM directly, not through re-import.)
/// </summary>
public static class SubjectImporter
{
    public static async Task<ImportResult> ApplyAsync(
        IamDbContext db,
        IReadOnlyList<SubjectImportPlan> plans,
        CancellationToken ct = default)
    {
        int created = 0, updated = 0, unchanged = 0;

        foreach (var plan in plans)
        {
            var subject = await db.Subjects
                .Include(s => s.Credential)
                .SingleOrDefaultAsync(s => s.Email == plan.Email, ct);

            if (subject is null)
            {
                db.Subjects.Add(new Subject
                {
                    Email = plan.Email,
                    Name = plan.Name,
                    IsActive = plan.Active,
                    Credential = new Credential
                    {
                        Algorithm = plan.Algorithm,
                        Hash = plan.PasswordHash,
                    },
                });
                created++;
                continue;
            }

            var changed = subject.Name != plan.Name
                || subject.IsActive != plan.Active
                || subject.Credential?.Algorithm != plan.Algorithm
                || subject.Credential?.Hash != plan.PasswordHash;

            if (!changed)
            {
                unchanged++;
                continue;
            }

            subject.Name = plan.Name;
            subject.IsActive = plan.Active;
            subject.UpdatedAt = DateTime.UtcNow;

            if (subject.Credential is null)
            {
                subject.Credential = new Credential { Algorithm = plan.Algorithm, Hash = plan.PasswordHash };
            }
            else
            {
                subject.Credential.Algorithm = plan.Algorithm;
                subject.Credential.Hash = plan.PasswordHash;
                subject.Credential.UpdatedAt = DateTime.UtcNow;
            }

            updated++;
        }

        await db.SaveChangesAsync(ct);
        return new ImportResult(created, updated, unchanged);
    }
}
