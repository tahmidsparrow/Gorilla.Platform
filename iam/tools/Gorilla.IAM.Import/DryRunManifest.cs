namespace Gorilla.IAM.ImportTool;

/// <summary>
/// Known (email, password) pairs to replay against imported hashes — the
/// dry-run itself (spec section 9: "a dry-run import replaying known
/// passwords before the real one"). Nobody has everyone's plaintext, so this
/// can only ever be a small, explicitly-known set, never a full audit.
///
/// Defaults to this estate's seeded dev accounts — per-user decision, since
/// this machine's data isn't real (see GorillaHR/backend/app/seed.py and
/// Recruitment.Gorilla/server/.../Program.cs for where these literal
/// passwords come from). A real cutover dry-run needs a manifest of actual
/// known-password test accounts instead — this default is not that.
///
/// "iam-dryrun-test@example.com" is not a seeded account at all: it's a
/// synthetic row inserted directly into local RecruitmentGorilla.Users with
/// RG's real PBKDF2 hash format, specifically so the dry-run has at least
/// one genuine PBKDF2-sourced hash to verify — none of RG's actual seeded
/// credentials have a known plaintext (Auth:PasswordHash is set from user
/// secrets, not literal source).
/// </summary>
public static class DryRunManifest
{
    public static readonly IReadOnlyDictionary<string, string> Default = new Dictionary<string, string>
    {
        ["admin@gorillahr.com"] = "Admin@123",
        ["hr@gorillahr.com"] = "Hr@123",
        ["manager@gorillahr.com"] = "Manager@123",
        ["emp1@gorillahr.com"] = "Emp@123",
        ["emp2@gorillahr.com"] = "Emp@123",
        ["emp3@gorillahr.com"] = "Emp@123",
        ["iam-dryrun-test@example.com"] = "DryRun@123",
    };
}
