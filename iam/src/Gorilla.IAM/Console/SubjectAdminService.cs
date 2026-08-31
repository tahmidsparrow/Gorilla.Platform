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

public enum CreateSubjectResult
{
    Created,
    EmailAlreadyExists,
    InvalidEmail,
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

    /// <summary>
    /// Creates a person who exists in neither HR nor Recruitment.Gorilla.
    ///
    /// Until now the only way into <c>subjects</c> was the import tool reading the
    /// two apps' own user tables, which is fine only while those apps still own
    /// their logins. Once Recruitment's local login retires there is otherwise
    /// nowhere for a new hire to be created at all — RG's creation is gone, HR's
    /// console is P4, and this service could administer everyone except add anyone.
    ///
    /// Lands the new subject in exactly the state
    /// <see cref="ResetPasswordAsync"/> produces — bcrypt credential,
    /// <c>MustChangePassword</c> set — so first sign-in goes through the
    /// forced-change path that already exists rather than a second one. As with a
    /// reset, no email is sent: handing over the temporary password is the admin's
    /// job, out of band.
    ///
    /// Note this is for people the source systems don't have. If the same email
    /// later turns up in HR or RG, the import matches by email and will overwrite
    /// this credential — deliberate there ("re-running the import is how a changed
    /// password gets reflected"), and worth knowing here.
    /// </summary>
    public async Task<CreateSubjectResult> CreateSubjectAsync(
        string email, string name, string temporaryPassword, CancellationToken ct = default)
    {
        // Normalized before both the duplicate check and the insert — a subject
        // stored in any other shape is one BreakGlassAuthenticator can never find.
        // See SubjectEmail.
        var normalizedEmail = SubjectEmail.Normalize(email);

        if (normalizedEmail.Length == 0 || !normalizedEmail.Contains('@'))
            return CreateSubjectResult.InvalidEmail;

        if (string.IsNullOrWhiteSpace(name))
            return CreateSubjectResult.InvalidEmail;

        if (PasswordPolicy.Validate(temporaryPassword) is not null)
            return CreateSubjectResult.PolicyViolation;

        // Checked rather than left to the unique index: an admin typing an address
        // that already exists should get told so, not a raw DbUpdateException.
        if (await db.Subjects.AnyAsync(s => s.Email == normalizedEmail, ct))
            return CreateSubjectResult.EmailAlreadyExists;

        db.Subjects.Add(new Subject
        {
            Email = normalizedEmail,
            Name = name.Trim(),
            IsActive = true,
            Credential = new Credential
            {
                Algorithm = CredentialAlgorithm.Bcrypt,
                Hash = BcryptPasswordHasher.Hash(temporaryPassword),
                MustChangePassword = true,
            },
        });

        await db.SaveChangesAsync(ct);
        return CreateSubjectResult.Created;
    }
}
