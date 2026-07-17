namespace Palantir.Application.Ai;

public sealed record AiContentPart(
    string Type,
    string? Text = null,
    string? ImageUrl = null);

/// <param name="Content">Plain text content when <paramref name="Parts"/> is null.</param>
/// <param name="Parts">Optional multimodal parts (text + image_url) for vision models.</param>
public sealed record AiChatMessage(
    string Role,
    string Content,
    IReadOnlyList<AiContentPart>? Parts = null);

public sealed record AiProviderStatusDto(
    string Name,
    string Provider,
    string Model,
    bool Configured,
    string? Detail);

public sealed record AiRoutingStatusDto(
    string Task,
    string ProviderName,
    string Provider,
    string Model,
    bool Configured);

public sealed record AiStatusDto(
    bool AnyConfigured,
    IReadOnlyList<AiProviderStatusDto> Providers,
    IReadOnlyList<AiRoutingStatusDto> Tasks);

public interface IAiCompletionClient
{
    bool IsConfigured { get; }

    bool IsConfiguredFor(AiTaskKind task);

    AiStatusDto GetStatus();

    Task<string> CompleteAsync(
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken = default);

    Task<string> CompleteAsync(
        AiTaskKind task,
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken = default);
}
