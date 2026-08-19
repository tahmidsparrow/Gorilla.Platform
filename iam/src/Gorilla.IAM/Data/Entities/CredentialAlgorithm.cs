namespace Gorilla.IAM.Data.Entities;

/// <summary>
/// The hash algorithm a <see cref="Credential"/> was stored with. Both apps'
/// hashes are imported verbatim (spec section 3.4) rather than forcing a
/// reset; <see cref="Auth.CredentialVerifier"/> dispatches on this and
/// rehashes on successful verify so the estate converges on bcrypt over time.
/// </summary>
public enum CredentialAlgorithm
{
    /// <summary>GorillaHR's hashes (passlib/bcrypt).</summary>
    Bcrypt = 0,

    /// <summary>Recruitment.Gorilla's hashes ("iterations.saltB64.hashB64").</summary>
    Pbkdf2Sha256 = 1,
}
