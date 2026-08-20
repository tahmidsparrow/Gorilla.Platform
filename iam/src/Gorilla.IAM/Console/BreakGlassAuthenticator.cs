using Gorilla.IAM.Auth;
using Gorilla.IAM.Data;
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
    public sealed record Failure(LoginFailureReason Reason) : LoginResult;
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

        return new LoginResult.Success(subject.Id, subject.Email, subject.Name);
    }
}
