using System.Net;
using Gorilla.IAM.Auth;
using Gorilla.IAM.Console;
using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Entities;
using static Gorilla.IAM.Data.IamSelfConsumerApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
[Collection(IamMySqlCollection.Name)]
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

    [SkippableFact]
    public async Task An_admin_can_create_a_person_who_then_appears_on_the_dashboard()
    {
        await RunSkippableAsync(async factory =>
        {
            var adminEmail = await SeedSubjectAsync(factory, mustChangePassword: false, grants: [(AppKey, AdminRole)]);
            var client = await SignInAsync(factory, adminEmail, Password);

            var created = await client.PostAsync("/console/subjects",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["email"] = "brand.new@example.com",
                    ["name"] = "Brand New",
                    ["temporaryPassword"] = "Temp@12345",
                }));

            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            var (path, query) = PathAndQuery(created.Headers.Location!);
            Assert.Equal("/console", path);
            Assert.DoesNotContain("createFailed", query);

            var dashboard = await client.GetStringAsync("/console");
            Assert.Contains("brand.new@example.com", dashboard);
        });
    }

    [SkippableFact]
    public async Task A_failed_create_comes_back_with_a_reason_the_dashboard_renders()
    {
        await RunSkippableAsync(async factory =>
        {
            var adminEmail = await SeedSubjectAsync(factory, mustChangePassword: false, grants: [(AppKey, AdminRole)]);
            var client = await SignInAsync(factory, adminEmail, Password);

            var created = await client.PostAsync("/console/subjects",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["email"] = "weak@example.com", ["name"] = "Weak", ["temporaryPassword"] = "short",
                }));

            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            var (_, query) = PathAndQuery(created.Headers.Location!);
            Assert.Contains(nameof(CreateSubjectResult.PolicyViolation), query);

            var dashboard = await client.GetStringAsync($"/console{query}");
            Assert.Contains("did not meet the policy", dashboard);
        });
    }

    /// <summary>Creating a person is an admin action — the route sits in the
    /// RequireRole(admin) group, so an ordinary signed-in subject must not reach it
    /// even though they can now sign in perfectly well.</summary>
    [SkippableFact]
    public async Task A_non_admin_cannot_create_a_person()
    {
        await RunSkippableAsync(async factory =>
        {
            var email = await SeedSubjectAsync(factory, mustChangePassword: false, grants: [("ats", "Recruiter")]);
            var client = await SignInAsync(factory, email, Password);

            var created = await client.PostAsync("/console/subjects",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["email"] = "sneaky@example.com", ["name"] = "Sneaky", ["temporaryPassword"] = "Temp@12345",
                }));

            // AccessDeniedPath sends a role-denied principal to /console/change-password
            // rather than returning a bare 403 — either way it is not a create.
            Assert.NotEqual("/console", PathAndQuery(created.Headers.Location ?? new Uri("/x", UriKind.Relative)).Path);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Assert.False(await db.Subjects.AnyAsync(s => s.Email == "sneaky@example.com"));
        });
    }

    /// <summary>Signs in and returns the cookie-carrying client, so each test does not
    /// re-spell the login POST.</summary>
    private static async Task<HttpClient> SignInAsync(
        WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsync("/console/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email, ["password"] = password }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return client;
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
            "GORILLA_IAM_TEST_MYSQL_CONNECTION is not set — this test needs a MySQL server to create a throwaway database on.");

        // A throwaway database, dropped afterwards — never the one the env var names.
        // See IamTestDatabase for why.
        await using var database = await IamTestDatabase.CreateAsync(ConnectionString!);
        await body(database.Factory);
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
