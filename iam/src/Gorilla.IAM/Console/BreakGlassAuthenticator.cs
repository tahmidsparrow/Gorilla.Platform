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
}

public abstract record LoginResult
{
    /// <param name="IsAdmin">Whether this subject actually holds the iam:admin
    /// grant — carried, not gated on: any active subject with valid credentials
    /// succeeds here now (see the class doc for why), but the console's own
    /// admin-only pages still require this to be true (ConsoleEndpoints'
    /// RequireRole(IamSelfConsumerApp.AdminRole) group).</param>
    public sealed record Success(Guid SubjectId, string Email, string Name, bool IsAdmin) : LoginResult;

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
/// dependency on OpenIddict's authorization-code flow at all. That was the
/// original point (spec section 3.1's "break-glass" console, needed "before
/// HR is wired up, and when HR is down"), and it still holds: this never
/// depends on OpenIddict's flows being configured or working.
///
/// It is no longer break-glass-admin-only, though. This is also the sign-in
/// every OIDC-participating app's <c>/connect/authorize</c> challenge
/// depends on (see OidcEndpoints.cs) — any active subject with valid
/// credentials succeeds here, not just iam:admin holders. The console's own
/// admin-only pages (subject list, grants) stay gated separately, by
/// ConsoleEndpoints' RequireRole(IamSelfConsumerApp.AdminRole) group; this
/// class only answers "are these credentials valid for an active subject,"
/// same as it always did — it just no longer conflates that with "is this
/// subject an admin." Goes through the exact same
/// CredentialVerifier/rehash-on-verify every login uses — "minimal
/// dependencies" does not mean "a separate, untested auth path."
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

        if (subject.Credential!.MustChangePassword)
            return new LoginResult.MustChangePassword(subject.Id, subject.Email, subject.Name);

        var isIamAdmin = subject.RoleGrants.Any(g => g.AppKey == IamSelfConsumerApp.AppKey && g.Role == IamSelfConsumerApp.AdminRole);
        return new LoginResult.Success(subject.Id, subject.Email, subject.Name, isIamAdmin);
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
