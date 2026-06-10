using System.Net;
using System.Net.Sockets;
using DynamicUi.Server.Data;
using DynamicUi.Server.Hubs;
using DynamicUi.Server.Schema;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

const string DevCorsPolicy = "vite-dev";
const string ViteOrigin = "http://localhost:5173";

// Anchor the SQLite file to the binary's own directory, NOT the process cwd. A relative
// "Data Source=dynamic-ui.db" resolves against whatever cwd the launcher happens to use and can
// fail with SQLite Error 14 (unable to open database file). AppContext.BaseDirectory is stable.
var dbPath = Path.Combine(AppContext.BaseDirectory, "dynamic-ui.db");
builder.Services.AddDbContextFactory<UiSchemaDb>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<SchemaStore>();
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddPolicy(DevCorsPolicy, p => p
    .WithOrigins(ViteOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ONE long-running process serves the MCP tools over HTTP (Streamable HTTP transport) to EVERY
// client at once. Claude Code and Claude Desktop both connect to http://localhost:5179/mcp instead
// of each spawning their own stdio copy of this binary. That gives a single authoritative
// SchemaStore + one SignalR hub the browser listens on, so: (a) edits from either client broadcast
// live to the browser, and (b) the in-process write gate in SchemaStore now genuinely serializes
// both clients, so concurrent edits can't collide on a schema version. No per-client process race.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Create + seed the SQLite store before serving.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<SchemaStore>().EnsureSeededAsync();
}

app.UseCors(DevCorsPolicy);

// Read surface for browser clients (bootstrap + reconnect re-fetch). Editing is MCP-only.
app.MapGet("/api/schema", async (SchemaStore store) =>
{
    var snap = await store.GetCurrentAsync();
    return Results.Content($$"""{"version":{{snap.Version}},"schema":{{snap.Schema}}}""", "application/json");
});
app.MapGet("/api/history", async (SchemaStore store) => Results.Ok(await store.GetHistoryAsync()));
app.MapHub<UiHub>("/hub/ui");

// Streamable HTTP MCP endpoint. Clients point at http://localhost:5179/mcp.
app.MapMcp("/mcp");

// This is a singleton service now — clients connect to a fixed URL, they don't spawn it. If 5179 is
// already taken, another copy is already running; a second one would only fight over the same DB and
// port, so refuse to start and point at the live one rather than crash-looping or binding a useless
// ephemeral port that no client's URL can reach.
const int WebPort = 5179;
if (IsPortInUse(WebPort))
{
    app.Logger.LogError(
        "Port {Port} is already in use — dynamic-ui is already running at http://localhost:{Port}. " +
        "This server is a singleton (all clients share one instance over HTTP), so not starting a " +
        "second copy. Stop the other instance first if you meant to restart " +
        "(netstat -ano | findstr :{Port}  ->  taskkill /PID <pid> /F).",
        WebPort, WebPort, WebPort);
    return;
}

app.Run($"http://localhost:{WebPort}");

// True if something is already listening on the loopback port. Best-effort (a TOCTOU race with the
// subsequent Kestrel bind is possible but harmless for this single-operator PoC).
static bool IsPortInUse(int port)
{
    try
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        return false;
    }
    catch (SocketException)
    {
        return true;
    }
}
