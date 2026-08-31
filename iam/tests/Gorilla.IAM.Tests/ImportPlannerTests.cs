using Gorilla.IAM.Data.Entities;
using Gorilla.IAM.Import;

namespace Gorilla.IAM.Tests;

public class ImportPlannerTests
{
    private static SourceUser Hr(string email, string name = "HR Name", bool active = true, string hash = "hr-hash") =>
        new(email, name, active, hash, CredentialAlgorithm.Bcrypt);

    private static SourceUser Rg(
        string email, string name = "RG Name", bool active = true, string hash = "rg-hash", params string[] roles) =>
        new(email, name, active, hash, CredentialAlgorithm.Pbkdf2Sha256, roles);

    [Fact]
    public void HR_only_user_imports_as_HrOnly_with_bcrypt()
    {
        var plans = ImportPlanner.Plan([Hr("only-hr@example.com")], []);

        var plan = Assert.Single(plans);
        Assert.Equal(ImportSource.HrOnly, plan.Source);
        Assert.Equal(CredentialAlgorithm.Bcrypt, plan.Algorithm);
        Assert.Equal("hr-hash", plan.PasswordHash);
    }

    [Fact]
    public void RG_only_user_imports_as_RgOnly_with_pbkdf2()
    {
        var plans = ImportPlanner.Plan([], [Rg("only-rg@example.com")]);

        var plan = Assert.Single(plans);
        Assert.Equal(ImportSource.RgOnly, plan.Source);
        Assert.Equal(CredentialAlgorithm.Pbkdf2Sha256, plan.Algorithm);
        Assert.Equal("rg-hash", plan.PasswordHash);
    }

    /// <summary>Spec section 3.4's one unsolved case: HR's credential always wins.</summary>
    [Fact]
    public void A_person_in_both_systems_imports_HRs_credential_not_RGs()
    {
        var plans = ImportPlanner.Plan(
            [Hr("both@example.com", hash: "hr-hash")],
            [Rg("both@example.com", hash: "rg-hash")]);

        var plan = Assert.Single(plans);
        Assert.Equal(ImportSource.Both, plan.Source);
        Assert.Equal(CredentialAlgorithm.Bcrypt, plan.Algorithm);
        Assert.Equal("hr-hash", plan.PasswordHash);
    }

    [Fact]
    public void A_person_in_both_systems_takes_HRs_name_and_active_status_too()
    {
        var plans = ImportPlanner.Plan(
            [Hr("both@example.com", name: "HR Says This Name", active: false)],
            [Rg("both@example.com", name: "RG Says That Name", active: true)]);

        var plan = Assert.Single(plans);
        Assert.Equal("HR Says This Name", plan.Name);
        Assert.False(plan.Active);
    }

    [Fact]
    public void Matches_by_email_ignoring_case_and_whitespace()
    {
        var plans = ImportPlanner.Plan(
            [Hr(" Admin@Example.com ")],
            [Rg("admin@example.com")]);

        var plan = Assert.Single(plans);
        Assert.Equal(ImportSource.Both, plan.Source);
    }

    [Fact]
    public void Falls_back_to_RGs_name_then_the_email_when_HRs_name_is_blank()
    {
        var withRgName = Assert.Single(ImportPlanner.Plan(
            [Hr("a@example.com", name: "  ")],
            [Rg("a@example.com", name: "RG Name")]));
        Assert.Equal("RG Name", withRgName.Name);

        var withNeitherName = Assert.Single(ImportPlanner.Plan(
            [Hr("b@example.com", name: "")],
            []));
        Assert.Equal("b@example.com", withNeitherName.Name);
    }

    [Fact]
    public void Every_email_is_planned_exactly_once()
    {
        var plans = ImportPlanner.Plan(
            [Hr("a@example.com"), Hr("shared@example.com")],
            [Rg("shared@example.com"), Rg("c@example.com")]);

        Assert.Equal(3, plans.Count);
        Assert.Equal(
            new HashSet<string> { "a@example.com", "shared@example.com", "c@example.com" },
            plans.Select(p => p.Email).ToHashSet());
    }

    /// <summary>The load-bearing one: HR wins the credential for someone in both
    /// systems, but that says nothing about what they may do in Recruitment — a
    /// naive `winner.Roles` would silently drop every ATS role for exactly the
    /// people most likely to have them.</summary>
    [Fact]
    public void RG_roles_survive_when_HR_wins_the_credential()
    {
        var plans = ImportPlanner.Plan(
            [Hr("both@example.com")],
            [Rg("both@example.com", roles: ["Recruiter"])]);

        var plan = Assert.Single(plans);
        Assert.Equal(ImportSource.Both, plan.Source);
        Assert.Equal("hr-hash", plan.PasswordHash);          // HR still won the credential
        Assert.Equal(["Recruiter"], plan.AtsRoles);           // ...and the RG role still survived
    }

    [Fact]
    public void All_of_a_multi_role_users_roles_are_planned_not_just_one()
    {
        var plans = ImportPlanner.Plan([], [Rg("multi@example.com", roles: ["Recruiter", "Interviewer"])]);

        Assert.Equal(["Recruiter", "Interviewer"], Assert.Single(plans).AtsRoles);
    }

    [Fact]
    public void An_HR_only_user_is_planned_with_no_ats_roles()
    {
        var plans = ImportPlanner.Plan([Hr("only-hr@example.com")], []);

        Assert.Empty(Assert.Single(plans).AtsRoles!);
    }

    [Fact]
    public void An_RG_user_with_no_roles_is_planned_with_none_rather_than_failing()
    {
        var plans = ImportPlanner.Plan([], [Rg("roleless@example.com")]);

        Assert.Empty(Assert.Single(plans).AtsRoles!);
    }
}
