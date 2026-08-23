using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Gorilla.IAM.Data.Seeding;

/// <summary>
/// Registers Recruitment.Gorilla's SPA as an OpenIddict OAuth client — a
/// row in OpenIddict's own <c>OpenIddictApplications</c> table, written via
/// <see cref="IOpenIddictApplicationManager"/>, not raw EF Core writes (the
/// manager owns normalization/validation of what it persists).
///
/// This is a <b>different table from <see cref="ConsumerApp"/></b>, on
/// purpose — see that class's doc comment for the "naming collision to
/// avoid on day one" spec calls out. Both happen to use the client id
/// <c>"ats"</c> for operator clarity, but they are two separate rows in two
/// separate tables: <c>ConsumerApp</c> is this service's own RBAC
/// vocabulary (what roles "ats" has); this is OAuth client registration
/// (how "ats" the SPA is allowed to authenticate against this server).
///
/// Idempotent the same way <see cref="ConsumerAppSeeder"/> and
/// <see cref="BootstrapAdminSeeder"/> are — safe to run on every boot.
/// </summary>
public static class OpenIddictClientSeeder
{
    public const string AtsClientId = "ats";

    /// <param name="redirectUrisCsv">Comma-separated absolute URIs — RG's
    /// frontend origin(s) plus any loopback URI a token-acquisition test
    /// helper needs. Empty/unset is a valid "not configured yet" state
    /// (matches <see cref="BootstrapAdminSeeder"/>'s style): the client is
    /// simply not registered rather than registered with no redirect URIs,
    /// which would be a client nothing could ever authorize against.
    /// Used for both <c>RedirectUris</c> and <c>PostLogoutRedirectUris</c> —
    /// one config knob, not two, until front-channel logout (spec section
    /// 3.5) actually needs somewhere different to land after signing out.</param>
    /// <returns>A warning message if nothing was registered, or null on
    /// success/already-registered — same contract as
    /// <see cref="BootstrapAdminSeeder.SeedAsync"/>.</returns>
    public static async Task<string?> SeedAsync(
        IOpenIddictApplicationManager manager, string? redirectUrisCsv, CancellationToken ct = default)
    {
        if (await manager.FindByClientIdAsync(AtsClientId, ct) is not null)
            return null;

        var redirectUris = (redirectUrisCsv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(uri => new Uri(uri, UriKind.Absolute))
            .ToList();

        if (redirectUris.Count == 0)
            return "Iam:AtsClientRedirectUris is not configured — the \"ats\" OpenIddict client was not registered.";

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = AtsClientId,
            ClientType = ClientTypes.Public, // a browser SPA: PKCE only, no client secret
            DisplayName = "Recruitment.Gorilla",
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OpenId,
                Permissions.Prefixes.Scope + Scopes.Email,
                Permissions.Prefixes.Scope + Scopes.Profile,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        foreach (var uri in redirectUris)
        {
            descriptor.RedirectUris.Add(uri);
            descriptor.PostLogoutRedirectUris.Add(uri);
        }

        await manager.CreateAsync(descriptor, ct);
        return null;
    }
}
