using Microsoft.EntityFrameworkCore;
using Palantir.Application.Abstractions;
using Palantir.Domain.Entities;

namespace Palantir.Infrastructure.Persistence;

public sealed class PalantirDbContext : DbContext, IPalantirDbContext
{
    public PalantirDbContext(DbContextOptions<PalantirDbContext> options) : base(options)
    {
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<ConnectedAccount> ConnectedAccounts => Set<ConnectedAccount>();
    public DbSet<OAuthGrant> OAuthGrants => Set<OAuthGrant>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<WorkflowAction> WorkflowActions => Set<WorkflowAction>();
    public DbSet<TaskItem> TaskItems => Set<TaskItem>();
    public DbSet<Connector> Connectors => Set<Connector>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    IQueryable<Organization> IPalantirDbContext.Organizations => Organizations;
    IQueryable<User> IPalantirDbContext.Users => Users;
    IQueryable<Conversation> IPalantirDbContext.Conversations => Conversations;
    IQueryable<Message> IPalantirDbContext.Messages => Messages;
    IQueryable<Draft> IPalantirDbContext.Drafts => Drafts;
    IQueryable<ApprovalRequest> IPalantirDbContext.ApprovalRequests => ApprovalRequests;
    IQueryable<AuditEvent> IPalantirDbContext.AuditEvents => AuditEvents;
    IQueryable<WorkflowAction> IPalantirDbContext.WorkflowActions => WorkflowActions;
    IQueryable<TaskItem> IPalantirDbContext.TaskItems => TaskItems;

    public new void Add<TEntity>(TEntity entity) where TEntity : class => base.Add(entity);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PalantirDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
