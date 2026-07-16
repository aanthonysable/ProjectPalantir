using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Palantir.Api.Auth;
using Palantir.Application.Connectors;

namespace Palantir.Api.Controllers;

[ApiController]
public sealed class ConnectedAccountsController : ControllerBase
{
    private readonly IMicrosoftGraphConnectorService _microsoft;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly MicrosoftGraphOptions _options;

    public ConnectedAccountsController(
        IMicrosoftGraphConnectorService microsoft,
        ICurrentUserAccessor currentUser,
        IOptions<MicrosoftGraphOptions> options)
    {
        _microsoft = microsoft;
        _currentUser = currentUser;
        _options = options.Value;
    }

    [HttpPost("connected-accounts/microsoft/authorize")]
    public async Task<ActionResult<AuthorizeMicrosoftResult>> BeginAuthorize(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null || _currentUser.OrganizationId is null)
        {
            return BadRequest("X-Palantir-User-Id and X-Palantir-Organization-Id headers are required.");
        }

        try
        {
            var result = await _microsoft.BeginAuthorizeAsync(
                _currentUser.UserId.Value,
                _currentUser.OrganizationId.Value,
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("oauth/microsoft/callback")]
    public async Task<IActionResult> MicrosoftCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? error_description,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            var message = Uri.EscapeDataString(error_description ?? error);
            return Redirect($"{_options.FrontendErrorUri}&message={message}");
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return Redirect($"{_options.FrontendErrorUri}&message=missing_code");
        }

        try
        {
            var account = await _microsoft.CompleteAuthorizeAsync(code, state, cancellationToken);
            var success =
                $"{_options.FrontendSuccessUri}&accountId={account.Id}&address={Uri.EscapeDataString(account.PrimaryAddress ?? string.Empty)}";
            return Redirect(success);
        }
        catch (Exception ex)
        {
            var message = Uri.EscapeDataString(ex.Message);
            return Redirect($"{_options.FrontendErrorUri}&message={message}");
        }
    }

    [HttpGet("connected-accounts")]
    public async Task<ActionResult<IReadOnlyList<ConnectedAccountDto>>> List(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required.");
        }

        var items = await _microsoft.ListForUserAsync(_currentUser.UserId.Value, cancellationToken);
        return Ok(items);
    }

    [HttpGet("connected-accounts/{connectedAccountId:guid}")]
    public async Task<ActionResult<ConnectedAccountDto>> Get(Guid connectedAccountId, CancellationToken cancellationToken)
    {
        var account = await _microsoft.GetAsync(connectedAccountId, cancellationToken);
        return account is null ? NotFound() : Ok(account);
    }

    [HttpDelete("connected-accounts/{connectedAccountId:guid}")]
    public async Task<IActionResult> Disconnect(Guid connectedAccountId, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required.");
        }

        try
        {
            await _microsoft.DisconnectAsync(connectedAccountId, _currentUser.UserId.Value, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("connected-accounts/{connectedAccountId:guid}/mail")]
    public async Task<ActionResult<IReadOnlyList<OutlookMessageDto>>> ListMail(
        Guid connectedAccountId,
        [FromQuery] int top = 20,
        CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required.");
        }

        try
        {
            var messages = await _microsoft.ListMailAsync(
                connectedAccountId,
                _currentUser.UserId.Value,
                top,
                cancellationToken);
            return Ok(messages);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
