using System.Security.Cryptography;

namespace Gorilla.IAM.Auth;

/// <summary>
/// PBKDF2-SHA256 password hashing in the "iterations.saltB64.hashB64" format.
/// Lifted verbatim from Recruitment.Gorilla's <c>Auth/PasswordHasher.cs</c>
/// (spec section 3.4: "RG's PasswordHasher.cs lifts verbatim for the PBKDF2
/// side") so imported RG hashes verify byte-for-byte against the same
/// derivation. Only used to *verify* existing RG hashes — new credentials are
/// always written as bcrypt via <see cref="BcryptPasswordHasher"/>, and
/// <see cref="CredentialVerifier"/> rehashes a successful PBKDF2 verify to
/// bcrypt so the estate converges over time. Do not use this to hash new
/// passwords.
/// </summary>
public static class Pbkdf2PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Present only so RG-side dry-run/import tooling can produce hashes
    /// in the exact format being imported; not used by the running service.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>Verifies a password against an "iterations.saltB64.hashB64" hash.</summary>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
