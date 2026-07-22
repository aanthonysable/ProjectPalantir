namespace Palantir.Application.FollowUps;

public sealed record FollowUpScanResult(
    Guid OrganizationId,
    int ConversationsReviewed,
    int Proposals,
    int TasksCreated,
    IReadOnlyList<string> Notes);

public interface IFollowUpScanService
{
    Task<FollowUpScanResult> ScanOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
