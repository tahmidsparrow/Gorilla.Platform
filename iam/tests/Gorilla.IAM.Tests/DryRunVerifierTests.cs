using Gorilla.IAM.Auth;
using Gorilla.IAM.Data.Entities;
using Gorilla.IAM.Import;

namespace Gorilla.IAM.Tests;

public class DryRunVerifierTests
{
    [Fact]
    public void An_email_with_no_matching_plan_is_reported_NotPlanned()
    {
        var results = DryRunVerifier.Verify([], new Dictionary<string, string> { ["nobody@example.com"] = "x" });

        Assert.Equal(DryRunOutcome.NotPlanned, Assert.Single(results).Outcome);
    }

    [Fact]
    public void A_correct_bcrypt_password_verifies()
    {
        var plan = new SubjectImportPlan(
            "a@example.com", "A", true, CredentialAlgorithm.Bcrypt, BcryptPasswordHasher.Hash("s3cret!"), ImportSource.HrOnly);

        var results = DryRunVerifier.Verify([plan], new Dictionary<string, string> { ["a@example.com"] = "s3cret!" });

        Assert.Equal(DryRunOutcome.Verified, Assert.Single(results).Outcome);
    }

    /// <summary>The scenario the dry-run exists to catch: a manifested password
    /// that does NOT verify against the planned hash. This must report Failed,
    /// not throw, not silently pass — this is the one result that means "do
    /// not run the real import."</summary>
    [Fact]
    public void A_wrong_password_is_reported_Failed_not_thrown()
    {
        var plan = new SubjectImportPlan(
            "a@example.com", "A", true, CredentialAlgorithm.Bcrypt, BcryptPasswordHasher.Hash("s3cret!"), ImportSource.HrOnly);

        var results = DryRunVerifier.Verify([plan], new Dictionary<string, string> { ["a@example.com"] = "wrong" });

        Assert.Equal(DryRunOutcome.Failed, Assert.Single(results).Outcome);
    }

    [Fact]
    public void A_correct_pbkdf2_password_verifies_and_reports_the_rehash()
    {
        var plan = new SubjectImportPlan(
            "a@example.com", "A", true, CredentialAlgorithm.Pbkdf2Sha256, Pbkdf2PasswordHasher.Hash("s3cret!"), ImportSource.RgOnly);

        var results = DryRunVerifier.Verify([plan], new Dictionary<string, string> { ["a@example.com"] = "s3cret!" });

        Assert.Equal(DryRunOutcome.VerifiedAndRehashed, Assert.Single(results).Outcome);
    }

    [Fact]
    public void Manifest_email_matching_is_case_and_whitespace_insensitive_like_the_planner()
    {
        var plan = new SubjectImportPlan(
            "a@example.com", "A", true, CredentialAlgorithm.Bcrypt, BcryptPasswordHasher.Hash("s3cret!"), ImportSource.HrOnly);

        var results = DryRunVerifier.Verify([plan], new Dictionary<string, string> { [" A@Example.com "] = "s3cret!" });

        Assert.Equal(DryRunOutcome.Verified, Assert.Single(results).Outcome);
    }

    [Fact]
    public void Never_touches_the_plans_credential_data_itself()
    {
        // The dry-run must not have side effects on the plan it was given —
        // it builds a throwaway Credential internally, never mutates the input.
        var originalHash = Pbkdf2PasswordHasher.Hash("s3cret!");
        var plan = new SubjectImportPlan("a@example.com", "A", true, CredentialAlgorithm.Pbkdf2Sha256, originalHash, ImportSource.RgOnly);

        DryRunVerifier.Verify([plan], new Dictionary<string, string> { ["a@example.com"] = "s3cret!" });

        Assert.Equal(originalHash, plan.PasswordHash);
        Assert.Equal(CredentialAlgorithm.Pbkdf2Sha256, plan.Algorithm);
    }
}
