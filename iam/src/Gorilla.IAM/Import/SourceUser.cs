using Gorilla.IAM.Data.Entities;

namespace Gorilla.IAM.Import;

/// <summary>
/// One user row as read from HR's or RG's own database — the input to
/// <see cref="ImportPlanner"/>. <see cref="Algorithm"/> is fixed per source
/// (HR always bcrypt, RG always PBKDF2-SHA256), not inferred from the hash
/// string, so a malformed hash can't silently get misclassified.
/// </summary>
public record SourceUser(string Email, string Name, bool Active, string PasswordHash, CredentialAlgorithm Algorithm);
