namespace Gorilla.IAM.Import;

/// <summary>
/// Pure merge logic — no database, no I/O — deciding what to import for
/// each person. Deliberately separated the same way reconcile_users.py
/// separates its reconcile() function from DB access: this is the part
/// correctness actually depends on, and it's what a unit test can check
/// directly.
/// </summary>
public static class ImportPlanner
{
    /// <summary>
    /// One row per person, matched by normalized email across both sources.
    /// Per spec section 3.4's one unsolved case — a person in both systems
    /// with different passwords — <b>HR's credential always wins</b>; never
    /// "accept either and converge." HR wins the name and active status too,
    /// for the same reason: one authority per subject, not values picked
    /// independently from whichever source happens to look better.
    /// </summary>
    public static IReadOnlyList<SubjectImportPlan> Plan(
        IEnumerable<SourceUser> hrUsers,
        IEnumerable<SourceUser> rgUsers)
    {
        var hrByEmail = hrUsers.ToDictionary(u => Normalize(u.Email));
        var rgByEmail = rgUsers.ToDictionary(u => Normalize(u.Email));

        var allEmails = hrByEmail.Keys.Union(rgByEmail.Keys);

        var plans = new List<SubjectImportPlan>();
        foreach (var email in allEmails)
        {
            var hr = hrByEmail.GetValueOrDefault(email);
            var rg = rgByEmail.GetValueOrDefault(email);

            var winner = hr ?? rg!; // one of the two is guaranteed non-null
            var name = !string.IsNullOrWhiteSpace(hr?.Name) ? hr!.Name
                : !string.IsNullOrWhiteSpace(rg?.Name) ? rg!.Name
                : email;

            var source = hr is not null && rg is not null ? ImportSource.Both
                : hr is not null ? ImportSource.HrOnly
                : ImportSource.RgOnly;

            plans.Add(new SubjectImportPlan(email, name, winner.Active, winner.Algorithm, winner.PasswordHash, source));
        }

        return plans;
    }

    /// <summary>Lowercase + trim, matching reconcile_users.py's normalize_email —
    /// same reasoning: casing/whitespace differences between the two apps'
    /// signup forms must not produce a false "these are different people."</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
