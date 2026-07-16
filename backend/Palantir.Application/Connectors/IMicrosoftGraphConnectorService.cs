using Palantir.Domain.Enums;

namespace Palantir.Application.Connectors;

public sealed record ConnectedAccountDto(
    Guid Id,
    Guid UserId,
    string Provider,
    string? DisplayName,
    string? PrimaryAddress,
    ConnectionStatus ConnectionStatus,
    string? GrantedScopesJson,
    DateTimeOffset? LastSuccessfulSyncAt,
    DateTimeOffset UpdatedAt);

public sealed record AuthorizeMicrosoftResult(string AuthorizationUrl, string State);

public sealed record OutlookMessageDto(
    string Id,
    string? Subject,
    string? From,
    string? Preview,
    DateTimeOffset? ReceivedAt,
    bool IsRead,
    string? GraphConversationId = null,
    string? BodyText = null);

public interface IMicrosoftGraphConnectorService
{
    Task<AuthorizeMicrosoftResult> BeginAuthorizeAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<ConnectedAccountDto> CompleteAuthorizeAsync(
        string code,
        string state,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectedAccountDto>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<ConnectedAccountDto?> GetAsync(Guid connectedAccountId, CancellationToken cancellationToken = default);

    Task DisconnectAsync(Guid connectedAccountId, Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutlookMessageDto>> ListMailAsync(
        Guid connectedAccountId,
        Guid userId,
        int top = 20,
        CancellationToken cancellationToken = default);

    Task SendMailAsync(
        Guid connectedAccountId,
        Guid userId,
        string toAddress,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}
