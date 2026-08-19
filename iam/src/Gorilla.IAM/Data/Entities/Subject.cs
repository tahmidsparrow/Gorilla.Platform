namespace Gorilla.IAM.Data.Entities;

/// <summary>
/// A person known to the estate. The UUID <see cref="Id"/> is the
/// <c>sub</c> claim minted into every token — the inter-service identifier.
/// Consumer apps keep their own local integer PKs and add a shadow
/// <c>iam_subject</c> column pointing back here; nothing is repointed
/// (spec section 3.3).
/// </summary>
public class Subject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>A deactivated subject cannot authenticate or refresh; grants and
    /// history are retained.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Credential? Credential { get; set; }
    public ICollection<RoleGrant> RoleGrants { get; set; } = [];
}
