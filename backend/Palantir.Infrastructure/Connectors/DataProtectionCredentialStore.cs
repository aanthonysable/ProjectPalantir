using Microsoft.AspNetCore.DataProtection;
using Palantir.Application.Connectors;

namespace Palantir.Infrastructure.Connectors;

public sealed class DataProtectionCredentialStore : IConnectorCredentialStore
{
    private readonly IDataProtector _protector;

    public DataProtectionCredentialStore(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Palantir.ConnectorCredentials.v1");
    }

    public string Protect(string plaintext) =>
        "local-dp:" + _protector.Protect(plaintext);

    public string Unprotect(string protectedPayload)
    {
        const string prefix = "local-dp:";
        if (!protectedPayload.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsupported credential reference format.");
        }

        return _protector.Unprotect(protectedPayload[prefix.Length..]);
    }
}
