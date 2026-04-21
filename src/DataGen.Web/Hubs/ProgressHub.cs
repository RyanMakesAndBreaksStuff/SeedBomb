using Microsoft.AspNetCore.SignalR;

namespace DataGen.Web.Hubs;

/// <summary>SignalR hub for streaming generation progress to connected clients.</summary>
public class ProgressHub : Hub
{
    /// <summary>Client subscribes to progress events for a specific generation session.</summary>
    /// <param name="sessionId">The session identifier to subscribe to.</param>
    public Task SubscribeToGeneration(string sessionId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }
}
