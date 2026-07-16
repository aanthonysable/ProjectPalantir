using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;

namespace Palantir.Infrastructure.Services;

public sealed class AuditEventWriter : IAuditEventWriter
{
    private readonly IPalantirDbContext _db;

    public AuditEventWriter(IPalantirDbContext db)
    {
        _db = db;
    }

    public async Task WriteAsync(
        Guid organizationId,
        string eventType,
        Guid? actorUserId = null,
        string? entityType = null,
        Guid? entityId = null,
        string? detailsJson = null,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
        {
            return;
        }

        _db.Add(new AuditEvent
        {
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            DetailsJson = detailsJson
        });

        await _db.SaveChangesAsync(cancellationToken);
    }
}
