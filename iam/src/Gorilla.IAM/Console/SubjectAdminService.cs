using Gorilla.IAM.Auth;
using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Console;

public record SubjectSummary(Guid Id, string Email, string Name, bool IsActive, IReadOnlyList<(string AppKey, string Role)> Grants);

public enum GrantRoleResult
{
    Granted,
    AlreadyGranted,
    UnknownAppOrRole,
}

public enum ResetPasswordResult
{
    Reset,
    PolicyViolation,
}

/// <summary>
/// The break-glass console's actual operations — spec section 3.1's
/// deliberately spartan list: "list subjects, toggle grants, deactivate."
/// Kept separate from the HTTP endpoints (thin, per this estate's own
/// routers -> services -> models convention — see CLAUDE.md) so this stays
/// testable without a running web host.
/// </summary>
public class SubjectAdminService(IamDbContext db)
{
    public async Task<IReadOnlyList<SubjectSummary>> ListSubjectsAsync(CancellationToken ct = default)
    {
        var subjects = await db.Subjects
            .Include(s => s.RoleGrants)
            .OrderBy(s => s.Email)
            .ToListAsync(ct);

        return subjects
            .Select(s => new SubjectSummary(
                s.Id, s.Email, s.Name, s.IsActive,
                s.RoleGrants.Select(g => (g.AppKey, g.Role)).OrderBy(g => g.AppKey).ToList()))
            .ToList();
    }

    /// <summary>Every ConsumerApp and its role vocabulary — populates the
    /// grant form; a grant can only be issued for a role that actually exists.</summary>
    public async Task<IReadOnlyList<(string AppKey, string[] Roles)>> ListGrantableRolesAsync(CancellationToken ct = default)
    {
        var apps = await db.ConsumerApps.Include(a => a.Roles).OrderBy(a => a.Key).ToListAsync(ct);
        return apps.Select(a => (a.Key, a.Roles.Select(r => r.Role).OrderBy(r => r).ToArray())).ToList();
    }

    /// <summary>Deactivate/reactivate. The spec's own wording only says
    /// "deactivate," but a break-glass tool that can lock someone out and
    /// never let them back in without a direct database edit is not
    /// actually a safety net — reactivation is the same operation with the
    /// flag flipped, so it costs nothing to support both.</summary>
    public async Task SetActiveAsync(Guid subjectId, bool active, CancellationToken ct = default)
    {
        var subject = await db.Subjects.FindAsync([subjectId], ct)
            ?? throw new KeyNotFoundException($"No subject {subjectId}.");
        subject.IsActive = active;
        subject.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<GrantRoleResult> GrantRoleAsync(Guid subjectId, string appKey, string role, Guid? grantedBySubjectId, CancellationToken ct = default)
    {
        var roleExists = await db.ConsumerAppRoles.AnyAsync(r => r.AppKey == appKey && r.Role == role, ct);
        if (!roleExists)
            return GrantRoleResult.UnknownAppOrRole;

        var alreadyGranted = await db.RoleGrants
            .AnyAsync(g => g.SubjectId == subjectId && g.AppKey == appKey && g.Role == role, ct);
        if (alreadyGranted)
            return GrantRoleResult.AlreadyGranted;

        db.RoleGrants.Add(new RoleGrant
        {
            SubjectId = subjectId,
            AppKey = appKey,
            Role = role,
            GrantedBySubjectId = grantedBySubjectId,
        });
        await db.SaveChangesAsync(ct);
        return GrantRoleResult.Granted;
    }

    public async Task RevokeRoleAsync(Guid subjectId, string appKey, string role, CancellationToken ct = default)
    {
        var grant = await db.RoleGrants
            .SingleOrDefaultAsync(g => g.SubjectId == subjectId && g.AppKey == appKey && g.Role == role, ct);
        if (grant is null) return; // already gone — revoke is idempotent

        db.RoleGrants.Remove(grant);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Admin-initiated reset — the P2 blocker: after Recruitment cuts over
    /// (spec section 3.2), a person locked out because their old RG password
    /// stopped working and they don't know their HR one has no self-service
    /// recovery (out of scope until P6, needs email). This is the only way
    /// back in until then. Sets a temporary password chosen by the admin and
    /// flags MustChangePassword — the subject cannot use the console for
    /// anything except changing it on next login (BreakGlassAuthenticator).
    /// No email is sent; communicating the temporary password is the admin's
    /// job, out of band. There is no currentPassword to compare against
    /// here — the admin does not know it either — so PasswordPolicy is
    /// checked without that argument, unlike the subject's own later change.
    /// </summary>
    public async Task<ResetPasswordResult> ResetPasswordAsync(Guid subjectId, string newPassword, CancellationToken ct = default)
    {
        if (PasswordPolicy.Validate(newPassword) is not null)
            return ResetPasswordResult.PolicyViolation;

        var subject = await db.Subjects.Include(s => s.Credential).SingleOrDefaultAsync(s => s.Id == subjectId, ct)
            ?? throw new KeyNotFoundException($"No subject {subjectId}.");

        if (subject.Credential is null)
        {
            subject.Credential = new Credential { SubjectId = subjectId };
            db.Credentials.Add(subject.Credential);
        }

        subject.Credential.Algorithm = CredentialAlgorithm.Bcrypt;
        subject.Credential.Hash = BcryptPasswordHasher.Hash(newPassword);
        subject.Credential.MustChangePassword = true;
        subject.Credential.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ResetPasswordResult.Reset;
    }
}
