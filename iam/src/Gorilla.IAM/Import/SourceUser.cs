using Gorilla.IAM.Data.Entities;

namespace Gorilla.IAM.Import;

/// <summary>
/// One user row as read from HR's or RG's own database — the input to
/// <see cref="ImportPlanner"/>. <see cref="Algorithm"/> is fixed per source
/// (HR always bcrypt, RG always PBKDF2-SHA256), not inferred from the hash
/// string, so a malformed hash can't silently get misclassified.
/// </summary>
/// <param name="Roles">The app roles this user holds in its own source system,
/// verbatim — RG's UserRoles rows today. Optional, and left empty for HR: HR
/// isn't an IAM consumer until P3, so importing hr grants now would write data
/// ahead of the code that reads it. Never expanded along a role hierarchy: RG's
/// SuperAdmin/Admin/Recruiter/Interviewer ordering lives only in its policy
/// strings (Auth/Roles.cs), never in stored rows, so a verbatim copy is the
/// faithful projection and expanding would grant access RG never granted.</param>
public record SourceUser(
    string Email,
    string Name,
    bool Active,
    string PasswordHash,
    CredentialAlgorithm Algorithm,
    IReadOnlyList<string>? Roles = null);
