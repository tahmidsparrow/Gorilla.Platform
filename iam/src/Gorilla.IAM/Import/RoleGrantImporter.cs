using Gorilla.IAM.Console;
using Gorilla.IAM.Data;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Import;

/// <param name="Rejected">Grants refused because the role isn't in the app's
/// vocabulary. Never silently ignored — the caller reports it, and the dry-run
/// gate catches it before an apply ever gets here.</param>
public record RoleGrantResult(int Granted, int AlreadyGranted, int Rejected);

/// <summary>
/// Writes each planned subject's RG roles into role_grants as "ats" grants —
/// the piece that makes an imported RG user actually able to sign in. Without
/// it they authenticate at IAM and are then refused by OidcEndpoints' grant
/// check, which requires a grant for the requesting client.
///
/// Runs as a second pass AFTER <see cref="SubjectImporter"/> has saved, so
/// every subject is guaranteed persisted and findable by email.
///
/// Delegates to <see cref="SubjectAdminService.GrantRoleAsync"/> rather than
/// adding RoleGrant rows directly, for two reasons worth preserving:
/// there is no FK from role_grants.Role to consumer_app_roles, so that method's
/// vocabulary check is the ONLY thing standing between a typo and a garbage
/// grant; and it returns AlreadyGranted instead of throwing, which is what
/// makes re-running the import idempotent for free.
///
/// That costs ~3 queries per grant (it saves per call) rather than one batched
/// save. Deliberate: this is a one-shot cutover tool, and the validation is
/// worth more than the round trips. Don't "optimize" it into a bulk insert
/// without carrying the vocabulary check across.
/// </summary>
public static class RoleGrantImporter
{
    /// <summary>The consumer app key RG's roles are granted under. Matches
    /// ConsumerAppSeedData's "ats" entry and OpenIddictClientSeeder's client id —
    /// two different tables that deliberately share this string.</summary>
    public const string AtsAppKey = "ats";

    public static async Task<RoleGrantResult> ApplyAsync(
        IamDbContext db,
        IReadOnlyList<SubjectImportPlan> plans,
        CancellationToken ct = default)
    {
        var admin = new SubjectAdminService(db);
        int granted = 0, alreadyGranted = 0, rejected = 0;

        foreach (var plan in plans)
        {
            var roles = plan.AtsRoles ?? [];
            if (roles.Count == 0)
                continue;

            var subjectId = await db.Subjects
                .Where(s => s.Email == plan.Email)
                .Select(s => (Guid?)s.Id)
                .SingleOrDefaultAsync(ct);

            // Shouldn't happen — SubjectImporter ran first and plans are keyed by
            // the same normalized email — but skipping beats throwing mid-import
            // and leaving grants half-applied.
            if (subjectId is null)
                continue;

            foreach (var role in roles)
            {
                switch (await admin.GrantRoleAsync(subjectId.Value, AtsAppKey, role, grantedBySubjectId: null, ct))
                {
                    case GrantRoleResult.Granted: granted++; break;
                    case GrantRoleResult.AlreadyGranted: alreadyGranted++; break;
                    default: rejected++; break;
                }
            }
        }

        return new RoleGrantResult(granted, alreadyGranted, rejected);
    }
}
