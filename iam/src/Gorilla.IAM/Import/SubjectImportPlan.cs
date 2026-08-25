using Gorilla.IAM.Data.Entities;

namespace Gorilla.IAM.Import;

public enum ImportSource
{
    HrOnly,
    RgOnly,
    Both,
}

/// <param name="AtsRoles">The "ats" role grants to create for this subject,
/// taken from RG's UserRoles verbatim. Deliberately sourced from the RG row even
/// when <see cref="Source"/> is <see cref="ImportSource.Both"/> and HR won the
/// credential: HR winning is about which password verifies, and says nothing
/// about what someone may do in Recruitment. Empty for an HR-only subject.</param>
public record SubjectImportPlan(
    string Email,
    string Name,
    bool Active,
    CredentialAlgorithm Algorithm,
    string PasswordHash,
    ImportSource Source,
    IReadOnlyList<string>? AtsRoles = null);
