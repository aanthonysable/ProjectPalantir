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
    DateTimeOffset UpdatedAt,
    string MailboxKind = "Work");

public sealed record AuthorizeMicrosoftResult(string AuthorizationUrl, string State);

public sealed record OutlookMailAttachmentDto(
    string Id,
    string Name,
    string ContentType,
    long Size,
    bool IsInline,
    byte[]? ContentBytes);

public sealed record OutlookCalendarEventDto(
    string Id,
    string? Subject,
    string? Organizer,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? Location,
    bool IsAllDay,
    string? WebLink);

public sealed record TeamsChatDto(
    string Id,
    string? Topic,
    string ChatType,
    DateTimeOffset? LastUpdated,
    string? LastPreview,
    string? WebUrl);

public sealed record TeamsChatMessageDto(
    string Id,
    string ChatId,
    string? From,
    string? Body,
    DateTimeOffset? CreatedAt);

public sealed record OutlookMessageDto(
    string Id,
    string? Subject,
    string? From,
    string? Preview,
    DateTimeOffset? ReceivedAt,
    bool IsRead,
    string? GraphConversationId = null,
    string? BodyText = null,
    bool HasAttachments = false,
    IReadOnlyList<string>? ToAddresses = null,
    IReadOnlyList<string>? CcAddresses = null);

public interface IMicrosoftGraphConnectorService
{
    Task<AuthorizeMicrosoftResult> BeginAuthorizeAsync(
        Guid userId,
        Guid organizationId,
        string mailboxKind = "Work",
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

    Task<ConnectedAccountDto> UpdateMailboxKindAsync(
        Guid connectedAccountId,
        Guid userId,
        string mailboxKind,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutlookMessageDto>> ListMailAsync(
        Guid connectedAccountId,
        Guid userId,
        int top = 20,
        CancellationToken cancellationToken = default);

    Task<OutlookMessageDto?> GetMailMessageAsync(
        Guid connectedAccountId,
        Guid userId,
        string providerMessageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutlookMailAttachmentDto>> ListMailAttachmentsAsync(
        Guid connectedAccountId,
        Guid userId,
        string providerMessageId,
        CancellationToken cancellationToken = default);

    Task SendMailAsync(
        Guid connectedAccountId,
        Guid userId,
        string toAddress,
        string subject,
        string body,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutlookCalendarEventDto>> ListCalendarEventsAsync(
        Guid connectedAccountId,
        Guid userId,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        int top = 25,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamsChatDto>> ListTeamsChatsAsync(
        Guid connectedAccountId,
        Guid userId,
        int top = 20,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamsChatMessageDto>> ListTeamsChatMessagesAsync(
        Guid connectedAccountId,
        Guid userId,
        string chatId,
        int top = 25,
        CancellationToken cancellationToken = default);
}
