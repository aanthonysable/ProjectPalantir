namespace Palantir.Domain.Entities;

/// <summary>
/// Stores a Key Vault / credential-store reference — never raw OAuth tokens in this table.
/// </summary>
public class OAuthGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectedAccountId { get; set; }
    public string CredentialReference { get; set; } = string.Empty;
    public int TokenVersion { get; set; } = 1;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ConnectedAccount? ConnectedAccount { get; set; }
}
