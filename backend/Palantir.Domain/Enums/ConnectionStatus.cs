namespace Palantir.Domain.Enums;

public enum ConnectionStatus
{
    NotConnected = 0,
    Connected = 1,
    AdminConsentRequired = 2,
    PolicyBlocked = 3,
    ReauthorizationRequired = 4,
    Revoked = 5,
    Error = 6
}
