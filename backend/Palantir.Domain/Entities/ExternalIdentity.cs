namespace Palantir.Domain.Entities;

public class ExternalIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string? ProviderTenantId { get; set; }
    public string ProviderSubjectId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool IsLoginEnabled { get; set; } = true;
    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastVerifiedAt { get; set; }

    public User? User { get; set; }
}
