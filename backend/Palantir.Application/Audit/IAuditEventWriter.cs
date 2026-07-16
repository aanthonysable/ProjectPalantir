namespace Palantir.Application.Audit;

public interface IAuditEventWriter
{
    Task WriteAsync(
        Guid organizationId,
        string eventType,
        Guid? actorUserId = null,
        string? entityType = null,
        Guid? entityId = null,
        string? detailsJson = null,
        CancellationToken cancellationToken = default);
}
