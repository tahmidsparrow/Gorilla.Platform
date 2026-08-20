namespace Gorilla.IAM.Data.Entities;

/// <summary>
/// One role held by one subject in one consumer app — the authoritative grant
/// (spec section 3.1). <c>(SubjectId, AppKey, Role)</c> is unique; HR and RG
/// each keep a read-through projection of these, fed by webhook/JIT, and never
/// write here directly except through <see cref="ConsumerApp"/>'s admin API.
/// </summary>
public class RoleGrant
{
    public int Id { get; set; }

    public Guid SubjectId { get; set; }
    public Subject Subject { get; set; } = null!;

    /// <summary>Foreign key to <see cref="ConsumerApp.Key"/> (e.g. "hr", "ats").</summary>
    public string AppKey { get; set; } = string.Empty;
    public ConsumerApp App { get; set; } = null!;

    /// <summary>Must be one of <see cref="ConsumerApp"/>'s <see cref="ConsumerApp.Roles"/>
    /// for this app — validated at write time, not by a DB constraint, since the
    /// vocabulary is per-app data, not a fixed enum.</summary>
    public string Role { get; set; } = string.Empty;

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The subject who made this grant — null for seed/bootstrap data.</summary>
    public Guid? GrantedBySubjectId { get; set; }
}
