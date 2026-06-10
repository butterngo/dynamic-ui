---
type: decision
title: "ADR-001: Single authoritative .NET process (MCP + SignalR + SQLite)"
projectId: mantu
workspaceId: dynamic-ui
scope: project
createdAt: 2026-06-10
lastVerifiedAt: 2026-06-10
---

# ADR-001: Single authoritative .NET process (MCP + SignalR + SQLite)

- **Status:** Accepted
- **Date:** 2026-06-10
- **Task:** TASK-1061
- **Scope:** PoC

## Context

TASK-1061 needs an authoritative server that validates a UI-schema patch, persists a new
version, and broadcasts it to all clients — atomically. The open fork was *where that
authoritative layer lives*:

- **A — Single .NET process.** One process hosts the stdio MCP server (ModelContextProtocol
  .NET SDK), the SignalR hub, and the SQLite store. `validate → persist → broadcast` is a single
  in-process call path.
- **B — TS MCP server forwarding to a .NET app.** A separate Node MCP process forwards `ui_*`
  calls over HTTP to a .NET app that owns validate/persist/broadcast.

## Decision

**Adopt A — a single authoritative .NET process.**

The same binary runs in two modes off one entry point:
- launched by Claude Desktop as `DynamicUi.Server --mcp` → stdio MCP transport is attached;
- launched as `dotnet run` (no flag) → web-only, so Kestrel + SignalR stay up for local dev.

Both modes host Kestrel (SignalR hub + read REST API) and share one `SchemaStore` singleton.

## Rationale

- **Atomicity for free.** `SchemaStore.ApplyPatchAsync` validates, persists, and broadcasts inside
  one process under a semaphore — no cross-process contract, no partial-apply window.
- **One language, one deploy.** No Node↔.NET HTTP hop to define, secure, or operate; fits the
  Mantu .NET stack.
- **PoC-appropriate.** Single trusted local operator over stdio; no auth surface to design.

## Consequences

- **stdout is reserved for MCP JSON-RPC.** All logging is routed to stderr
  (`LogToStandardErrorThreshold = Trace`); writing logs to stdout would corrupt the protocol.
- The web server lives only as long as the process Claude Desktop spawned (acceptable for a demo;
  for production we'd run the web host independently and have the MCP shim call it).
- The React client connects to a fixed Kestrel port (`http://localhost:5179`).

## Alternatives rejected

- **B (TS forwarding)** — adds a process boundary and an internal HTTP contract for no PoC benefit;
  splits the authoritative logic across two runtimes. Revisit only if the MCP server must scale or
  deploy separately from the app.
