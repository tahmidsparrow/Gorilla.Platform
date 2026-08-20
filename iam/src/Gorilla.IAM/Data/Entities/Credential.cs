namespace Gorilla.IAM.Data.Entities;

/// <summary>
/// One subject's password hash. A 1:1 with <see cref="Subject"/>. For a
/// person who existed in both source systems with different passwords
/// (the one unsolved case in spec section 3.4), the row imported here is
/// HR's — never "accept either and converge."
/// </summary>
public class Credential
{
    public Guid SubjectId { get; set; }
    public Subject Subject { get; set; } = null!;

    public CredentialAlgorithm Algorithm { get; set; }
    public string Hash { get; set; } = string.Empty;

    /// <summary>Set whenever an admin resets this credential (never by the
    /// subject's own password change). Spec section 3.2: "no token is
    /// issued until the password changes" — the break-glass console applies
    /// the same principle to its own session, not just the future OIDC
    /// login page: a true here blocks everything except the change-password
    /// form itself. See BreakGlassLoginGate.</summary>
    public bool MustChangePassword { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
