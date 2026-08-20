using Gorilla.IAM.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gorilla.IAM.Data.Seeding;

/// <summary>
/// Idempotent startup seeding — matches Recruitment.Gorilla's own pattern
/// (Program.cs: seed the first Super Admin "if !await db.Users.AnyAsync()").
/// Checked per-app, not once for the whole table, so adding a third app later
/// seeds just that one row without re-touching "hr" or "ats".
/// </summary>
public static class ConsumerAppSeeder
{
    public static async Task SeedAsync(IamDbContext db, CancellationToken ct = default)
    {
        foreach (var (key, name, roles) in ConsumerAppSeedData.Apps)
        {
            if (await db.ConsumerApps.AnyAsync(a => a.Key == key, ct))
                continue;

            db.ConsumerApps.Add(new ConsumerApp
            {
                Key = key,
                Name = name,
                Roles = roles.Select(role => new ConsumerAppRole { AppKey = key, Role = role }).ToList(),
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
