using Gorilla.IAM.Data.Entities;

namespace Gorilla.IAM.Auth;

public enum CredentialVerifyResult
{
    /// <summary>Wrong password, or no credential row at all.</summary>
    Rejected,

    /// <summary>Correct password; <see cref="Credential"/> was not modified.</summary>
    Accepted,

    /// <summary>Correct password, verified via <see cref="CredentialAlgorithm.Pbkdf2Sha256"/>;
    /// the caller must persist the bcrypt rehash written onto the credential.</summary>
    AcceptedAndRehashed,
}

/// <summary>
/// Dispatches to the algorithm recorded on the credential and, per spec
/// section 3.4 ("a dispatching verifier plus rehash-on-verify means no resets
/// and self-healing convergence"), rewrites a successful PBKDF2 verification
/// as bcrypt in memory. Callers must save the entity when the result is
/// <see cref="CredentialVerifyResult.AcceptedAndRehashed"/> — this type does
/// not touch the database itself, so it stays testable without one.
/// </summary>
public static class CredentialVerifier
{
    public static CredentialVerifyResult Verify(Credential? credential, string password)
    {
        if (credential is null) return CredentialVerifyResult.Rejected;

        switch (credential.Algorithm)
        {
            case CredentialAlgorithm.Bcrypt:
                return BcryptPasswordHasher.Verify(password, credential.Hash)
                    ? CredentialVerifyResult.Accepted
                    : CredentialVerifyResult.Rejected;

            case CredentialAlgorithm.Pbkdf2Sha256:
                if (!Pbkdf2PasswordHasher.Verify(password, credential.Hash))
                    return CredentialVerifyResult.Rejected;

                credential.Algorithm = CredentialAlgorithm.Bcrypt;
                credential.Hash = BcryptPasswordHasher.Hash(password);
                credential.UpdatedAt = DateTime.UtcNow;
                return CredentialVerifyResult.AcceptedAndRehashed;

            default:
                return CredentialVerifyResult.Rejected;
        }
    }
}
