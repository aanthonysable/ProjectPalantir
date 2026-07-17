namespace Palantir.Application.Azure;

public sealed class AzureOptions
{
    public const string SectionName = "Azure";

    public KeyVaultOptions KeyVault { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
}

public sealed class KeyVaultOptions
{
    /// <summary>e.g. https://kv-palantir-dev.vault.azure.net/</summary>
    public string Uri { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Uri)
        && Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

public sealed class StorageOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string KnowledgeContainer { get; set; } = "knowledge";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ConnectionString)
        && !string.IsNullOrWhiteSpace(KnowledgeContainer);
}
