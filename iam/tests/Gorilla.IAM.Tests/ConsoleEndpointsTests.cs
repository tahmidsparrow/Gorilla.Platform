using System.Net;
using Gorilla.IAM.Auth;
using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using static Gorilla.IAM.Data.IamSelfConsumerApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Gorilla.IAM.Tests;

/// <summary>
/// Real-HTTP coverage for /console's endpoints — previously untested directly
/// (OidcFlowTests exercises /console/login only as a means to an authorize/token
/// round trip). Opt-in via GORILLA_IAM_TEST_MYSQL_CONNECTION, same reasoning as
/// OidcFlowTests: Program.cs's DbContext registration is hardcoded to
/// UseMySql, so there is no SQLite-backed way to boot the real HTTP pipeline
/// this exercises.
///
/// Covers the three behaviors this plan's changes are responsible for and
/// that nothing else tests: ReturnUrl surviving the forced-password-change
/// round trip, a non-admin correctly seeing "access denied" rather than the
/// password form, and an admin's existing voluntary self-service rotation
/// still working unchanged.
/// </summary>
public class ConsoleEndpointsTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("GORILLA_IAM_TEST_MYSQL_CONNECTION");
    private const string Password = "FlowTest@123";
    private const string NewPassword = "NewFlowTest@456";

    [SkippableFact]
    public async Task ReturnUrl_survives_the_forced_password_change_round_trip()
    {
        await RunSkippableAsync(async factory =>
        {
            var email = await SeedSubjectAsync(factory, mustChangePassword: true, grants: [("ats", "Recruiter")]);
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            const string returnUrl = "/connect/authorize?client_id=ats&state=xyz";

            // WebUtility.HtmlEncode's output (ConsoleHtml.E) — the hidden-field
            // assertions below check for this exact rendered form, "&" and all.
            var returnUrlHtmlEncoded = System.Net.WebUtility.HtmlEncode(returnUrl);
            var hiddenFieldMarker = $"name=\"ReturnUrl\" value=\"{returnUrlHtmlEncoded}\"";

            var loginResponse = await client.PostAsync("/console/login",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["email"] = email, ["password"] = Password, ["ReturnUrl"] = returnUrl,
                }));
            Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
            var changePasswordLocation = loginResponse.Headers.Location!;
            var (changePasswordPath, changePasswordQuery) = PathAndQuery(changePasswordLocation);
            Assert.Equal("/console/change-password", changePasswordPath);
            Assert.Equal(returnUrl, System.Web.HttpUtility.ParseQueryString(changePasswordQuery)["ReturnUrl"]);

            // The GET page carries ReturnUrl forward as a hidden field — confirm it's
            // actually there, not just present on the redirect that got us here.
            var formPage = await client.GetStringAsync(changePasswordLocation);
            Assert.Contains(hiddenFieldMarker, formPage);

            var changeResponse = await client.PostAsync("/console/change-password",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["currentPassword"] = Password,
                    ["newPassword"] = NewPassword,
                    ["confirmPassword"] = NewPassword,
                    ["ReturnUrl"] = returnUrl,
                }));
            Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode); // re-renders LoginPage inline, not a redirect
            var loginPageAfterChange = await changeResponse.Content.ReadAsStringAsync();
            Assert.Contains(hiddenFieldMarker, loginPageAfterChange);

            // Signing in again with the new password continues the original flow.
            var secondLogin = await client.PostAsync("/console/login",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["email"] = email, ["password"] = NewPassword, ["ReturnUrl"] = returnUrl,
                }));
            Assert.Equal(HttpStatusCode.Redirect, secondLogin.StatusCode);
            var (secondLoginPath, secondLoginQuery) = PathAndQuery(secondLogin.Headers.Location!);
            Assert.Equal(returnUrl, $"{secondLoginPath}{secondLoginQuery}"); // Query already includes its leading '?', if any
        });
    }

    [SkippableFact]
    public async Task Non_admin_visiting_change_password_directly_sees_access_denied_not_the_form()
    {
        await RunSkippableAsync(async factory =>
        {
            var email = await SeedSubjectAsync(factory, mustChangePassword: false, grants: [("ats", "Recruiter")]);
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var login = await client.PostAsync("/console/login",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email, ["password"] = Password }));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode); // ordinary success, lands on /console

            var page = await client.GetStringAsync("/console/change-password");
            Assert.Contains("iam:admin", page);
            Assert.DoesNotContain("currentPassword", page);
        });
    }

    [SkippableFact]
    public async Task Admin_can_still_voluntarily_reach_the_change_password_form()
    {
        await RunSkippableAsync(async factory =>
        {
            var email = await SeedSubjectAsync(factory, mustChangePassword: false, grants: [(AppKey, AdminRole)]);
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var login = await client.PostAsync("/console/login",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email, ["password"] = Password }));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

            var page = await client.GetStringAsync("/console/change-password");
            Assert.Contains("currentPassword", page);
            Assert.DoesNotContain("requires an", page);
        });
    }

    /// <summary>WebApplicationFactory's redirect Location headers come back as
    /// either absolute or relative URIs depending on context (confirmed the hard
    /// way in OidcFlowTests) — Uri.AbsolutePath/.Query throw on a relative one, so
    /// this splits on '?' directly instead of relying on those properties.</summary>
    private static (string Path, string Query) PathAndQuery(Uri uri)
    {
        if (uri.IsAbsoluteUri)
            return (uri.AbsolutePath, uri.Query);
        var s = uri.ToString();
        var i = s.IndexOf('?');
        return i < 0 ? (s, "") : (s[..i], s[i..]);
    }

    private static async Task RunSkippableAsync(Func<WebApplicationFactory<Program>, Task> body)
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "GORILLA_IAM_TEST_MYSQL_CONNECTION is not set — this test needs a real, already-migrated MySQL database.");

        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
            await body(factory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
        }
    }

    private static async Task<string> SeedSubjectAsync(
        WebApplicationFactory<Program> factory, bool mustChangePassword, (string AppKey, string Role)[] grants)
    {
        var email = $"console-endpoints-test-{Guid.NewGuid():N}@example.com";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var subject = new Subject
        {
            Email = email,
            Name = "Console Endpoints Test Subject",
            IsActive = true,
            Credential = new Credential
            {
                Algorithm = CredentialAlgorithm.Bcrypt,
                Hash = BcryptPasswordHasher.Hash(Password),
                MustChangePassword = mustChangePassword,
            },
        };
        db.Subjects.Add(subject);
        db.RoleGrants.AddRange(grants.Select(g => new RoleGrant { SubjectId = subject.Id, AppKey = g.AppKey, Role = g.Role }));
        await db.SaveChangesAsync();
        return email;
    }
}
