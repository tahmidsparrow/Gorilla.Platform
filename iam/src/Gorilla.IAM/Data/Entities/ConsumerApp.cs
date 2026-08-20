namespace Gorilla.IAM.Data.Entities;

/// <summary>
/// The app registry: one row per consumer, holding its role vocabulary for
/// grant-write validation (spec section 3.1) — <b>not</b> the same thing as
/// OpenIddict's own <c>OpenIddictApplications</c> table, which holds OAuth
/// *clients* (redirect URIs, secrets). A consumer app and an OpenIddict
/// application are correlated by <see cref="Key"/> but are separate rows;
/// see the "naming collision to avoid" note in spec section 3.4.
///
/// Adding a new app (spec section 3.6) is one row here plus an OpenIddict
/// client — zero changes to this service's code — provided HR's grant panel
/// renders its checkboxes from this table rather than hard-coding them.
/// </summary>
public class ConsumerApp
{
    /// <summary>Stable short key used as the JWT audience entry and the
    /// namespaced role-claim prefix (e.g. "hr" -> hr_roles).</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ICollection<ConsumerAppRole> Roles { get; set; } = [];
}
