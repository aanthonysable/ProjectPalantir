namespace Palantir.Application.Connectors;

public interface IWahaDirectoryClient
{
    bool IsConfigured { get; }

    /// <summary>Friendly subject for a group or chat id, when WAHA can resolve it.</summary>
    Task<string?> GetChatSubjectAsync(string chatId, CancellationToken cancellationToken = default);

    /// <summary>Map of group id → subject from WAHA Groups API.</summary>
    Task<IReadOnlyDictionary<string, string>> ListGroupSubjectsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort display label for a participant id (LID or phone JID),
    /// using group membership phone numbers from WAHA.
    /// </summary>
    Task<string?> ResolveParticipantLabelAsync(
        string participantId,
        CancellationToken cancellationToken = default);
}
