namespace Gorilla.IAM.Data.Seeding;

/// <summary>
/// The two consumer apps that exist today and their role vocabularies (spec
/// section 3.1's table: "hr: Employee/Line Manager/HR/Admin; ats: SuperAdmin/
/// Admin/Recruiter/Interviewer"). Nothing in this service can validate a role
/// grant until these rows exist — every RoleGrant FKs to a ConsumerAppRole
/// (see IamDbContext).
///
/// Copied, not referenced, from each app's own source of truth — the same
/// choice spec section 2 makes for RG's PasswordHasher.cs, for the same
/// reason: a real cross-repo build dependency is worse than values that can
/// drift and need a human to notice. If either app adds or renames a role,
/// this list goes stale until someone updates it by hand. The pinned source
/// spellings below are exact string matches with those files as of this
/// writing:
///
///   HR:  GorillaHR/backend/app/models/enums.py — class RoleName
///   ATS: Recruitment.Gorilla/server/Recruitment.Gorilla.API/Auth/Roles.cs
/// </summary>
public static class ConsumerAppSeedData
{
    public static readonly IReadOnlyList<(string Key, string Name, string[] Roles)> Apps =
    [
        ("hr", "GorillaHR", ["Employee", "Line Manager", "HR", "Admin"]),
        ("ats", "Recruitment.Gorilla", ["SuperAdmin", "Admin", "Recruiter", "Interviewer"]),
    ];
}
