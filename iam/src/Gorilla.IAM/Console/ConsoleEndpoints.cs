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

        console.MapGet("/login", (HttpContext http) =>
            Results.Content(ConsoleHtml.LoginPage(returnUrl: SafeLocalReturnUrl(http.Request.Query["ReturnUrl"])), "text/html"));

        console.MapPost("/login", async (HttpContext http, BreakGlassAuthenticator auth) =>
        {
            var form = await http.Request.ReadFormAsync();
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var returnUrl = SafeLocalReturnUrl(form["ReturnUrl"]);

            var result = await auth.AuthenticateAsync(email, password);
            switch (result)
            {
                case LoginResult.Success success:
                    await SignInAsync(http, success.SubjectId, success.Email, success.Name, isFullAdmin: true);
                    // Honor ReturnUrl (e.g. OidcEndpoints' /connect/authorize challenge)
                    // when present and safe, so a real browser sign-in actually
                    // continues the flow that triggered the login instead of always
                    // landing on the dashboard. Confirmed the hard way: without this,
                    // a real OIDC login through a real browser dead-ended here.
                    return Results.Redirect(returnUrl ?? "/console");

                case LoginResult.MustChangePassword pending:
                    // No Role claim — the "authorized" group below requires
                    // RequireRole(admin), so this session can reach nothing
                    // except /console/change-password (and /console/logout).
                    // See AccessDeniedPath in Program.cs for the other half
                    // of this: an authenticated-but-role-denied request lands
                    // there, not back at the login form. Deliberately ignores
                    // ReturnUrl — spec 3.2: no token is issued until the
                    // password changes, so an OIDC flow can't complete from here.
                    await SignInAsync(http, pending.SubjectId, pending.Email, pending.Name, isFullAdmin: false);
                    return Results.Redirect("/console/change-password");

                default:
                    // Deliberately the same generic message for every
                    // LoginFailureReason — an unauthenticated caller must not
                    // be able to distinguish "wrong password" from "correct
                    // password, not an admin" from "no such account."
                    return Results.Content(ConsoleHtml.LoginPage("Invalid email or password.", returnUrl), "text/html");
            }
        });

        console.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(ConsoleAuth.Scheme);
            return Results.Redirect("/console/login");
        });

        // Authenticated via the cookie scheme but deliberately no role
        // requirement — the interim "must change password" session (no Role
        // claim) has to be able to reach this even though it fails
        // RequireRole(admin) everywhere else. A full admin session can reach
        // it too (self-service rotation), since ChangePasswordAsync doesn't
        // care which state the caller arrived from.
        var authenticatedOnly = console.MapGroup("").RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(ConsoleAuth.Scheme)
            .RequireAuthenticatedUser());

        authenticatedOnly.MapGet("/change-password", () =>
            Results.Content(ConsoleHtml.ChangePasswordPage(), "text/html"));

        authenticatedOnly.MapPost("/change-password", async (HttpContext http, BreakGlassAuthenticator auth) =>
        {
            var form = await http.Request.ReadFormAsync();
            var currentPassword = form["currentPassword"].ToString();
            var newPassword = form["newPassword"].ToString();
            var confirmPassword = form["confirmPassword"].ToString();
            var subjectId = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            if (newPassword != confirmPassword)
                return Results.Content(ConsoleHtml.ChangePasswordPage("New password and confirmation do not match."), "text/html");

            var result = await auth.ChangePasswordAsync(subjectId, currentPassword, newPassword);
            if (result != ChangePasswordResult.Changed)
            {
                var message = result == ChangePasswordResult.WrongCurrentPassword
                    ? "Current password is incorrect."
                    : "New password does not meet the password policy (at least 8 characters, at most 72 bytes, a letter and a digit, different from the current password).";
                return Results.Content(ConsoleHtml.ChangePasswordPage(message), "text/html");
            }

            // Sign out rather than upgrade the interim session in place — no
            // session survives a password change; sign in again with the new
            // password. Simpler and safer than reissuing claims mid-session.
            await http.SignOutAsync(ConsoleAuth.Scheme);
            return Results.Content(ConsoleHtml.LoginPage("Password changed. Sign in with your new password."), "text/html");
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
            var resetFailed = http.Request.Query["resetFailed"] == "1";
            return Results.Content(ConsoleHtml.Dashboard(subjects, roles, email, resetFailed), "text/html");
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

        // The P2 blocker itself: an admin sets a temporary password for
        // someone else (SubjectAdminService.ResetPasswordAsync — see its
        // doc comment for why this exists and what it does not do, e.g. no
        // email). PolicyViolation surfaces as a redirect with a query flag
        // rather than a full form re-render — the dashboard has no per-row
        // form state to preserve, unlike the login/change-password pages.
        authorized.MapPost("/subjects/{id:guid}/reset-password", async (Guid id, HttpContext http, SubjectAdminService admin) =>
        {
            var form = await http.Request.ReadFormAsync();
            var result = await admin.ResetPasswordAsync(id, form["newPassword"].ToString());
            return Results.Redirect(result == ResetPasswordResult.PolicyViolation ? "/console?resetFailed=1" : "/console");
        });
    }

    /// <summary>Open-redirect guard: a ReturnUrl is only honored when it's a same-app
    /// relative path. "/connect/authorize?..." (this cookie scheme's own LoginPath
    /// challenge target) qualifies; an absolute or protocol-relative URL does not.</summary>
    private static string? SafeLocalReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : null;

    private static async Task SignInAsync(HttpContext http, Guid subjectId, string email, string name, bool isFullAdmin)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subjectId.ToString()),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, name),
        };
        if (isFullAdmin)
            claims.Add(new Claim(ClaimTypes.Role, IamSelfConsumerApp.AdminRole));

        var identity = new ClaimsIdentity(claims, ConsoleAuth.Scheme);
        await http.SignInAsync(ConsoleAuth.Scheme, new ClaimsPrincipal(identity));
    }
}
