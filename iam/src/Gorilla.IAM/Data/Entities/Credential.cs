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

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
