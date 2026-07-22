using Microsoft.AspNetCore.Mvc;
using Palantir.Api.Auth;
using Palantir.Application.Customers;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("customers")]
public sealed class CustomersController : ControllerBase
{
    private readonly ICustomerService _customers;
    private readonly ICurrentUserAccessor _currentUser;

    public CustomersController(ICustomerService customers, ICurrentUserAccessor currentUser)
    {
        _customers = customers;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CustomerSummaryDto>>> List(
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null)
        {
            return BadRequest("Organization is required.");
        }

        return Ok(await _customers.ListAsync(_currentUser.OrganizationId.Value, cancellationToken));
    }

    [HttpGet("{customerId:guid}")]
    public async Task<ActionResult<CustomerDetailDto>> Get(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null)
        {
            return BadRequest("Organization is required.");
        }

        var detail = await _customers.GetAsync(
            _currentUser.OrganizationId.Value,
            customerId,
            cancellationToken);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost]
    public async Task<ActionResult<CustomerSummaryDto>> Create(
        [FromBody] CreateCustomerBody body,
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null)
        {
            return BadRequest("Organization is required.");
        }

        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return BadRequest("Name is required.");
        }

        var created = await _customers.CreateAsync(
            _currentUser.OrganizationId.Value,
            body.Name.Trim(),
            cancellationToken);
        return Created($"/customers/{created.Id}", created);
    }

    [HttpGet("{customerId:guid}/overview")]
    public async Task<ActionResult<CustomerCompanyOverviewDto>> Overview(
        Guid customerId,
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (_currentUser.OrganizationId is null)
        {
            return BadRequest("Organization is required.");
        }

        var overview = await _customers.GetCompanyOverviewAsync(
            _currentUser.OrganizationId.Value,
            customerId,
            refresh,
            cancellationToken);
        return overview is null ? NotFound() : Ok(overview);
    }

    [HttpPost("warm")]
    public async Task<ActionResult<object>> Warm(CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null)
        {
            return BadRequest("Organization is required.");
        }

        var updated = await _customers.WarmFromSnapshotAsync(
            _currentUser.OrganizationId.Value,
            cancellationToken);
        return Ok(new { customersUpdated = updated });
    }

    [HttpPost("reconcile")]
    public async Task<ActionResult<CustomerReconcileResult>> Reconcile(
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null)
        {
            return BadRequest("Organization is required.");
        }

        var result = await _customers.ReconcileAsync(
            _currentUser.OrganizationId.Value,
            cancellationToken);
        return Ok(result);
    }
}

public sealed class CreateCustomerBody
{
    public string Name { get; set; } = string.Empty;
}
