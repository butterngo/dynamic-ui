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

app.Run("http://localhost:5179");
