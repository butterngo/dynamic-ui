# Dynamic UI — live schema-driven UI, edited from Claude Desktop

Edit a running app's UI by talking to Claude Desktop. The UI is a stored JSON **schema**;
"editing" mutates that schema through MCP `ui_*` tools. The server validates every change,
persists a new version, and broadcasts it over SignalR — all connected clients re-render live,
no rebuild, no redeploy.

**Scope: demo / PoC** ([TASK-1061](../)). Single trusted local operator, no auth. See
`docs/knowledge/ADR-001-single-dotnet-process.md` for the architecture decision.

## Architecture

```
Claude Desktop ──stdio(MCP)──▶ ┌─────────────────────────────────────────┐
                               │  DynamicUi.Server (single .NET process)   │
   ui_apply_patch, ui_set_prop │   • MCP tools (ui_*)                       │
   ui_add/remove_component     │   • PatchValidator + ComponentRegistry     │
   ui_history, ui_rollback     │   • SchemaStore  (validate→persist→cast)   │
                               │   • SQLite (schema + patch history)        │
                               │   • SignalR hub  /hub/ui                   │
                               └───────────────┬───────────────────────────┘
                                               │ SignalR "SchemaChanged"
                                               ▼
                              React client (Vite) — walks schema, renders live
```

- **server/** — .NET 8. MCP (stdio) + SignalR + EF Core/SQLite in one process.
- **client/** — React + TypeScript (Vite), `@microsoft/signalr`.

## Run it

**1. Start the server (web mode, for the client):**

```sh
cd server
dotnet run            # hosts http://localhost:5179  (SignalR + GET /api/schema)
```

**2. Start the client:**

```sh
cd client
npm install           # first time only
npm run dev           # http://localhost:5173
```

Open http://localhost:5173 — it renders the seeded schema and shows a live/▽version indicator.

**3. Wire Claude Desktop (to drive edits via MCP):**

Copy the `dynamic-ui` block from `docs/claude-desktop-config.example.json` into your
`claude_desktop_config.json` and restart Claude Desktop. It launches the server with `--mcp`
(stdio transport on + the same Kestrel/SignalR/SQLite host). Then ask Claude, e.g.:

> "Make the welcome heading say 'Hello team' and add a teal banner that says 'Live edit works'."

The change is validated, versioned, broadcast — and appears in every open browser instantly.

> Note: in MCP mode the web host lives only while Claude Desktop has the process spawned. For
> driving both the browser *and* Claude Desktop against one store during a demo, run `dotnet run`
> (web mode) in a terminal and point the browser at it; use Claude Desktop's MCP launch to issue
> edits. (PoC trade-off — see ADR-001.)

## Verify the live-edit loop (no Claude Desktop needed)

`client/scripts/loop-test.mjs` is an integration check for the whole path —
MCP `tools/call` (raw JSON-RPC over stdin) → `SchemaStore` validate → persist → SignalR
broadcast → a connected SignalR client receives `SchemaChanged`. It also asserts an invalid
patch is rejected without bumping the version.

```sh
cd server
dotnet build                       # produces bin/Debug/net8.0/DynamicUi.Server.dll
node ../client/scripts/loop-test.mjs   # spawns the server (--mcp), drives a patch, asserts the broadcast
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
