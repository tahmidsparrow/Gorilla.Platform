using Gorilla.IAM.Data;
using Gorilla.IAM.Data.Seeding;
using Gorilla.IAM.Import;
using Gorilla.IAM.ImportTool;
using Microsoft.EntityFrameworkCore;

if (args.Length != 1 || (args[0] != "dry-run" && args[0] != "apply"))
{
    Console.Error.WriteLine("Usage: dotnet run -- dry-run   (report only, no writes)");
    Console.Error.WriteLine("       dotnet run -- apply     (writes to gorilla_iam)");
    return 2;
}

Console.WriteLine("Reading GorillaHR and Recruitment.Gorilla user tables (read-only)...");
var hrUsers = await SourceReaders.FetchHrUsersAsync();
var rgUsers = await SourceReaders.FetchRgUsersAsync();
Console.WriteLine($"  HR: {hrUsers.Count} users. RG: {rgUsers.Count} users.");

var plans = ImportPlanner.Plan(hrUsers, rgUsers);
Console.WriteLine($"  Planned {plans.Count} subjects "
    + $"({plans.Count(p => p.Source == ImportSource.Both)} in both systems, "
    + $"{plans.Count(p => p.Source == ImportSource.HrOnly)} HR-only, "
    + $"{plans.Count(p => p.Source == ImportSource.RgOnly)} RG-only).");

Console.WriteLine();
Console.WriteLine("Dry run: replaying known passwords against the planned import"
    + " (spec section 9's go/no-go safety net — this NEVER writes to a database).");
var dryRun = DryRunVerifier.Verify(plans, DryRunManifest.Default);
var failed = 0;
foreach (var result in dryRun)
{
    var symbol = result.Outcome switch
    {
        DryRunOutcome.Verified => "OK    ",
        DryRunOutcome.VerifiedAndRehashed => "OK    ",
        DryRunOutcome.NotPlanned => "SKIP  ",
        DryRunOutcome.Failed => "FAILED",
        _ => "?     ",
    };
    if (result.Outcome == DryRunOutcome.Failed) failed++;
    Console.WriteLine($"  [{symbol}] {result.Email} - {result.Outcome}");
}

// The same go/no-go discipline the credential replay gets, applied to roles:
// a role string RG has that IAM's "ats" vocabulary doesn't would be silently
// rejected at apply time and that person would end up with fewer grants than
// they should have. Validated against ConsumerAppSeedData rather than the
// database because a dry run deliberately never connects to gorilla_iam — and
// that list is the same one the DB's consumer_app_roles rows are seeded from.
var atsRoles = ConsumerAppSeedData.Apps
    .Single(a => a.Key == RoleGrantImporter.AtsAppKey).Roles
    .ToHashSet(StringComparer.Ordinal);

var unknownRoles = plans
    .SelectMany(p => (p.AtsRoles ?? []).Select(r => (p.Email, Role: r)))
    .Where(x => !atsRoles.Contains(x.Role))
    .ToList();

var plannedGrants = plans.Sum(p => (p.AtsRoles ?? []).Count);
Console.WriteLine();
Console.WriteLine($"Planned {plannedGrants} \"{RoleGrantImporter.AtsAppKey}\" role grant(s) across "
    + $"{plans.Count(p => (p.AtsRoles ?? []).Count > 0)} subject(s).");

foreach (var (email, role) in unknownRoles)
    Console.Error.WriteLine($"  [FAILED] {email} - role \"{role}\" is not in the "
        + $"\"{RoleGrantImporter.AtsAppKey}\" vocabulary ({string.Join(", ", atsRoles)}).");

if (unknownRoles.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{unknownRoles.Count} role(s) are not grantable. Refusing to proceed —"
        + " add them to ConsumerAppSeedData (and re-seed) or correct them in Recruitment.Gorilla first.");
    return 1;
}

if (failed > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{failed} of {dryRun.Count} dry-run checks FAILED. Refusing to proceed"
        + " — this means an imported hash would not verify against a password known to be correct,"
        + " which is exactly what section 9's dry-run gate exists to catch before it costs someone"
        + " their real login.");
    return 1;
}

Console.WriteLine();
Console.WriteLine($"All {dryRun.Count} dry-run checks passed.");

if (args[0] == "dry-run")
{
    Console.WriteLine("Dry run only — no database was written. Re-run with 'apply' to import for real.");
    return 0;
}

Console.WriteLine();
Console.WriteLine("Applying to gorilla_iam...");

var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? throw new InvalidOperationException("Missing required environment variable: ConnectionStrings__DefaultConnection");
var options = new DbContextOptionsBuilder<IamDbContext>()
    .UseMySql(connStr, new MySqlServerVersion(new Version(9, 0, 0)))
    .Options;

await using var db = new IamDbContext(options);

// The web app seeds these on boot; this CLI doesn't run that path. Without the
// "ats" row, role_grants' FK to consumer_apps refuses every grant (and
// GrantRoleAsync would report every role as unknown), so a fresh database would
// import subjects and silently no grants. Idempotent, so running it here is free.
await ConsumerAppSeeder.SeedAsync(db);

var result2 = await SubjectImporter.ApplyAsync(db, plans);
Console.WriteLine($"Subjects: created {result2.Created}, updated {result2.Updated}, unchanged {result2.Unchanged}.");

var grants = await RoleGrantImporter.ApplyAsync(db, plans);
Console.WriteLine($"Role grants: granted {grants.Granted}, already granted {grants.AlreadyGranted}, rejected {grants.Rejected}.");

if (grants.Rejected > 0)
{
    Console.Error.WriteLine($"WARNING: {grants.Rejected} grant(s) were rejected as unknown app/role."
        + " Those people will be refused at /connect/authorize until it's corrected.");
    return 1;
}

Console.WriteLine("Done.");
return 0;
