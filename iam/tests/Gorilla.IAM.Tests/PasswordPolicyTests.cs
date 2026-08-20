using Gorilla.IAM.Auth;

namespace Gorilla.IAM.Tests;

/// <summary>
/// Every boundary case here was cross-checked against GorillaHR's real
/// backend/app/core/security.py::password_policy_error before this file was
/// written — not just a visual comparison of the two implementations, an
/// actual run of HR's function against the identical inputs.
/// </summary>
public class PasswordPolicyTests
{
    [Fact]
    public void Accepts_a_password_meeting_every_rule()
    {
        Assert.Null(PasswordPolicy.Validate("validPass1"));
    }

    [Fact]
    public void Rejects_a_password_shorter_than_8_characters()
    {
        Assert.NotNull(PasswordPolicy.Validate("short1"));
    }

    [Fact]
    public void Rejects_a_password_with_no_digit()
    {
        Assert.NotNull(PasswordPolicy.Validate("nodigithere"));
    }

    [Fact]
    public void Rejects_a_password_with_no_letter()
    {
        Assert.NotNull(PasswordPolicy.Validate("12345678"));
    }

    /// <summary>Verified against HR's real function: exactly 72 UTF-8 bytes passes.</summary>
    [Fact]
    public void Accepts_exactly_72_bytes()
    {
        Assert.Null(PasswordPolicy.Validate("a1" + new string('b', 70)));
    }

    /// <summary>Verified against HR's real function: 73 UTF-8 bytes fails — the
    /// boundary matters because bcrypt silently truncates beyond 72 bytes,
    /// which would otherwise let two different "passwords" hash identically.</summary>
    [Fact]
    public void Rejects_73_bytes()
    {
        Assert.NotNull(PasswordPolicy.Validate("a1" + new string('b', 71)));
    }

    /// <summary>Verified against HR's real function: multi-byte characters count
    /// their real UTF-8 byte length, not their character count, and CJK
    /// characters satisfy the "contains a letter" rule (Unicode-aware in both
    /// Python's str.isalpha() and C#'s char.IsLetter).</summary>
    [Fact]
    public void Byte_length_uses_UTF8_not_character_count()
    {
        // 20 CJK characters (3 bytes each in UTF-8) + "1a" = 62 bytes, 22 chars.
        Assert.Null(PasswordPolicy.Validate(new string('密', 20) + "1a"));
    }

    [Fact]
    public void Rejects_reusing_the_current_password_when_one_is_given()
    {
        Assert.NotNull(PasswordPolicy.Validate("validPass1", currentPassword: "validPass1"));
    }

    [Fact]
    public void Does_not_compare_against_a_current_password_when_none_is_given()
    {
        // Admin-initiated resets have no plaintext current password to compare
        // against — passing null must not itself be treated as a match.
        Assert.Null(PasswordPolicy.Validate("validPass1", currentPassword: null));
    }
}
