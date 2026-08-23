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
using Microsoft.EntityFrameworkCore;
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
    private const string Password = "FlowTest@123";

    [SkippableFact]
    public async Task Admin_subject_with_an_ats_grant_completes_the_full_flow()
    {
        await RunSkippableAsync(async factory =>
        {
            // Grants BOTH iam:admin and an ats role — proves the admin path still
            // works end to end after opening login to everyone (this session's
            // grant check now requires an ats grant specifically; iam:admin alone
            // no longer implies access to every other app's client).
            var (email, subjectId) = await SeedSubjectAsync(factory, "OIDC Flow Test Admin", [(AppKey, AdminRole), ("ats", "SuperAdmin")]);
            await AssertFullFlowSucceedsAsync(factory, email, subjectId, "OIDC Flow Test Admin");
        });
    }

    /// <summary>The master regression test: before this plan's changes, a subject
    /// without iam:admin could not complete ANY OIDC sign-in at all — retiring RG's
    /// own local login would have locked out every ordinary Recruiter/Interviewer.
    /// This is that exact scenario, proven to work now.</summary>
    [SkippableFact]
    public async Task Non_admin_subject_with_only_an_ats_grant_completes_the_full_flow()
    {
        await RunSkippableAsync(async factory =>
        {
            var (email, subjectId) = await SeedSubjectAsync(factory, "OIDC Flow Test Non-Admin", [("ats", "Recruiter")]);
            await AssertFullFlowSucceedsAsync(factory, email, subjectId, "OIDC Flow Test Non-Admin");
        });
    }

    /// <summary>The other half of the same regression: opening login to everyone
    /// must not mean every subject gets a token for every app. A subject who is a
    /// real, active, correctly-authenticated IAM subject but holds no grant for
    /// "ats" at all must be refused here, not handed a token that RG would then
    /// have to be trusted to refuse on its own.</summary>
    [SkippableFact]
    public async Task Subject_with_no_ats_grant_is_refused_access_denied()
    {
        await RunSkippableAsync(async factory =>
        {
            // iam:admin only — proves this is genuinely about the ats grant, not
            // just "no grants of any kind."
            var (email, _) = await SeedSubjectAsync(factory, "OIDC Flow Test No Ats Grant", [(AppKey, AdminRole)]);

            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var (_, challenge) = GeneratePkcePair();
            var authorizeUrl = BuildAuthorizeUrl(challenge);

            var loginRedirect = await LoginAndGetContinuationUrlAsync(client, authorizeUrl, email, Password);
            var authorizeResponse = await client.GetAsync(loginRedirect);

            // OAuth2 convention: an authorization error is still a redirect to
            // RedirectUri, just carrying ?error=access_denied instead of ?code=...
            // — not a raw 403. OpenIddict's Results.Forbid(..., Error=access_denied)
            // produces exactly this.
            Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);
            var deniedLocation = authorizeResponse.Headers.Location
                ?? throw new InvalidOperationException("Denied authorize response carried no redirect Location.");
            Assert.StartsWith(RedirectUri, deniedLocation.ToString());
            var deniedQuery = System.Web.HttpUtility.ParseQueryString(deniedLocation.Query);
            Assert.Equal("access_denied", deniedQuery["error"]);
            Assert.Null(deniedQuery["code"]);
        });
    }

    private static async Task RunSkippableAsync(Func<WebApplicationFactory<Program>, Task> body)
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "GORILLA_IAM_TEST_MYSQL_CONNECTION is not set — this test needs a real, already-migrated MySQL database.");

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
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
            await body(factory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
            Environment.SetEnvironmentVariable("Iam__AtsClientRedirectUris", null);
        }
    }

    /// <summary>A fresh, uniquely-emailed subject with a known password and the
    /// given grants. Written directly rather than via BootstrapAdminSeeder so
    /// tests don't depend on run order/state left behind by any other test or
    /// manual session against the same database.</summary>
    private static async Task<(string Email, Guid SubjectId)> SeedSubjectAsync(
        WebApplicationFactory<Program> factory, string name, (string AppKey, string Role)[] grants)
    {
        var email = $"oidc-flow-test-{Guid.NewGuid():N}@example.com";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var subject = new Subject
        {
            Email = email,
            Name = name,
            IsActive = true,
            Credential = new Credential { Algorithm = CredentialAlgorithm.Bcrypt, Hash = BcryptPasswordHasher.Hash(Password) },
        };
        db.Subjects.Add(subject);
        db.RoleGrants.AddRange(grants.Select(g => new RoleGrant { SubjectId = subject.Id, AppKey = g.AppKey, Role = g.Role }));
        await db.SaveChangesAsync();
        return (email, subject.Id);
    }

    private static string BuildAuthorizeUrl(string codeChallenge) =>
        "/connect/authorize" +
        $"?client_id={ClientId}" +
        "&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        "&scope=openid%20email%20profile%20offline_access" +
        $"&code_challenge={codeChallenge}" +
        "&code_challenge_method=S256" +
        "&state=test-state";

    /// <summary>Deliberately NOT logging in first: a real browser hits
    /// /connect/authorize with no console session yet, gets challenged to
    /// /console/login?ReturnUrl=..., and the login POST has to actually honor that
    /// ReturnUrl to land back here — logging in first (this test's original shape)
    /// never exercised that path at all, and missed a real bug: the login handler
    /// ignored ReturnUrl entirely and always redirected to /console, dead-ending
    /// every real first-time browser login. Confirmed only by driving this through
    /// an actual browser (Increment 3) — see ConsoleEndpoints.cs's SafeLocalReturnUrl.
    /// Returns the post-login redirect Location — the caller decides what to assert
    /// about hitting it (a real flow continues to a code; a denied one doesn't).</summary>
    private static async Task<Uri> LoginAndGetContinuationUrlAsync(
        HttpClient client, string authorizeUrl, string email, string password)
    {
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
                ["email"] = email,
                ["password"] = password,
                ["ReturnUrl"] = returnUrl,
            }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        var loginRedirect = loginResponse.Headers.Location
            ?? throw new InvalidOperationException("Login response carried no redirect Location.");
        var loginRedirectPathAndQuery = loginRedirect.IsAbsoluteUri ? loginRedirect.PathAndQuery : loginRedirect.ToString();
        Assert.Equal(returnUrl, loginRedirectPathAndQuery);

        return loginRedirect;
    }

    private static async Task AssertFullFlowSucceedsAsync(
        WebApplicationFactory<Program> factory, string email, Guid subjectId, string expectedName)
    {
        // WebApplicationFactoryClientOptions.HandleCookies defaults true —
        // the console login's Set-Cookie is persisted and replayed
        // automatically on the /connect/authorize request below, same as a
        // real browser.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var (verifier, challenge) = GeneratePkcePair();
        var authorizeUrl = BuildAuthorizeUrl(challenge);
        var loginRedirect = await LoginAndGetContinuationUrlAsync(client, authorizeUrl, email, Password);

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
        // the claims the /connect/authorize handler is responsible for shaping
        // correctly: the real subject id, not a placeholder.
        var claims = DecodeJwtPayload(idToken!);
        Assert.Equal(subjectId.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal(email, claims.GetProperty("email").GetString());
        Assert.Equal(expectedName, claims.GetProperty("name").GetString());
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
