using Palantir.Domain.Enums;

namespace Palantir.Domain.Entities;

public class ConnectedAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ProviderTenantId { get; set; }
    public string ProviderAccountId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? PrimaryAddress { get; set; }
    /// <summary>Work or Personal — how this mailbox is treated in the assistant.</summary>
    public string MailboxKind { get; set; } = "Work";
    public ConnectionStatus ConnectionStatus { get; set; } = ConnectionStatus.NotConnected;
    public string? GrantedScopesJson { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? User { get; set; }
    public ICollection<OAuthGrant> OAuthGrants { get; set; } = new List<OAuthGrant>();
}
