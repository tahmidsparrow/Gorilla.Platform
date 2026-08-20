namespace Gorilla.IAM.Auth;

/// <summary>
/// Bcrypt hashing via BCrypt.Net-Next (spec section 3.4: "BCrypt.Net-Next
/// covers HR's hashes"). GorillaHR's own hashes verify directly against this;
/// this is also the algorithm every credential converges to — new credentials
/// and PBKDF2 rehash-on-verify both write bcrypt, never PBKDF2.
/// </summary>
public static class BcryptPasswordHasher
{
    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, stored);
        }
        catch (Exception ex) when (ex is BCrypt.Net.SaltParseException or ArgumentException)
        {
            // Malformed/foreign hash — treat as a verification failure, not a
            // crash. BCrypt.Net-Next throws SaltParseException for a
            // recognizably-wrong-but-present salt and plain ArgumentException
            // for missing/empty input; both mean "not a valid bcrypt hash".
            return false;
        }
    }
}
