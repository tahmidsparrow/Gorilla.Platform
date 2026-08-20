using Gorilla.IAM.Data.Seeding;

namespace Gorilla.IAM.Tests;

/// <summary>
/// Pins the seed data against silent drift from its two upstream sources.
/// There is no build-time link back to GorillaHR's RoleName enum or RG's
/// Roles.cs (spec section 2's "copied, not referenced" — see the comment on
/// ConsumerAppSeedData), so nothing else would ever catch one of these lists
/// going stale after a role is renamed or added in either app.
/// </summary>
public class ConsumerAppSeedDataTests
{
    [Fact]
    public void Defines_exactly_hr_and_ats()
    {
        Assert.Equal(["hr", "ats"], ConsumerAppSeedData.Apps.Select(a => a.Key));
    }

    [Fact]
    public void HRs_role_vocabulary_matches_backend_app_models_enums_py_RoleName()
    {
        var hr = ConsumerAppSeedData.Apps.Single(a => a.Key == "hr");
        Assert.Equal(["Employee", "Line Manager", "HR", "Admin"], hr.Roles);
    }

    [Fact]
    public void ATSs_role_vocabulary_matches_server_Auth_Roles_cs()
    {
        var ats = ConsumerAppSeedData.Apps.Single(a => a.Key == "ats");
        Assert.Equal(["SuperAdmin", "Admin", "Recruiter", "Interviewer"], ats.Roles);
    }
}
