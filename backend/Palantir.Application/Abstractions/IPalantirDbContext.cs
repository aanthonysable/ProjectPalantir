using Palantir.Domain.Entities;

namespace Palantir.Application.Abstractions;

public interface IPalantirDbContext
{
    IQueryable<Organization> Organizations { get; }
    IQueryable<User> Users { get; }
    IQueryable<Conversation> Conversations { get; }
    IQueryable<Message> Messages { get; }
    IQueryable<Draft> Drafts { get; }
    IQueryable<ApprovalRequest> ApprovalRequests { get; }
    IQueryable<AuditEvent> AuditEvents { get; }
    IQueryable<WorkflowAction> WorkflowActions { get; }
    IQueryable<TaskItem> TaskItems { get; }
    IQueryable<ConnectedAccount> ConnectedAccounts { get; }
    IQueryable<OAuthGrant> OAuthGrants { get; }

    void Add<TEntity>(TEntity entity) where TEntity : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
