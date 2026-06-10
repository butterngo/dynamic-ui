# Dynamic UI — live schema-driven UI, edited from Claude Desktop

Edit a running app's UI by talking to Claude Desktop. The UI is a stored JSON **schema**;
"editing" mutates that schema through MCP `ui_*` tools. The server validates every change,
persists a new version, and broadcasts it over SignalR — all connected clients re-render live,
no rebuild, no redeploy.

**Scope: demo / PoC** ([TASK-1061](../)). Single trusted local operator, no auth. See
`docs/knowledge/ADR-001-single-dotnet-process.md` for the architecture decision.

## Architecture

```
Claude Code     ─┐
                 │ HTTP(MCP) /mcp  ┌─────────────────────────────────────────┐
Claude Desktop  ─┤───────────────▶│  DynamicUi.Server (single .NET process)   │
                 │                 │   • MCP tools (ui_*)  — Streamable HTTP    │
   ui_apply_patch, ui_set_prop     │   • PatchValidator + ComponentRegistry     │
   ui_add/remove_component         │   • SchemaStore  (validate→persist→cast)   │
   ui_history, ui_rollback         │   • SQLite (schema + patch history)        │
                                   │   • SignalR hub  /hub/ui                   │
                                   └───────────────┬───────────────────────────┘
                                                   │ SignalR "SchemaChanged"
                                                   ▼
                              React client (Vite) — walks schema, renders live
```

Every client (Claude Code, Claude Desktop, the browser) connects to **one** long-running server
over HTTP — a single authoritative `SchemaStore`, so concurrent edits serialize and every edit
broadcasts live regardless of which client made it.

- **server/** — .NET 8. MCP (HTTP, Streamable) + SignalR + EF Core/SQLite in one process.
- **client/** — React + TypeScript (Vite), `@microsoft/signalr`.

## Run it

**1. Start the server (one shared instance — serves the browser AND every MCP client):**

```sh
cd server
dotnet run            # hosts http://localhost:5179  (SignalR + GET /api/schema + MCP at /mcp)
```

Leave it running. Clients no longer spawn their own copy — they all connect to this one. (Starting
a second copy is a no-op: it sees 5179 in use and exits rather than fighting over the port/DB.)

**2. Start the client:**

```sh
cd client
npm install           # first time only
npm run dev           # http://localhost:5173
```

Open http://localhost:5173 — it renders the seeded schema and shows a live/▽version indicator.

**3. Wire the Claude clients (to drive edits via MCP):**

Both connect to the running server's MCP endpoint, and both can be open at once against the one store.

- **Claude Code** — `.mcp.json` (already set in this repo):
  ```json
  { "mcpServers": { "dynamic-ui": { "type": "http", "url": "http://localhost:5179/mcp" } } }
  ```
- **Claude Desktop** — its config talks stdio, so bridge to the HTTP server with `mcp-remote`
  (see `docs/claude-desktop-config.example.json`), then restart Desktop:
  ```json
  { "mcpServers": { "dynamic-ui": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:5179/mcp", "--allow-http"]
  } } }
  ```

Then ask Claude, e.g.:

> "Make the title bar say 'Hello team' and add a teal banner that says 'Live edit works'."

The change is validated, versioned, broadcast — and appears in every open browser instantly,
no matter which client issued it.

## Verify the live-edit loop (no Claude Desktop needed)

`client/scripts/loop-test.mjs` is an integration check for the whole path —
MCP `tools/call` (raw JSON-RPC over stdin) → `SchemaStore` validate → persist → SignalR
broadcast → a connected SignalR client receives `SchemaChanged`. It also asserts an invalid
patch is rejected without bumping the version.

```sh
cd server
dotnet build                       # produces bin/Debug/net8.0/DynamicUi.Server.dll
node ../client/scripts/loop-test.mjs   # starts (or reuses) the server, drives a patch over /mcp, asserts the broadcast
```

Expected: `ALL PASS ✅`.

## MCP tools

| Tool | Purpose |
|------|---------|
| `ui_get_schema` | Current schema tree + version |
| `ui_apply_patch` | Apply an RFC-6902 JSON Patch |
| `ui_set_prop` | Set one prop on a node (JSON Pointer) |
| `ui_add_component` | Append a child to a parent node |
| `ui_remove_component` | Remove a node |
| `ui_history` | Version history |
| `ui_rollback` | Restore a prior version (as a new version) |

## Component registry

`server/Schema/ComponentRegistry.cs` is the validation contract (known types + required props);
`client/src/sdui/registry.tsx` is the matching renderer. Add a component in **both** to extend the
catalogue. A patch introducing an unknown type or missing a required prop is rejected server-side.
