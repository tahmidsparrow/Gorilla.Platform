using System.Security.Claims;
using Gorilla.IAM.Console;
using Gorilla.IAM.Data;
using Microsoft.AspNetCore; // GetOpenIddictServerRequest() — OpenIddictServerAspNetCoreHelpers
using Microsoft.AspNetCore.Authentication; // HttpContext.AuthenticateAsync, AuthenticationProperties
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions; // SetClaim/SetClaims/SetScopes/SetResources/SetDestinations
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Gorilla.IAM.Oidc;

/// <summary>
/// The actual authorize/token handlers OpenIddict's
/// <c>Enable*EndpointPassthrough()</c> calls in Program.cs require this
/// application to provide. "Passthrough" does not mean OpenIddict serves
/// these routes itself — it means OpenIddict validates the OAuth request
/// framing and then hands off to routing, which has nothing mapped at
/// those paths unless the app maps it. Confirmed the hard way: with these
/// endpoints absent, GET /connect/authorize 404'd even though the
/// discovery document correctly advertised the URL — the framing/discovery
/// layer and the actual route existing are two different things.
///
/// The interactive identity this asserts is the console's own
/// <see cref="ConsoleAuth.Scheme"/> cookie (<see cref="BreakGlassAuthenticator"/>
/// — no longer admin-only, see its class doc). Authenticating there proves
/// who the subject is, not what they may access: the grant check below is
/// what stops that from being enough on its own.
/// </summary>
public static class OidcEndpoints
{
    public static void MapOidcEndpoints(this WebApplication app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], async (HttpContext http) =>
        {
            var request = http.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict request could not be retrieved.");

            var result = await http.AuthenticateAsync(ConsoleAuth.Scheme);
            if (result.Principal is null)
            {
                // Not signed in to the console — challenge via the same
                // scheme, which redirects to /console/login (LoginPath) and
                // (via the default ReturnUrl mechanism) back here afterward.
                return Results.Challenge(authenticationSchemes: [ConsoleAuth.Scheme]);
            }

            var subjectId = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var email = result.Principal.FindFirstValue(ClaimTypes.Email)!;
            var name = result.Principal.FindFirstValue(ClaimTypes.Name)!;

            await using var scope = http.RequestServices.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var rolesByApp = (await db.RoleGrants
                    .Where(g => g.SubjectId == Guid.Parse(subjectId))
                    .Select(g => new { g.AppKey, g.Role })
                    .ToListAsync())
                .GroupBy(g => g.AppKey)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Role).ToArray());

            // Authenticating proves who the subject is; it says nothing about
            // whether they may use THIS client. role_grants is the authoritative
            // access-control record (spec section 3.1) — a subject with zero
            // grants for request.ClientId (e.g. an HR-only user hitting the
            // "ats" flow, or any subject with no grants at all) must never get a
            // token for it, regardless of what other apps they're allowed into.
            // client_id and ConsumerApp.Key align by convention (OpenIddictClientSeeder).
            if (!rolesByApp.ContainsKey(request.ClientId!))
            {
                return Results.Forbid(
                    new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                            "This account has no access to the requested application.",
                    }),
                    [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
            }

            // authenticationType non-null marks this identity as
            // authenticated — required for OpenIddict to accept the
            // sign-in below; nameType/roleType match the claim types
            // actually being set (sub/role), not ASP.NET Core's defaults.
            var identity = new ClaimsIdentity(
                authenticationType: "OpenIddict",
                nameType: Claims.Name,
                roleType: Claims.Role);

            identity.SetClaim(Claims.Subject, subjectId);
            identity.SetClaim(Claims.Email, email);
            identity.SetClaim(Claims.Name, name);

            // Spec section 3.2's token design: namespaced "{app}_roles"
            // claims, one array per app — never a flat "roles" list, since
            // "Admin" means something different in hr vs ats.
            foreach (var (appKey, roles) in rolesByApp)
                identity.SetClaims($"{appKey}_roles", [.. roles]);

            identity.SetDestinations(claim => claim.Type switch
            {
                _ when claim.Type == Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
                _ when claim.Type == Claims.Email => [Destinations.AccessToken, Destinations.IdentityToken],
                _ when claim.Type == Claims.Name => [Destinations.AccessToken, Destinations.IdentityToken],
                _ => [Destinations.AccessToken], // {app}_roles and anything else: access token only
            });

            var principal = new ClaimsPrincipal(identity);
            principal.SetScopes(request.GetScopes());
            // Resource/audience = the requesting client itself. Revisit once
            // a second client (hr) exists and a token might need to carry
            // more than one audience.
            principal.SetResources(request.ClientId!);

            return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        });

        app.MapPost("/connect/token", async (HttpContext http) =>
        {
            var request = http.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict request could not be retrieved.");

            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                // The principal stored against the authorization code (or
                // refresh token) at issuance time — set in the /authorize
                // handler above — not re-derived here.
                var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                if (result.Principal is null)
                    return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

                return Results.SignIn(result.Principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            throw new NotImplementedException($"Grant type not supported by this endpoint: {request.GrantType}.");
        });
    }
}
