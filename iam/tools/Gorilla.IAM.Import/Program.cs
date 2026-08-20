using Gorilla.IAM.Data;
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
var result2 = await SubjectImporter.ApplyAsync(db, plans);

Console.WriteLine($"Done. Created {result2.Created}, updated {result2.Updated}, unchanged {result2.Unchanged}.");
return 0;
