namespace Gorilla.IAM.Data;

/// <summary>
/// IAM's own app_key/role for the break-glass console's access grant — the
/// single source of truth for what would otherwise be the same "iam"/"admin"
/// string pair duplicated across three places: <c>ConsumerAppSeedData</c>
/// (registers the vocabulary), <c>BreakGlassAuthenticator</c> (checks the
/// grant), and <c>BootstrapAdminSeeder</c> (grants it the first time). Lives
/// in the <c>Data</c> namespace, not <c>Console</c>, so the seeding layer
/// doesn't have to depend on the console feature to reference it.
/// </summary>
public static class IamSelfConsumerApp
{
    public const string AppKey = "iam";
    public const string AdminRole = "admin";
}
