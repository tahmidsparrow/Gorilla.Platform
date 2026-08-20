using Gorilla.IAM.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Data.Seeding;

/// <summary>
/// Closes the break-glass console's bootstrap problem: the console can only
/// grant iam:admin to someone through a form that itself requires
/// iam:admin to reach. Matches Recruitment.Gorilla's own exact pattern
/// (Program.cs: seed the first Super Admin "if !await db.Users.AnyAsync()",
/// email from Auth:SeedAdminEmail) — here, "if no one holds iam:admin yet,
/// grant it to the configured email" instead of creating a whole new user.
/// The subject must already exist (e.g. via the import tool); this only
/// grants the role, it does not create people.
/// </summary>
public static class BootstrapAdminSeeder
{
    public static async Task<string?> SeedAsync(IamDbContext db, string? bootstrapAdminEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bootstrapAdminEmail))
            return "Iam:BootstrapAdminEmail is not configured — no bootstrap admin grant was made.";

        if (await db.RoleGrants.AnyAsync(g => g.AppKey == IamSelfConsumerApp.AppKey && g.Role == IamSelfConsumerApp.AdminRole, ct))
            return null; // someone already holds it — never overwrite an operator's own grant decisions

        var email = bootstrapAdminEmail.Trim().ToLowerInvariant();
        var subject = await db.Subjects.SingleOrDefaultAsync(s => s.Email == email, ct);
        if (subject is null)
            return $"Iam:BootstrapAdminEmail is set to '{email}' but no such subject exists yet " +
                "(run the import tool first, or create the subject some other way).";

        db.RoleGrants.Add(new RoleGrant { SubjectId = subject.Id, AppKey = IamSelfConsumerApp.AppKey, Role = IamSelfConsumerApp.AdminRole });
        await db.SaveChangesAsync(ct);
        return null;
    }
}
