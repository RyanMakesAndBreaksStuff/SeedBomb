using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DataGen.Web.Hubs;

/// <summary>SignalR hub for streaming generation progress to connected clients.</summary>
[Authorize]
public sealed class ProgressHub : Hub
{
    /// <summary>Client subscribes to receive progress events for a specific generation session.</summary>
    /// <param name="sessionId">The session identifier to subscribe to.</param>
    public Task SubscribeToGeneration(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        return Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }
}
