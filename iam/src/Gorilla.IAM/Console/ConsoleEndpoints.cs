using System.Security.Claims;
using Gorilla.IAM.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Gorilla.IAM.Console;

/// <summary>Cookie scheme name for the break-glass console — deliberately
/// separate from OpenIddict's own cookie/token handling, since this console
/// authenticates directly against Subject/Credential and must not depend on
/// OpenIddict's flows being configured or working. See
/// BreakGlassAuthenticator's class doc for why.</summary>
public static class ConsoleAuth
{
    public const string Scheme = "BreakGlassCookie";
}

/// <summary>
/// Thin HTTP layer over BreakGlassAuthenticator/SubjectAdminService — this
/// estate's own convention (CLAUDE.md: "routers -> services -> models; keep
/// routers thin") applied here even though this is C#, not the Python
/// backend it was written for.
/// </summary>
public static class ConsoleEndpoints
{
    public static void MapConsoleEndpoints(this WebApplication app)
    {
        var console = app.MapGroup("/console");

        console.MapGet("/login", () => Results.Content(ConsoleHtml.LoginPage(), "text/html"));

        console.MapPost("/login", async (HttpContext http, BreakGlassAuthenticator auth) =>
        {
            var form = await http.Request.ReadFormAsync();
            var email = form["email"].ToString();
            var password = form["password"].ToString();

            var result = await auth.AuthenticateAsync(email, password);
            if (result is not LoginResult.Success success)
            {
                // Deliberately the same generic message for every failure
                // reason (LoginFailureReason has four distinct cases) — an
                // unauthenticated caller must not be able to distinguish
                // "wrong password" from "correct password, not an admin"
                // from "no such account."
                return Results.Content(ConsoleHtml.LoginPage("Invalid email or password."), "text/html");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, success.SubjectId.ToString()),
                new(ClaimTypes.Email, success.Email),
                new(ClaimTypes.Name, success.Name),
                new(ClaimTypes.Role, IamSelfConsumerApp.AdminRole),
            };
            var identity = new ClaimsIdentity(claims, ConsoleAuth.Scheme);
            await http.SignInAsync(ConsoleAuth.Scheme, new ClaimsPrincipal(identity));

            return Results.Redirect("/console");
        });

        console.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(ConsoleAuth.Scheme);
            return Results.Redirect("/console/login");
        });

        // A nested group, not RequireAuthorization() on `console` directly:
        // RouteGroupBuilder conventions apply to every route matched under
        // that builder regardless of map order, so calling
        // RequireAuthorization() on `console` itself would also gate
        // /console/login and /console/logout — an unbreakable redirect
        // loop, since the cookie scheme's own challenge redirects to
        // LoginPath ("/console/login"). Confirmed by hitting it for real:
        // POST /console/login came back 302 to
        // /console/login?ReturnUrl=%2Fconsole%2Flogin instead of processing
        // the login. MapGroup("") on the existing group keeps the same URL
        // prefix but gets its own, independent convention set.
        var authorized = console.MapGroup("").RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(ConsoleAuth.Scheme)
            .RequireRole(IamSelfConsumerApp.AdminRole));

        authorized.MapGet("/", async (HttpContext http, SubjectAdminService admin) =>
        {
            var subjects = await admin.ListSubjectsAsync();
            var roles = await admin.ListGrantableRolesAsync();
            var email = http.User.FindFirstValue(ClaimTypes.Email) ?? "";
            return Results.Content(ConsoleHtml.Dashboard(subjects, roles, email), "text/html");
        });

        authorized.MapPost("/subjects/{id:guid}/active", async (Guid id, HttpContext http, SubjectAdminService admin) =>
        {
            var form = await http.Request.ReadFormAsync();
            var active = form["active"] == "true";
            await admin.SetActiveAsync(id, active);
            return Results.Redirect("/console");
        });

        authorized.MapPost("/subjects/{id:guid}/grants", async (Guid id, HttpContext http, SubjectAdminService admin) =>
        {
            var form = await http.Request.ReadFormAsync();
            var raw = form["appKeyAndRole"].ToString();
            var parts = raw.Split(':', 2);
            if (parts.Length == 2)
            {
                var grantedBy = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
                await admin.GrantRoleAsync(id, parts[0], parts[1], grantedBy);
            }
            return Results.Redirect("/console");
        });

        authorized.MapPost("/subjects/{id:guid}/grants/revoke", async (Guid id, HttpContext http, SubjectAdminService admin) =>
        {
            var form = await http.Request.ReadFormAsync();
            await admin.RevokeRoleAsync(id, form["appKey"].ToString(), form["role"].ToString());
            return Results.Redirect("/console");
        });
    }
}
