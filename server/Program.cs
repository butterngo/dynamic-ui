using System.Net;
using System.Net.Sockets;
using DynamicUi.Server.Data;
using DynamicUi.Server.Hubs;
using DynamicUi.Server.Schema;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// MCP stdio owns stdout — every log line must go to stderr or it corrupts the JSON-RPC stream.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

const string DevCorsPolicy = "vite-dev";
const string ViteOrigin = "http://localhost:5173";

builder.Services.AddDbContextFactory<UiSchemaDb>(o => o.UseSqlite("Data Source=dynamic-ui.db"));
builder.Services.AddSingleton<SchemaStore>();
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddPolicy(DevCorsPolicy, p => p
    .WithOrigins(ViteOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// Attach the stdio MCP transport only when launched as an MCP server (e.g. by Claude Desktop:
// `DynamicUi.Server --mcp`). Without the flag the same binary runs web-only so Kestrel/SignalR
// stay up for local dev — single process, both capabilities, one authoritative SchemaStore.
var asMcpServer = args.Contains("--mcp") || Environment.GetEnvironmentVariable("MCP_STDIO") == "1";
if (asMcpServer)
{
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();
}

var app = builder.Build();

// Create + seed the SQLite store before serving.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<SchemaStore>().EnsureSeededAsync();
}

app.UseCors(DevCorsPolicy);

// Read surface for clients (bootstrap + reconnect re-fetch). Editing is MCP-only.
app.MapGet("/api/schema", async (SchemaStore store) =>
{
    var snap = await store.GetCurrentAsync();
    return Results.Content($$"""{"version":{{snap.Version}},"schema":{{snap.Schema}}}""", "application/json");
});
app.MapGet("/api/history", async (SchemaStore store) => Results.Ok(await store.GetHistoryAsync()));
app.MapHub<UiHub>("/hub/ui");

// The MCP stdio transport and this web host share one process. A second instance — or a stale
// orphan — holding 5179 would crash the whole process on Kestrel bind, taking the MCP transport
// down mid-handshake (Claude Desktop/Code shows "fetching tools failed: Not connected"). So when
// 5179 is already taken we keep the MCP tools alive on an ephemeral port and warn loudly instead
// of dying: tools stay usable; only SignalR live-broadcast from THIS process is forfeited.
const int WebPort = 5179;
var webUrl = $"http://localhost:{WebPort}";
if (IsPortInUse(WebPort))
{
    app.Logger.LogWarning(
        "Port {Port} is already in use — another dynamic-ui instance is likely running. Starting MCP " +
        "tools only; SignalR live-broadcast from THIS process is disabled. Stop the other instance " +
        "(netstat -ano | findstr :{Port}  ->  taskkill /PID <pid> /F) and restart for live UI updates.",
        WebPort, WebPort);
    webUrl = "http://127.0.0.1:0"; // ephemeral — keeps the host (and MCP transport) up without conflicting
                                   // (Kestrel rejects "localhost:0"; dynamic port needs an explicit IP)
}

app.Run(webUrl);

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
