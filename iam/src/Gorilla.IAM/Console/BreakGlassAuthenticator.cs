using Gorilla.IAM.Auth;
using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Console;

public enum LoginFailureReason
{
    NoSuchSubject,
    WrongPassword,
    SubjectInactive,
    MissingIamAdminGrant,
}

public abstract record LoginResult
{
    public sealed record Success(Guid SubjectId, string Email, string Name) : LoginResult;

    /// <summary>Credentials, active status and the iam:admin grant all check
    /// out, but an admin reset this password (Credential.MustChangePassword)
    /// — spec section 3.2's "no token is issued until the password changes,"
    /// applied to this console's own session. The caller must not grant
    /// dashboard access; only a change-password step.</summary>
    public sealed record MustChangePassword(Guid SubjectId, string Email, string Name) : LoginResult;

    public sealed record Failure(LoginFailureReason Reason) : LoginResult;
}

public enum ChangePasswordResult
{
    Changed,
    WrongCurrentPassword,
    PolicyViolation,
}

/// <summary>
/// Authenticates directly against Subject/Credential/RoleGrant — no
/// dependency on OpenIddict's authorization-code flow at all. That is the
/// point, not an oversight: spec section 3.1 calls this a "break-glass"
/// console needed "before HR is wired up, and when HR is down," and a path
/// that only works when the full OIDC machinery is also working stops being
/// break-glass. It still goes through the exact same
/// CredentialVerifier/rehash-on-verify every other login will eventually
/// use — "minimal dependencies" does not mean "a separate, untested auth
/// path."
/// </summary>
public class BreakGlassAuthenticator(IamDbContext db)
{
    public async Task<LoginResult> AuthenticateAsync(string email, string password, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var subject = await db.Subjects
            .Include(s => s.Credential)
            .Include(s => s.RoleGrants)
            .SingleOrDefaultAsync(s => s.Email == normalizedEmail, ct);

        if (subject is null)
            return new LoginResult.Failure(LoginFailureReason.NoSuchSubject);

        var verifyResult = CredentialVerifier.Verify(subject.Credential, password);
        if (verifyResult == CredentialVerifyResult.Rejected)
            return new LoginResult.Failure(LoginFailureReason.WrongPassword);

        if (verifyResult == CredentialVerifyResult.AcceptedAndRehashed)
            await db.SaveChangesAsync(ct); // persist the rehash before continuing

        if (!subject.IsActive)
            return new LoginResult.Failure(LoginFailureReason.SubjectInactive);

        var isIamAdmin = subject.RoleGrants.Any(g => g.AppKey == IamSelfConsumerApp.AppKey && g.Role == IamSelfConsumerApp.AdminRole);
        if (!isIamAdmin)
            return new LoginResult.Failure(LoginFailureReason.MissingIamAdminGrant);

        // Checked last, deliberately: only reachable once someone has already
        // proven a valid password, an active subject and an iam:admin grant —
        // a non-admin whose credential happens to have this flag set (e.g. a
        // future HR-initiated reset, unrelated to console access) must never
        // learn that from this console; "invalid credentials" is still all
        // they get from the failure branches above.
        if (subject.Credential!.MustChangePassword)
            return new LoginResult.MustChangePassword(subject.Id, subject.Email, subject.Name);

        return new LoginResult.Success(subject.Id, subject.Email, subject.Name);
    }

    /// <summary>Completes a pending admin-initiated reset. Requires
    /// <paramref name="currentPassword"/> — the temporary one the admin set —
    /// re-verified here rather than trusted from the interim session alone,
    /// so a leaked/stale cookie in the "must change password" state cannot
    /// itself be used to set a new password without proving the temporary
    /// one is known. Not for a subject's routine self-service change (no
    /// such feature exists in this console — see the spec's out-of-scope
    /// list, "self-service password recovery, needs email").</summary>
    public async Task<ChangePasswordResult> ChangePasswordAsync(
        Guid subjectId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var subject = await db.Subjects.Include(s => s.Credential).SingleAsync(s => s.Id == subjectId, ct);
        var credential = subject.Credential!;

        if (CredentialVerifier.Verify(credential, currentPassword) == CredentialVerifyResult.Rejected)
            return ChangePasswordResult.WrongCurrentPassword;

        if (PasswordPolicy.Validate(newPassword, currentPassword) is not null)
            return ChangePasswordResult.PolicyViolation;

        credential.Algorithm = CredentialAlgorithm.Bcrypt;
        credential.Hash = BcryptPasswordHasher.Hash(newPassword);
        credential.MustChangePassword = false;
        credential.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ChangePasswordResult.Changed;
    }
}
