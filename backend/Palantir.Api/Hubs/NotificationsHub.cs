using Microsoft.AspNetCore.SignalR;

namespace Palantir.Api.Hubs;

public sealed class NotificationsHub : Hub
{
    public Task SubscribeOrganization(string organizationId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"org:{organizationId}");

    public Task SubscribeUser(string userId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
}
