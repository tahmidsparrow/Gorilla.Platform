using System.Text;

namespace Gorilla.IAM.Auth;

/// <summary>
/// Password policy, ported from GorillaHR's backend/app/core/security.py —
/// copied, not referenced, the same "copied, not referenced" choice spec
/// section 2 makes for RG's PasswordHasher.cs, for the same reason: no
/// cross-repo build dependency across the estate. Pinned source: at least 8
/// characters, at most 72 bytes (bcrypt's own hard limit — silently
/// truncates beyond it otherwise), at least one letter and one digit.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxBytes = 72;

    /// <summary>Returns a human-readable reason the password is unacceptable,
    /// or null if it passes. <paramref name="currentPassword"/> is optional —
    /// pass it only when the caller already knows the plaintext current
    /// password (a self-service change) to reject "changing" to the same
    /// value; an admin-initiated reset has no current plaintext to compare
    /// against and should pass null.</summary>
    public static string? Validate(string newPassword, string? currentPassword = null)
    {
        if (newPassword.Length < MinLength)
            return $"Password must be at least {MinLength} characters long.";
        if (Encoding.UTF8.GetByteCount(newPassword) > MaxBytes)
            return $"Password must be at most {MaxBytes} bytes long.";
        if (!newPassword.Any(char.IsLetter))
            return "Password must contain at least one letter.";
        if (!newPassword.Any(char.IsDigit))
            return "Password must contain at least one digit.";
        if (currentPassword is not null && newPassword == currentPassword)
            return "New password must differ from the current password.";
        return null;
    }
}
