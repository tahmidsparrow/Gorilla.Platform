using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gorilla.IAM.Auth;
using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using static Gorilla.IAM.Data.IamSelfConsumerApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Gorilla.IAM.Tests;

/// <summary>
/// Drives a real authorization-code+PKCE round trip against a running
/// instance of this application over real HTTP — not a simulation of
/// OpenIddict's internals, and not the SQLite this suite otherwise uses.
/// Deliberately backed by real MySQL: this exact code path (OpenIddict's
/// authorization/token storage under a real MySQL provider) already broke
/// twice in ways SQLite never would have caught — P1's composite-index
/// key-length bug, and this increment's "Type" column being too narrow —
/// both found only by actually driving this flow against a real database.
///
/// Opt-in, not run by a plain `dotnet test`: needs a real MySQL server with
/// the gorilla_iam schema already migrated. Set
/// GORILLA_IAM_TEST_MYSQL_CONNECTION to run it; skipped — reported as
/// Skipped, not silently Passed — if unset, so the rest of this suite (86
/// tests, zero external dependencies) stays exactly as fast and portable as
/// it always has been.
/// </summary>
public class OidcFlowTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("GORILLA_IAM_TEST_MYSQL_CONNECTION");

    private const string RedirectUri = "http://127.0.0.1:9999/callback";
    private const string ClientId = "ats";

    [SkippableFact]
    public async Task Authorization_code_PKCE_and_refresh_token_grants_all_issue_real_tokens()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "GORILLA_IAM_TEST_MYSQL_CONNECTION is not set — this test needs a real, already-migrated MySQL database.");

        var adminEmail = $"oidc-flow-test-{Guid.NewGuid():N}@example.com";
        const string adminPassword = "FlowTest@123";

        // Program.cs reads ConnectionStrings:DefaultConnection synchronously
        // at the top of Main, before WebApplicationBuilder.Build() — earlier
        // than WebApplicationFactory's ConfigureAppConfiguration hook can
        // inject config for a minimal-hosting entry point (confirmed the hard
        // way: that approach left the connection string empty and Program.cs's
        // own guard threw). Real process environment variables, set before
        // the factory ever touches Program.Main, are visible the same way
        // `dotnet run` with real env vars would see them.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("Iam__AtsClientRedirectUris", RedirectUri);
        try
        {
            await RunFlowAsync(adminEmail, adminPassword);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
            Environment.SetEnvironmentVariable("Iam__AtsClientRedirectUris", null);
        }
    }

    private static async Task RunFlowAsync(string adminEmail, string adminPassword)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

        // A fresh, uniquely-emailed subject with a known password and an
        // iam:admin grant — the one authenticated identity this flow can use
        // today (see OidcEndpoints' documented limitation). Written directly
        // rather than via BootstrapAdminSeeder so this test doesn't depend
        // on run order/state left behind by any other test or manual session
        // against the same database.
        Guid subjectId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var subject = new Subject
            {
                Email = adminEmail,
                Name = "OIDC Flow Test Admin",
                IsActive = true,
                Credential = new Credential { Algorithm = CredentialAlgorithm.Bcrypt, Hash = BcryptPasswordHasher.Hash(adminPassword) },
            };
            db.Subjects.Add(subject);
            db.RoleGrants.Add(new RoleGrant { SubjectId = subject.Id, AppKey = AppKey, Role = AdminRole });
            await db.SaveChangesAsync();
            subjectId = subject.Id;
        }

        // WebApplicationFactoryClientOptions.HandleCookies defaults true —
        // the console login's Set-Cookie is persisted and replayed
        // automatically on the /connect/authorize request below, same as a
        // real browser.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var (verifier, challenge) = GeneratePkcePair();
        var authorizeUrl = "/connect/authorize" +
            $"?client_id={ClientId}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=openid%20email%20profile%20offline_access" +
            $"&code_challenge={challenge}" +
            "&code_challenge_method=S256" +
            "&state=test-state";

        // Deliberately NOT logging in first: a real browser hits /connect/authorize
        // with no console session yet, gets challenged to /console/login?ReturnUrl=...,
        // and the login POST has to actually honor that ReturnUrl to land back here —
        // logging in first (the original shape of this test) never exercised that
        // path at all, and missed a real bug: the login handler ignored ReturnUrl
        // entirely and always redirected to /console, dead-ending every real
        // first-time browser login. Confirmed only by driving this through an
        // actual browser (Increment 3) — see ConsoleEndpoints.cs's SafeLocalReturnUrl.
        var challengeResponse = await client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, challengeResponse.StatusCode);
        var challengeLocation = challengeResponse.Headers.Location
            ?? throw new InvalidOperationException("Unauthenticated authorize response carried no redirect Location.");
        Assert.Equal("/console/login", challengeLocation.AbsolutePath);
        var returnUrl = System.Web.HttpUtility.ParseQueryString(challengeLocation.Query)["ReturnUrl"]
            ?? throw new InvalidOperationException($"No 'ReturnUrl' on the login challenge: {challengeLocation}");

        var loginResponse = await client.PostAsync("/console/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = adminEmail,
                ["password"] = adminPassword,
                ["ReturnUrl"] = returnUrl,
            }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        var loginRedirect = loginResponse.Headers.Location
            ?? throw new InvalidOperationException("Login response carried no redirect Location.");
        var loginRedirectPathAndQuery = loginRedirect.IsAbsoluteUri ? loginRedirect.PathAndQuery : loginRedirect.ToString();
        Assert.Equal(returnUrl, loginRedirectPathAndQuery);

        var authorizeResponse = await client.GetAsync(loginRedirect);
        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);
        var location = authorizeResponse.Headers.Location
            ?? throw new InvalidOperationException("Authorize response carried no redirect Location.");
        Assert.StartsWith(RedirectUri, location.ToString());

        var code = System.Web.HttpUtility.ParseQueryString(location.Query)["code"]
            ?? throw new InvalidOperationException($"No 'code' in redirect: {location}");

        var tokenResponse = await client.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = ClientId,
                ["code_verifier"] = verifier,
            }));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

        using var tokenDoc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();
        var idToken = tokenDoc.RootElement.GetProperty("id_token").GetString();
        var refreshToken = tokenDoc.RootElement.GetProperty("refresh_token").GetString();
        Assert.False(string.IsNullOrEmpty(accessToken));
        Assert.False(string.IsNullOrEmpty(idToken));
        Assert.False(string.IsNullOrEmpty(refreshToken));

        // The id_token is a plain signed JWT — decode its payload directly and check
        // the claims this increment's /connect/authorize handler is responsible for
        // shaping correctly: the real subject id, not a placeholder.
        var claims = DecodeJwtPayload(idToken!);
        Assert.Equal(subjectId.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal(adminEmail, claims.GetProperty("email").GetString());
        Assert.Equal("OIDC Flow Test Admin", claims.GetProperty("name").GetString());
        Assert.Equal(ClientId, claims.GetProperty("aud").GetString());

        // The access token must ALSO be a plain signed JWT (3 dot-separated segments),
        // not OpenIddict's default encrypted JWE (5 segments) — a consumer like RG
        // validates this one directly via generic JwtBearer/JWKS, which cannot decrypt
        // a JWE at all. Confirmed the hard way: without Program.cs's
        // DisableAccessTokenEncryption(), RG rejected every real access token with
        // WWW-Authenticate: Bearer error="invalid_token".
        Assert.Equal(3, accessToken!.Split('.').Length);
        var accessClaims = DecodeJwtPayload(accessToken);
        Assert.Equal(subjectId.ToString(), accessClaims.GetProperty("sub").GetString());
        Assert.Equal(ClientId, accessClaims.GetProperty("aud").GetString());

        // The refresh_token grant this client was registered with is also
        // exercised for real, not assumed to work because authorization_code did.
        var refreshResponse = await client.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken!,
                ["client_id"] = ClientId,
            }));
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        using var refreshDoc = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(refreshDoc.RootElement.GetProperty("access_token").GetString()));
    }

    private static (string Verifier, string Challenge) GeneratePkcePair()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(48);
        var verifier = Convert.ToBase64String(verifierBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1];
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var bytes = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }
}
