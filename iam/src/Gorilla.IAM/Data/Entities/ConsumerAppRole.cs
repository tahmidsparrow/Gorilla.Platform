namespace Gorilla.IAM.Data.Entities;

/// <summary>One entry in an app's role vocabulary — e.g. ("hr", "Admin").</summary>
public class ConsumerAppRole
{
    public int Id { get; set; }

    public string AppKey { get; set; } = string.Empty;
    public ConsumerApp App { get; set; } = null!;

    public string Role { get; set; } = string.Empty;
}
