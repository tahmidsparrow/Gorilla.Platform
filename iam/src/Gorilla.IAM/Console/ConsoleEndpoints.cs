using System.Security.Claims;
using Gorilla.IAM.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

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
                    await SignInAsync(http, success.SubjectId, success.Email, success.Name, isAdmin: success.IsAdmin);
                    // Honor ReturnUrl (e.g. OidcEndpoints' /connect/authorize challenge)
                    // when present and safe, so a real browser sign-in actually
                    // continues the flow that triggered the login instead of always
                    // landing on the dashboard. Confirmed the hard way: without this,
                    // a real OIDC login through a real browser dead-ended here.
                    return Results.Redirect(returnUrl ?? "/console");

                case LoginResult.MustChangePassword pending:
                    // No Role claim and no admin grant needed here (see class doc —
                    // Success can now happen for non-admins too) — the "authorized"
                    // group below requires RequireRole(admin), so this session can
                    // reach nothing except /console/change-password (and
                    // /console/logout). See AccessDeniedPath in Program.cs. ReturnUrl
                    // rides along (not ignored) so the OIDC flow that triggered this
                    // login can still continue once the password is changed — spec
                    // 3.2 just means no token is issued until then, not that the
                    // flow has to restart from scratch.
                    await SignInAsync(http, pending.SubjectId, pending.Email, pending.Name, isAdmin: false, mustChangePassword: true);
                    return Results.Redirect(returnUrl is null
                        ? "/console/change-password"
                        : $"/console/change-password?ReturnUrl={Uri.EscapeDataString(returnUrl)}");

                default:
                    // Deliberately the same generic message for every
                    // LoginFailureReason — an unauthenticated caller must not
                    // be able to distinguish "wrong password" from "correct
                    // password, not an admin" from "no such account."
                    return Results.Content(ConsoleHtml.LoginPage("Invalid email or password.", returnUrl), "text/html");
            }
        }).RequireRateLimiting("login");

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
        // care which state the caller arrived from. A regular non-admin
        // session (also no Role claim, now that BreakGlassAuthenticator isn't
        // admin-only) can reach the route too, but the handler itself turns
        // them away — see its own comment just below.
        var authenticatedOnly = console.MapGroup("").RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(ConsoleAuth.Scheme)
            .RequireAuthenticatedUser());

        // Reachable by anyone authenticated (see the group's own comment above),
        // but only actually useful for two cases: a pending forced reset
        // (must_change_password claim), or an admin's own voluntary rotation
        // (ConsoleHtml.Dashboard's "Change my password" link — ConsoleEndpoints'
        // ChangePasswordAsync doesn't care which state the caller arrived from).
        // Anyone else landing here — a non-admin, non-pending subject bounced by
        // AccessDeniedPath after a role-denied request — gets told plainly that
        // this area needs an iam:admin grant, not the password form.
        authenticatedOnly.MapGet("/change-password", (HttpContext http) =>
        {
            var pending = http.User.HasClaim("must_change_password", "true");
            var isAdmin = http.User.IsInRole(IamSelfConsumerApp.AdminRole);
            if (!pending && !isAdmin)
                return Results.Content(ConsoleHtml.AccessDeniedPage(), "text/html");

            var returnUrl = SafeLocalReturnUrl(http.Request.Query["ReturnUrl"]);
            return Results.Content(ConsoleHtml.ChangePasswordPage(returnUrl: returnUrl), "text/html");
        });

        authenticatedOnly.MapPost("/change-password", async (HttpContext http, BreakGlassAuthenticator auth) =>
        {
            var form = await http.Request.ReadFormAsync();
            var currentPassword = form["currentPassword"].ToString();
            var newPassword = form["newPassword"].ToString();
            var confirmPassword = form["confirmPassword"].ToString();
            var returnUrl = SafeLocalReturnUrl(form["ReturnUrl"]);
            var subjectId = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            if (newPassword != confirmPassword)
                return Results.Content(
                    ConsoleHtml.ChangePasswordPage("New password and confirmation do not match.", returnUrl), "text/html");

            var result = await auth.ChangePasswordAsync(subjectId, currentPassword, newPassword);
            if (result != ChangePasswordResult.Changed)
            {
                var message = result == ChangePasswordResult.WrongCurrentPassword
                    ? "Current password is incorrect."
                    : "New password does not meet the password policy (at least 8 characters, at most 72 bytes, a letter and a digit, different from the current password).";
                return Results.Content(ConsoleHtml.ChangePasswordPage(message, returnUrl), "text/html");
            }

            // Sign out rather than upgrade the interim session in place — no
            // session survives a password change; sign in again with the new
            // password. Simpler and safer than reissuing claims mid-session.
            // ReturnUrl rides through to the login page so a caller who arrived
            // here via a pending OIDC flow (see the login handler above) isn't
            // dropped back at the start of it after signing in again.
            await http.SignOutAsync(ConsoleAuth.Scheme);
            return Results.Content(
                ConsoleHtml.LoginPage("Password changed. Sign in with your new password.", returnUrl), "text/html");
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
            var createFailed = http.Request.Query["createFailed"].ToString();
            return Results.Content(ConsoleHtml.Dashboard(subjects, roles, email, resetFailed, createFailed), "text/html");
        });

        // Creating a person is unambiguously an admin action, so it lives in this
        // group beside grant/revoke/reset rather than the authenticated-only one.
        // Same redirect-with-a-query-flag idiom as reset-password below: the
        // dashboard has no per-form state worth preserving, unlike the login and
        // change-password pages which re-render inline.
        authorized.MapPost("/subjects", async (HttpContext http, SubjectAdminService admin) =>
        {
            var form = await http.Request.ReadFormAsync();
            var result = await admin.CreateSubjectAsync(
                form["email"].ToString(), form["name"].ToString(), form["temporaryPassword"].ToString());

            return Results.Redirect(result == CreateSubjectResult.Created
                ? "/console"
                : $"/console?createFailed={result}");
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

    /// <param name="mustChangePassword">Marks the interim, forced-reset session
    /// (spec 3.2: no token/session privileges until the password changes). Distinct
    /// from "no Role claim" as a signal: once non-admins can also reach a normal,
    /// non-pending Success (see BreakGlassAuthenticator's class doc), "no Role
    /// claim" alone no longer means "must change password" — this claim is the
    /// actual signal /console/change-password's GET handler branches on.</param>
    private static async Task SignInAsync(
        HttpContext http, Guid subjectId, string email, string name, bool isAdmin, bool mustChangePassword = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subjectId.ToString()),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, name),
        };
        if (isAdmin)
            claims.Add(new Claim(ClaimTypes.Role, IamSelfConsumerApp.AdminRole));
        if (mustChangePassword)
            claims.Add(new Claim("must_change_password", "true"));

        var identity = new ClaimsIdentity(claims, ConsoleAuth.Scheme);
        await http.SignInAsync(ConsoleAuth.Scheme, new ClaimsPrincipal(identity));
    }
}
