using Microsoft.AspNetCore.SignalR;

namespace DynamicUi.Server.Hubs;

/// <summary>
/// Broadcast channel to all connected clients. The server is authoritative; clients only
/// receive. The client method is "SchemaChanged" — payload carries the new version, the full
/// schema (so clients can render by replacement), and the patch that produced it.
/// </summary>
public class UiHub : Hub
{
    // No inbound methods: editing happens via MCP tools, never from clients (server-authoritative).
}
