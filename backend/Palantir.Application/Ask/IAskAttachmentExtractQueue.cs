namespace Palantir.Application.Ask;

public sealed record AskAttachmentExtractJob(Guid OrganizationId, Guid AttachmentId);

public interface IAskAttachmentExtractQueue
{
    ValueTask EnqueueAsync(AskAttachmentExtractJob job, CancellationToken cancellationToken = default);
}
