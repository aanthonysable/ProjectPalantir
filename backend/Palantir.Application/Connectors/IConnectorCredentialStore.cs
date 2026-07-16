namespace Palantir.Application.Connectors;

public interface IConnectorCredentialStore
{
    string Protect(string plaintext);
    string Unprotect(string protectedPayload);
}
