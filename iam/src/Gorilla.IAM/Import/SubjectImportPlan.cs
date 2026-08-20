using Gorilla.IAM.Data.Entities;

namespace Gorilla.IAM.Import;

/// <summary>Which source system(s) a planned subject was matched from — purely
/// informational (dry-run/apply reporting), no behavior depends on it.</summary>
public enum ImportSource
{
    HrOnly,
    RgOnly,
    Both,
}

/// <summary>
/// One subject as <see cref="ImportPlanner"/> decided to import it — the
/// output of the pure merge, and the input to both the dry-run verifier and
/// the real database upsert. Never touches a database itself.
/// </summary>
public record SubjectImportPlan(
    string Email,
    string Name,
    bool Active,
    CredentialAlgorithm Algorithm,
    string PasswordHash,
    ImportSource Source);
