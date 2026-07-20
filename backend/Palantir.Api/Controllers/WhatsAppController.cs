using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Palantir.Application.Connectors;

namespace Palantir.Api.Controllers;

[ApiController]
[Authorize]
[Route("whatsapp")]
public sealed class WhatsAppController : ControllerBase
{
    private readonly IWhatsAppIngestService _ingest;
    private readonly IWhatsAppOpsWatchService _watch;

    public WhatsAppController(IWhatsAppIngestService ingest, IWhatsAppOpsWatchService watch)
    {
        _ingest = ingest;
        _watch = watch;
    }

    [HttpGet("status")]
    public ActionResult<WhatsAppBridgeStatusDto> Status() => Ok(_ingest.GetStatus());

    /// <summary>
    /// Internal WhatsApp group threads vs open MaintainX / Monday / EZRentOut work.
    /// Unmatched / Partial first — falling-through-the-cracks watch list.
    /// </summary>
    [HttpGet("gaps")]
    public async Task<ActionResult<IReadOnlyList<WhatsAppGapDto>>> Gaps(CancellationToken cancellationToken)
    {
        var gaps = await _watch.ListGapsAsync(cancellationToken);
        return Ok(gaps);
    }

    /// <summary>One-shot cleanup of duplicate WhatsApp inbox rows.</summary>
    [HttpPost("dedupe")]
    public async Task<ActionResult<object>> Dedupe(CancellationToken cancellationToken)
    {
        var removed = await _ingest.DedupeStoredMessagesAsync(cancellationToken);
        return Ok(new { removed });
    }
}
