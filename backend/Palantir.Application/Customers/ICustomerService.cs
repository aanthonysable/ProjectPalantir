namespace Palantir.Application.Customers;

public sealed record CustomerSummaryDto(
    Guid Id,
    string Name,
    int ContactCount,
    int ConversationCount,
    int OpenTaskCount,
    int WorkOrderCount,
    int QuoteCount,
    int RentalCount,
    int OrderCount,
    DateTimeOffset? LastActivityAt);

public sealed record ContactDto(
    Guid Id,
    Guid? CustomerId,
    string DisplayName,
    string? Email,
    string? Phone);

public sealed record CustomerActivityDto(
    string Kind,
    string Title,
    string? Detail,
    DateTimeOffset? OccurredAt,
    string? Url,
    Guid? ConversationId,
    string? SourceSystem);

public sealed record CustomerDetailDto(
    Guid Id,
    string Name,
    IReadOnlyList<ContactDto> Contacts,
    IReadOnlyList<CustomerActivityDto> Activity,
    int ConversationCount,
    int OpenTaskCount,
    int WorkOrderCount,
    int QuoteCount,
    int RentalCount,
    int OrderCount,
    string? CompanyOverview,
    DateTimeOffset? OverviewGeneratedAt);

public sealed record CustomerCompanyOverviewDto(
    Guid CustomerId,
    string Name,
    string Overview,
    DateTimeOffset GeneratedAt,
    bool FromCache,
    string? SourceNote);

public sealed record CustomerReconcileResult(
    int CustomersUpserted,
    int ContactsUpserted,
    int ConversationsLinked,
    IReadOnlyList<string> Notes);

public interface ICustomerService
{
    Task<IReadOnlyList<CustomerSummaryDto>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<CustomerDetailDto?> GetAsync(
        Guid organizationId,
        Guid customerId,
        CancellationToken cancellationToken = default);

    Task<CustomerSummaryDto> CreateAsync(
        Guid organizationId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuild customers from MaintainX / Monday / EZRentOut party names,
    /// purge junk non-ops rows, and link matching conversations by name.
    /// </summary>
    Task<CustomerReconcileResult> ReconcileAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// AI company briefing (website, size, industry, etc.) cached on the customer.
    /// </summary>
    Task<CustomerCompanyOverviewDto?> GetCompanyOverviewAsync(
        Guid organizationId,
        Guid customerId,
        bool refresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist CRM activity onto Customers.MetadataJson from the shared ops snapshot
    /// so all users get a SQL-backed cache without waiting on live connector pulls.
    /// </summary>
    Task<int> WarmFromSnapshotAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);
}