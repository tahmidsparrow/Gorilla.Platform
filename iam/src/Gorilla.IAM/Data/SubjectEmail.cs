namespace Gorilla.IAM.Data;

/// <summary>
/// The one way a subject's email is normalized, for both storage and lookup.
///
/// This has to be a single shared rule, not a convention each caller reimplements:
/// <see cref="Console.BreakGlassAuthenticator"/> finds a subject by comparing
/// <c>Subject.Email</c> to the normalized form of whatever was typed, so anything
/// that writes a subject in a different shape creates an account nobody can ever
/// sign into. The import has always normalized (that's why it works); a console
/// create form that stored "Jane.Doe@Example.com" verbatim would not, and MySQL's
/// case-insensitive collation would happily hide the bug on a developer's machine
/// while it bit somewhere else.
///
/// Trim + lowercase, matching gorilla-platform/scripts/reconcile_users.py's
/// normalize_email — same reasoning it gives: casing and whitespace differences
/// between two apps' signup forms must not read as two different people.
/// </summary>
public static class SubjectEmail
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
