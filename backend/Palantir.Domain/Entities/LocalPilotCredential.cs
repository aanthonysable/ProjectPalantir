namespace Palantir.Domain.Entities;

/// <summary>
/// Local email/password credentials for the standalone pilot IdP.
/// Replaced later by Entra External ID / ChatGPT workspace linking without changing User.Id.
/// </summary>
public class LocalPilotCredential
{
    public Guid UserId { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? User { get; set; }
}
