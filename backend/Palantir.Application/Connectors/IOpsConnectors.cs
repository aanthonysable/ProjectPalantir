namespace Palantir.Application.Connectors;

/// <summary>Normalized open work from MaintainX, EZRentOut, Monday, or future ERP.</summary>
public sealed record ExternalWorkItemDto(
    string SourceSystem,
    string? EnvironmentName,
    string ExternalId,
    string Title,
    string? Status,
    string? Assignee,
    DateTimeOffset? DueAt,
    string? Url,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record ConnectorHealthDto(
    string ConnectorType,
    string InstanceName,
    bool Configured,
    bool Healthy,
    string? Detail,
    DateTimeOffset CheckedAt);

public interface IOpsConnectorHealthService
{
    Task<IReadOnlyList<ConnectorHealthDto>> CheckAllAsync(CancellationToken cancellationToken = default);
}

public interface IMaintainXConnector
{
    Task<ConnectorHealthDto> CheckHealthAsync(
        MaintainXEnvironmentOptions environment,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExternalWorkItemDto>> ListOpenWorkAsync(
        MaintainXEnvironmentOptions environment,
        CancellationToken cancellationToken = default);

    /// <summary>Recently closed (DONE) work orders for completion recaps.</summary>
    Task<IReadOnlyList<ExternalWorkItemDto>> ListRecentlyCompletedAsync(
        MaintainXEnvironmentOptions environment,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Recent comment text on a work order (skips empty/attachment-only comments).</summary>
    Task<IReadOnlyList<string>> ListWorkOrderCommentSnippetsAsync(
        MaintainXEnvironmentOptions environment,
        string workOrderId,
        int limit = 8,
        CancellationToken cancellationToken = default);

    /// <summary>Post a comment on a work order (Ops-4 write-back).</summary>
    Task<string> CreateWorkOrderCommentAsync(
        MaintainXEnvironmentOptions environment,
        string workOrderId,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>Parts that are out of stock or at/below minimum quantity.</summary>
    Task<IReadOnlyList<InventoryAlertDto>> ListInventoryAlertsAsync(
        MaintainXEnvironmentOptions environment,
        CancellationToken cancellationToken = default);
}

public sealed record InventoryAlertDto(
    string EnvironmentName,
    string PartId,
    string Name,
    string Severity,
    double AvailableQuantity,
    double MinimumQuantity,
    string? Area,
    string? PartTypes);

/// <summary>EZRentOut rental order (basket) used for historical revenue rollups.</summary>
public sealed record EzRentOrderDto(
    string OrderId,
    string Customer,
    string State,
    decimal NetAmount,
    decimal GrossAmount,
    DateTimeOffset? BillFrom,
    DateTimeOffset? BillTo,
    DateTimeOffset? CheckedOutOn,
    DateTimeOffset? CompletedOn);

public interface IEZRentOutConnector
{
    Task<ConnectorHealthDto> CheckHealthAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExternalWorkItemDto>> ListOpenWorkAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completed / checked-out / payment-pending orders with billed amounts for MTD/YTD history.
    /// </summary>
    Task<IReadOnlyList<EzRentOrderDto>> ListOrdersAsync(
        CancellationToken cancellationToken = default);
}

public interface IMondayConnector
{
    Task<ConnectorHealthDto> CheckHealthAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExternalWorkItemDto>> ListOpenWorkAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Post an update (comment) on a Monday item (Ops-4 write-back).</summary>
    Task<string> CreateItemUpdateAsync(
        string itemId,
        string body,
        CancellationToken cancellationToken = default);
}

/// <summary>Future SAP or Syteline — limited accounting reads only.</summary>
public interface IAccountingConnector
{
    string ProviderName { get; }
    Task<ConnectorHealthDto> CheckHealthAsync(CancellationToken cancellationToken = default);
}
