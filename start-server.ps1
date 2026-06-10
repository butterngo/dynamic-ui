# Starts the single shared dynamic-ui server on http://localhost:5179.
# One process serves the browser (SignalR + /api/schema), Claude Code, and Claude Desktop —
# all clients connect over HTTP to the MCP endpoint at /mcp. Leave this window running.
# Starting a second copy is a no-op: it sees 5179 in use and exits.
dotnet run --project "$PSScriptRoot\server\DynamicUi.Server.csproj"
