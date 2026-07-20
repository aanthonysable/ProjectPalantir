namespace Palantir.Application.Connectors;

public sealed class WhatsAppBridgeOptions
{
    public const string SectionName = "Connectors:WhatsApp";

    /// <summary>When false, webhook returns 503 and status shows disabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Shared secret; required on X-Palantir-Webhook-Secret.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Org that owns ingested threads. Empty = first org in DB (dev seed).</summary>
    public string? OrganizationId { get; set; }

    /// <summary>Optional label in Admin health.</summary>
    public string InstanceName { get; set; } = "WAHA bridge";
}
