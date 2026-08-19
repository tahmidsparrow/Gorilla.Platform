namespace Gorilla.IAM.Data.Entities;

/// <summary>
/// A pending fan-out event — e.g. a role-grant change that HR and RG each
/// need to upsert into their own read-through projection (the worked example
/// in spec section 3.1: "webhook fan-out"). Written in the same transaction
/// as the domain change so the two never diverge; a background dispatcher
/// delivers each row at least once and stamps <see cref="ProcessedAt"/>.
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }

    /// <summary>e.g. "role_grant.changed", "subject.deactivated".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>JSON payload — shape is per-<see cref="Type"/>, not modeled here.</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
