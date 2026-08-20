using Gorilla.IAM.Auth;
using Gorilla.IAM.Data.Entities;

namespace Gorilla.IAM.Import;

public enum DryRunOutcome
{
    /// <summary>No plan exists for this email — it isn't in either source
    /// system, or wasn't included in this planning run.</summary>
    NotPlanned,

    /// <summary>The manifested password did not verify against the planned hash —
    /// exactly the failure this dry-run exists to catch before a real import.</summary>
    Failed,

    Verified,

    /// <summary>Verified via the PBKDF2 path and rehashed to bcrypt — proves the
    /// rehash-on-verify path specifically, not just plain verification.</summary>
    VerifiedAndRehashed,
}

public record DryRunResult(string Email, DryRunOutcome Outcome);

/// <summary>
/// The dry-run itself: for each manifested (email, known password) pair,
/// builds the <i>exact</i> Credential a real import would write and runs it
/// through the <i>exact</i> <see cref="CredentialVerifier"/> production login
/// would use — never a reimplementation or a simplified stand-in. No
/// database is touched; this only proves the import pipeline, not that any
/// particular Credential row already exists.
/// </summary>
public static class DryRunVerifier
{
    public static IReadOnlyList<DryRunResult> Verify(
        IReadOnlyList<SubjectImportPlan> plans,
        IReadOnlyDictionary<string, string> manifest)
    {
        var byEmail = plans.ToDictionary(p => p.Email);

        var results = new List<DryRunResult>();
        foreach (var (email, password) in manifest)
        {
            var normalizedEmail = ImportPlanner.Normalize(email);
            if (!byEmail.TryGetValue(normalizedEmail, out var plan))
            {
                results.Add(new DryRunResult(email, DryRunOutcome.NotPlanned));
                continue;
            }

            var credential = new Credential { Algorithm = plan.Algorithm, Hash = plan.PasswordHash };
            var outcome = CredentialVerifier.Verify(credential, password) switch
            {
                CredentialVerifyResult.Rejected => DryRunOutcome.Failed,
                CredentialVerifyResult.Accepted => DryRunOutcome.Verified,
                CredentialVerifyResult.AcceptedAndRehashed => DryRunOutcome.VerifiedAndRehashed,
                _ => throw new InvalidOperationException("Unreachable"),
            };
            results.Add(new DryRunResult(email, outcome));
        }
        return results;
    }
}
