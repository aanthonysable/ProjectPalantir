using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Palantir.Application.Connectors;

namespace Palantir.Api.Controllers;

/// <summary>WAHA (WhatsApp Web bridge) webhook — no JWT; shared secret header.</summary>
[ApiController]
[AllowAnonymous]
[Route("webhooks/whatsapp")]
public sealed class WhatsAppWebhookController : ControllerBase
{
    private readonly IWhatsAppIngestService _ingest;
    private readonly WhatsAppBridgeOptions _options;
    private readonly ILogger<WhatsAppWebhookController> _logger;

    public WhatsAppWebhookController(
        IWhatsAppIngestService ingest,
        IOptions<WhatsAppBridgeOptions> options,
        ILogger<WhatsAppWebhookController> logger)
    {
        _ingest = ingest;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "WhatsApp bridge disabled" });
        }

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Webhook secret not configured" });
        }

        var providedSecret =
            Request.Headers.TryGetValue("X-Palantir-Webhook-Secret", out var headerSecret)
                ? headerSecret.ToString()
                : Request.Query["secret"].ToString();

        if (string.IsNullOrWhiteSpace(providedSecret) ||
            !FixedTimeEquals(providedSecret, _options.WebhookSecret))
        {
            return Unauthorized(new { error = "Invalid webhook secret" });
        }

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        try
        {
            var result = await _ingest.IngestWahaEventAsync(raw, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp webhook ingest failed");
            return BadRequest(new { error = ex.Message });
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
