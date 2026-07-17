using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Palantir.Api.Auth;
using Palantir.Application.Connectors;
using Palantir.Application.Ops;
using Palantir.Application.Outbound;

namespace Palantir.Api.Controllers;

[ApiController]
[Authorize]
[Route("ops")]
public sealed class OpsConnectorsController : ControllerBase
{
    private readonly IOpsConnectorHealthService _health;
    private readonly IMaintainXConnector _maintainX;
    private readonly IEZRentOutConnector _ezRentOut;
    private readonly IMondayConnector _monday;
    private readonly MaintainXOptions _maintainXOptions;
    private readonly IAccountingConnector _accounting;
    private readonly IOpsWriteBackService _writeBack;
    private readonly ICurrentUserAccessor _currentUser;

    public OpsConnectorsController(
        IOpsConnectorHealthService health,
        IMaintainXConnector maintainX,
        IEZRentOutConnector ezRentOut,
        IMondayConnector monday,
        IOptions<MaintainXOptions> maintainXOptions,
        IAccountingConnector accounting,
        IOpsWriteBackService writeBack,
        ICurrentUserAccessor currentUser)
    {
        _health = health;
        _maintainX = maintainX;
        _ezRentOut = ezRentOut;
        _monday = monday;
        _maintainXOptions = maintainXOptions.Value;
        _accounting = accounting;
        _writeBack = writeBack;
        _currentUser = currentUser;
    }

    [HttpGet("health")]
    public async Task<ActionResult<IReadOnlyList<ConnectorHealthDto>>> Health(CancellationToken cancellationToken)
    {
        var results = (await _health.CheckAllAsync(cancellationToken)).ToList();
        results.Add(await _accounting.CheckHealthAsync(cancellationToken));
        return Ok(results);
    }

    [HttpGet("open-work")]
    public async Task<ActionResult<IReadOnlyList<ExternalWorkItemDto>>> OpenWork(
        CancellationToken cancellationToken)
    {
        var items = new List<ExternalWorkItemDto>();

        foreach (var env in _maintainXOptions.Environments.Where(e => !string.IsNullOrWhiteSpace(e.ApiKey)))
        {
            items.AddRange(await _maintainX.ListOpenWorkAsync(env, cancellationToken));
        }

        items.AddRange(await _ezRentOut.ListOpenWorkAsync(cancellationToken));
        items.AddRange(await _monday.ListOpenWorkAsync(cancellationToken));
        return Ok(items);
    }

    /// <summary>Propose an approval-gated MaintainX comment or Monday update (Ops-4).</summary>
    [HttpPost("write-back")]
    public async Task<ActionResult<ReplyDraftResult>> ProposeWriteBack(
        [FromBody] ProposeOpsWriteBackBody body,
        CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required in the pilot.");
        }

        try
        {
            var organizationId = body.OrganizationId ?? _currentUser.OrganizationId
                ?? throw new InvalidOperationException("Organization id is required.");

            var result = await _writeBack.ProposeAsync(
                new ProposeOpsWriteBackRequest(
                    organizationId,
                    _currentUser.UserId.Value,
                    body.SourceSystem,
                    body.EnvironmentName,
                    body.ExternalId,
                    body.Title ?? string.Empty,
                    body.Body),
                cancellationToken);
            return Ok(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed class ProposeOpsWriteBackBody
{
    public Guid? OrganizationId { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string? EnvironmentName { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string Body { get; set; } = string.Empty;
}
