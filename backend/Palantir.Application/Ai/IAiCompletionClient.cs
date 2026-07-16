namespace Palantir.Application.Ai;

public sealed record AiChatMessage(string Role, string Content);

public interface IAiCompletionClient
{
    bool IsConfigured { get; }

    Task<string> CompleteAsync(
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken = default);
}
