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

    public static string LoginPage(string? error = null)
    {
        var errorHtml = error is null ? "" : $"""<p class="error">{E(error)}</p>""";
        return Page("Break-glass sign-in", $"""
            <p>Bootstrap/break-glass console — spec section 3.1. Requires an
            <code>iam:admin</code> grant.</p>
            {errorHtml}
            <form method="post" action="/console/login">
              <p><label>Email <input type="email" name="email" required autofocus></label></p>
              <p><label>Password <input type="password" name="password" required></label></p>
              <button type="submit">Sign in</button>
            </form>
            """);
    }

    public static string Dashboard(
        IReadOnlyList<SubjectSummary> subjects,
        IReadOnlyList<(string AppKey, string[] Roles)> grantableRoles,
        string signedInAsEmail)
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
                </tr>
                """;
        }));

        return Page("Subjects", $"""
            <p>Signed in as {E(signedInAsEmail)} — <form class="inline" method="post" action="/console/logout"><button type="submit">Sign out</button></form></p>
            <table>
              <tr><th>Email</th><th>Name</th><th>Status</th><th>Grants</th><th>Grant a role</th><th></th></tr>
              {rows}
            </table>
            """);
    }
}
