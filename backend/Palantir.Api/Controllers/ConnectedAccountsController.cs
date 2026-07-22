using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Api.Auth;
using Palantir.Application.Abstractions;
using Palantir.Application.Connectors;

namespace Palantir.Api.Controllers;

[ApiController]
public sealed class ConnectedAccountsController : ControllerBase
{
    private readonly IMicrosoftGraphConnectorService _microsoft;
    private readonly IOutlookInboxSyncService _outlookSync;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MicrosoftGraphOptions _options;
    private readonly ILogger<ConnectedAccountsController> _logger;

    public ConnectedAccountsController(
        IMicrosoftGraphConnectorService microsoft,
        IOutlookInboxSyncService outlookSync,
        ICurrentUserAccessor currentUser,
        IServiceScopeFactory scopeFactory,
        IOptions<MicrosoftGraphOptions> options,
        ILogger<ConnectedAccountsController> logger)
    {
        _microsoft = microsoft;
        _outlookSync = outlookSync;
        _currentUser = currentUser;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost("connected-accounts/microsoft/authorize")]
    public async Task<ActionResult<AuthorizeMicrosoftResult>> BeginAuthorize(
        [FromBody] BeginMicrosoftAuthorizeBody? body,
        CancellationToken cancellationToken)
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
                body?.MailboxKind ?? "Work",
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
            QueueInitialInboxSync(account);
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

    [HttpPatch("connected-accounts/{connectedAccountId:guid}/mailbox-kind")]
    public async Task<ActionResult<ConnectedAccountDto>> UpdateMailboxKind(
        Guid connectedAccountId,
        [FromBody] UpdateMailboxKindBody body,
        CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required.");
        }

        try
        {
            var updated = await _microsoft.UpdateMailboxKindAsync(
                connectedAccountId,
                _currentUser.UserId.Value,
                body.MailboxKind ?? "Work",
                cancellationToken);
            return Ok(updated);
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

    [HttpPost("connected-accounts/{connectedAccountId:guid}/sync")]
    public async Task<ActionResult<OutlookMailSyncResult>> SyncInbox(
        Guid connectedAccountId,
        [FromQuery] int top = 25,
        CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is null || _currentUser.OrganizationId is null)
        {
            return BadRequest("X-Palantir-User-Id and X-Palantir-Organization-Id headers are required.");
        }

        try
        {
            var result = await _outlookSync.SyncAsync(
                connectedAccountId,
                _currentUser.UserId.Value,
                _currentUser.OrganizationId.Value,
                top,
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("connected-accounts/{connectedAccountId:guid}/calendar")]
    public async Task<ActionResult<IReadOnlyList<OutlookCalendarEventDto>>> ListCalendar(
        Guid connectedAccountId,
        [FromQuery] int top = 25,
        CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required.");
        }

        try
        {
            var items = await _microsoft.ListCalendarEventsAsync(
                connectedAccountId,
                _currentUser.UserId.Value,
                top: top,
                cancellationToken: cancellationToken);
            return Ok(items);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("connected-accounts/{connectedAccountId:guid}/teams/chats")]
    public async Task<ActionResult<IReadOnlyList<TeamsChatDto>>> ListTeamsChats(
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
            var items = await _microsoft.ListTeamsChatsAsync(
                connectedAccountId,
                _currentUser.UserId.Value,
                top,
                cancellationToken);
            return Ok(items);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("connected-accounts/{connectedAccountId:guid}/teams/chats/{chatId}/messages")]
    public async Task<ActionResult<IReadOnlyList<TeamsChatMessageDto>>> ListTeamsChatMessages(
        Guid connectedAccountId,
        string chatId,
        [FromQuery] int top = 25,
        CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required.");
        }

        try
        {
            var items = await _microsoft.ListTeamsChatMessagesAsync(
                connectedAccountId,
                _currentUser.UserId.Value,
                chatId,
                top,
                cancellationToken);
            return Ok(items);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private void QueueInitialInboxSync(ConnectedAccountDto account)
    {
        var accountId = account.Id;
        var userId = account.UserId;
        var address = account.PrimaryAddress;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IPalantirDbContext>();
                var sync = scope.ServiceProvider.GetRequiredService<IOutlookInboxSyncService>();
                var organizationId = db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => u.OrganizationId)
                    .FirstOrDefault();
                if (organizationId == Guid.Empty)
                {
                    return;
                }

                var result = await sync.SyncAsync(accountId, userId, organizationId, top: 25);
                _logger.LogInformation(
                    "Initial Outlook sync after connect for {Address}: imported={Imported} fetched={Fetched}",
                    address,
                    result.Imported,
                    result.Fetched);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Initial Outlook sync after connect failed for account {AccountId}",
                    accountId);
            }
        });
    }
}

public sealed class BeginMicrosoftAuthorizeBody
{
    public string? MailboxKind { get; set; }
}

public sealed class UpdateMailboxKindBody
{
    public string? MailboxKind { get; set; }
}
