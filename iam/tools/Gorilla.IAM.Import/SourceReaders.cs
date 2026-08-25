using Gorilla.IAM.Data.Entities;
using Gorilla.IAM.Import;
using MySqlConnector;

namespace Gorilla.IAM.ImportTool;

/// <summary>
/// Reads HR's and RG's user tables directly — read-only, SELECT-only, same
/// approach and the same environment-variable naming as
/// gorilla-platform/scripts/reconcile_users.py (GORILLAHR_DB_* /
/// RECRUITMENT_DB_*), so anyone who already ran the reconciliation script has
/// the connection details this needs too.
/// </summary>
public static class SourceReaders
{
    public static async Task<List<SourceUser>> FetchHrUsersAsync(CancellationToken ct = default)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Env("GORILLAHR_DB_HOST", "localhost"),
            Port = uint.Parse(Env("GORILLAHR_DB_PORT", "3306")),
            Database = Env("GORILLAHR_DB_NAME", "gorillahr"),
            UserID = Env("GORILLAHR_DB_USER", "gorillahr_app"),
            Password = RequireEnv("GORILLAHR_DB_PASSWORD"),
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        // A user's "active" is is_active AND (no employee row, or that
        // employee is ACTIVE) — matches reconcile_users.py's fetch_hr_users
        // exactly, for the same reason: a bare user with no employee row yet
        // is still a real, importable account.
        await using var cmd = new MySqlCommand(
            """
            SELECT u.email, COALESCE(e.full_name, ''), u.password_hash, u.is_active, e.status
            FROM users u
            LEFT JOIN employees e ON e.user_id = u.id
            """, conn);

        var users = new List<SourceUser>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var email = reader.GetString(0);
            var name = reader.GetString(1);
            var hash = reader.GetString(2);
            var isActive = reader.GetBoolean(3);
            var status = reader.IsDBNull(4) ? null : reader.GetString(4);

            var active = isActive && status is null or "ACTIVE";
            users.Add(new SourceUser(email, name, active, hash, CredentialAlgorithm.Bcrypt));
        }
        return users;
    }

    public static async Task<List<SourceUser>> FetchRgUsersAsync(CancellationToken ct = default)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Env("RECRUITMENT_DB_HOST", "localhost"),
            Port = uint.Parse(Env("RECRUITMENT_DB_PORT", "3306")),
            Database = Env("RECRUITMENT_DB_NAME", "RecruitmentGorilla"),
            UserID = Env("RECRUITMENT_DB_USER", "root"),
            Password = RequireEnv("RECRUITMENT_DB_PASSWORD"),
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        // LEFT JOIN, for the same reason HR's query left-joins employees: a user
        // with no role rows is still a real, importable account. Table/column
        // names are PascalCase because RG's AppDbContext sets no ToTable or
        // HasColumnName anywhere and registers no naming convention, so EF's
        // defaults (DbSet name, CLR property name) are what actually exist in
        // MySQL — verified against migration AddUsersRolesAndCandidateOwner.
        await using var cmd = new MySqlCommand(
            """
            SELECT u.Email, u.Name, u.PasswordHash, u.IsActive, ur.Role
            FROM Users u
            LEFT JOIN UserRoles ur ON ur.UserId = u.Id
            """, conn);

        // The join fans out one row per (user, role), so rows are grouped back
        // into one SourceUser per person rather than read one-to-one. Keyed by
        // the same normalization ImportPlanner matches on, so a casing
        // difference here can't split one person into two.
        var byEmail = new Dictionary<string, (SourceUser User, List<string> Roles)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var email = reader.GetString(0);
            var key = ImportPlanner.Normalize(email);

            if (!byEmail.TryGetValue(key, out var entry))
            {
                entry = (new SourceUser(
                    email,
                    reader.GetString(1),
                    reader.GetBoolean(3),
                    reader.GetString(2),
                    CredentialAlgorithm.Pbkdf2Sha256), []);
                byEmail[key] = entry;
            }

            if (!reader.IsDBNull(4))
                entry.Roles.Add(reader.GetString(4));
        }

        return byEmail.Values.Select(e => e.User with { Roles = e.Roles }).ToList();
    }

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing required environment variable: {name}");
}
