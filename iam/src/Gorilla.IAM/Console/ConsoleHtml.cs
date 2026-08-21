using System.Net;

namespace Gorilla.IAM.Console;

/// <summary>
/// Deliberately raw string HTML, not Razor Pages — spec section 3.1: "keep
/// it deliberately spartan." No client-side JS, no styling framework; every
/// action is a plain HTML form POST. The one non-negotiable: every value
/// interpolated from data (email, name, role strings) goes through
/// <see cref="E"/> — this renders subject-supplied data (email, name), so
/// skipping it would be a stored XSS hole in an admin tool that already has
/// full account-takeover blast radius.
/// </summary>
public static class ConsoleHtml
{
    public static string E(string s) => WebUtility.HtmlEncode(s);

    // Not interpolated (no $) so the braces below are plain CSS, not raw
    // string literal interpolation holes.
    private const string Style = """
        <style>
          body { font-family: system-ui, sans-serif; max-width: 900px; margin: 2rem auto; padding: 0 1rem; }
          table { border-collapse: collapse; width: 100%; }
          th, td { text-align: left; padding: 0.4rem 0.6rem; border-bottom: 1px solid #ddd; }
          .inactive { color: #999; }
          .error { color: #b00020; }
          form.inline { display: inline; }
          input, select, button { font: inherit; padding: 0.3rem; }
        </style>
        """;

    public static string Page(string title, string body) => $"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>{E(title)} — Gorilla.IAM</title>
        {Style}</head>
        <body>
        <h1>{E(title)}</h1>
        {body}
        </body></html>
        """;

    /// <param name="returnUrl">Where to land after a successful full-admin sign-in —
    /// set when the cookie scheme's own Challenge() (e.g. OidcEndpoints' /connect/authorize
    /// handler) redirected here with ?ReturnUrl=..., so the console can hand the caller
    /// back to whatever triggered the challenge instead of always landing on /console.
    /// Carried as a hidden field, not left in the query string: the form's action has
    /// no query string of its own, so a query-string-only ReturnUrl would be silently
    /// dropped on submit.</param>
    public static string LoginPage(string? error = null, string? returnUrl = null)
    {
        var errorHtml = error is null ? "" : $"""<p class="error">{E(error)}</p>""";
        var returnUrlHtml = returnUrl is null ? "" : $"""<input type="hidden" name="ReturnUrl" value="{E(returnUrl)}">""";
        return Page("Break-glass sign-in", $"""
            <p>Bootstrap/break-glass console — spec section 3.1. Requires an
            <code>iam:admin</code> grant.</p>
            {errorHtml}
            <form method="post" action="/console/login">
              {returnUrlHtml}
              <p><label>Email <input type="email" name="email" required autofocus></label></p>
              <p><label>Password <input type="password" name="password" required></label></p>
              <button type="submit">Sign in</button>
            </form>
            """);
    }

    public static string ChangePasswordPage(string? error = null)
    {
        var errorHtml = error is null ? "" : $"""<p class="error">{E(error)}</p>""";
        return Page("Change password", $"""
            <p>{errorHtml}</p>
            <form method="post" action="/console/change-password">
              <p><label>Current password <input type="password" name="currentPassword" required autofocus></label></p>
              <p><label>New password <input type="password" name="newPassword" required></label></p>
              <p><label>Confirm new password <input type="password" name="confirmPassword" required></label></p>
              <button type="submit">Change password</button>
            </form>
            <form method="post" action="/console/logout"><button type="submit">Sign out instead</button></form>
            """);
    }

    public static string Dashboard(
        IReadOnlyList<SubjectSummary> subjects,
        IReadOnlyList<(string AppKey, string[] Roles)> grantableRoles,
        string signedInAsEmail,
        bool resetFailed = false)
    {
        var roleOptions = string.Join("", grantableRoles.SelectMany(a =>
            a.Roles.Select(r => $"""<option value="{E(a.AppKey)}:{E(r)}">{E(a.AppKey)}:{E(r)}</option>""")));

        var rows = string.Join("", subjects.Select(s =>
        {
            var grants = string.Join(", ", s.Grants.Select(g => $"{E(g.AppKey)}:{E(g.Role)}"));
            var revokeForms = string.Join(" ", s.Grants.Select(g => $"""
                <form class="inline" method="post" action="/console/subjects/{s.Id}/grants/revoke">
                  <input type="hidden" name="appKey" value="{E(g.AppKey)}">
                  <input type="hidden" name="role" value="{E(g.Role)}">
                  <button type="submit" title="Revoke {E(g.AppKey)}:{E(g.Role)}">x</button>
                </form>
                """));
            var toggleLabel = s.IsActive ? "Deactivate" : "Reactivate";
            var rowClass = s.IsActive ? "" : "inactive";

            return $"""
                <tr class="{rowClass}">
                  <td>{E(s.Email)}</td>
                  <td>{E(s.Name)}</td>
                  <td>{(s.IsActive ? "active" : "INACTIVE")}</td>
                  <td>{grants} {revokeForms}</td>
                  <td>
                    <form class="inline" method="post" action="/console/subjects/{s.Id}/grants">
                      <select name="appKeyAndRole">{roleOptions}</select>
                      <button type="submit">Grant</button>
                    </form>
                  </td>
                  <td>
                    <form class="inline" method="post" action="/console/subjects/{s.Id}/active">
                      <input type="hidden" name="active" value="{(!s.IsActive).ToString().ToLowerInvariant()}">
                      <button type="submit">{toggleLabel}</button>
                    </form>
                  </td>
                  <td>
                    <form class="inline" method="post" action="/console/subjects/{s.Id}/reset-password">
                      <input type="password" name="newPassword" placeholder="temporary password" required>
                      <button type="submit" title="Sets this password and forces a change on next sign-in">Reset password</button>
                    </form>
                  </td>
                </tr>
                """;
        }));

        var resetFailedHtml = resetFailed
            ? """<p class="error">Reset failed: the password did not meet the policy (at least 8 characters, at most 72 bytes, a letter and a digit).</p>"""
            : "";

        return Page("Subjects", $"""
            <p>Signed in as {E(signedInAsEmail)} —
              <form class="inline" method="post" action="/console/logout"><button type="submit">Sign out</button></form> ·
              <a href="/console/change-password">Change my password</a></p>
            {resetFailedHtml}
            <table>
              <tr><th>Email</th><th>Name</th><th>Status</th><th>Grants</th><th>Grant a role</th><th></th><th>Reset password</th></tr>
              {rows}
            </table>
            <p>Resetting a password does not send an email — communicate the
            temporary password to the person out of band. They must change it
            on their next sign-in.</p>
            """);
    }
}
