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

    /// <summary>WAHA HTTP base URL (e.g. http://127.0.0.1:3000).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:3000";

    /// <summary>WAHA X-Api-Key for directory lookups (group subjects).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>WAHA session name used for Groups/Chats APIs.</summary>
    public string Session { get; set; } = "default";
}
