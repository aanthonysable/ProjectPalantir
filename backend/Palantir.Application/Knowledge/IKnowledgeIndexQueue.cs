namespace Palantir.Application.Knowledge;

public sealed record KnowledgeIndexJob(Guid OrganizationId, Guid DocumentId);

public interface IKnowledgeIndexQueue
{
    ValueTask EnqueueAsync(KnowledgeIndexJob job, CancellationToken cancellationToken = default);

    IAsyncEnumerable<KnowledgeIndexJob> DequeueAllAsync(CancellationToken cancellationToken);
}
