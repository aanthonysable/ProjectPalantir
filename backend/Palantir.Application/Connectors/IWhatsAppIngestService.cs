namespace Palantir.Application.Connectors;

public interface IWhatsAppIngestService
{
    WhatsAppBridgeStatusDto GetStatus();

    Task<WhatsAppIngestResult> IngestWahaEventAsync(
        string rawJson,
        CancellationToken cancellationToken = default);

    /// <summary>Remove duplicate WhatsApp messages (same provider id or same body/time).</summary>
    Task<int> DedupeStoredMessagesAsync(CancellationToken cancellationToken = default);
}

public sealed record WhatsAppBridgeStatusDto(
    bool Enabled,
    bool Configured,
    string InstanceName,
    string Detail,
    int WhatsAppConversationCount,
    int WhatsAppMessageCount);

public sealed record WhatsAppIngestResult(
    bool Accepted,
    string Outcome,
    Guid? ConversationId = null,
    Guid? MessageId = null);
