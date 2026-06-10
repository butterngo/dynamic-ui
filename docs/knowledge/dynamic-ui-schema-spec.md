---
type: learning
title: Dynamic UI schema spec — node shape, components, Tailwind styling, API-bound table
projectId: mantu
workspaceId: dynamic-ui
scope: project
refs: []
createdAt: 2026-06-10
lastVerifiedAt: 2026-06-10
---

UI definition format for the dynamic-ui app. The entire interface is a stored JSON tree; the renderer walks it recursively. Edits are JSON Patches applied via the dynamic-ui MCP server (see TASK-1061), persisted with a new `version`, and broadcast over SignalR so all clients re-render live.

## Top-level shape

```json
{
  "schemaVersion": "1.0",
  "version": 1,
  "root": { /* a node */ }
}
```

- `schemaVersion` — format version of the spec itself (breaking changes bump this).
- `version` — monotonic version of THIS document; incremented on every applied patch. Drives audit history + rollback.
- `root` — the top node, rendered as the page.

## Node shape

Every node is uniform so the renderer can recurse:

```json
{ "id": "unique-id", "type": "<component>", "props": { ... }, "children": [ ... ] }
```

- `id` — stable, unique; JSON Patch targets nodes by id path.
- `type` — component kind (see below).
- `props` — component-specific config. `props.className` carries Tailwind classes; an edit is just changing this string (e.g. `bg-blue-600` → `bg-teal-600`).
- `children` — array of child nodes (containers/header/footer only).

## Styling

Pure Tailwind via `props.className`. No custom CSS. Components that have sub-parts (e.g. table) expose extra class slots (`headerClassName`, `thClassName`, `rowClassName`, `tdClassName`) so every part is data-driven.

## Component types

- `container` — generic `div`; flex/grid layout via className; has `children`.
- `header` — semantic `<header>`; has `children`. Typically brand text + nav + CTA.
- `footer` — semantic `<footer>`; renders `props.text` (or `children`).
- `text` — `props.as` selects tag (`h1|h2|p|span`); `props.text` is the content.
- `link` — `props.href`, `props.text`.
- `button` — `props.text`; action wiring TBD (event/command model).
- `table` — see below.

## Table component

Declarative. Two parts: column definitions and a data source.

```json
{
  "id": "users-table",
  "type": "table",
  "props": {
    "className": "w-full border border-gray-200 rounded-lg overflow-hidden bg-white text-sm",
    "headerClassName": "bg-gray-100 text-left text-gray-600",
    "thClassName": "px-4 py-2 font-medium",
    "rowClassName": "border-t border-gray-200",
    "tdClassName": "px-4 py-2",
    "columns": [
      { "key": "name", "label": "Name" },
      { "key": "email", "label": "Email" },
      { "key": "role", "label": "Role" },
      { "key": "status", "label": "Status" }
    ],
    "rows": [ { "name": "...", "email": "...", "role": "...", "status": "..." } ]
  }
}
```

- `columns[].key` maps a row field to a column; supports nested paths (e.g. `profile.role`).
- `columns[].label` is the header text.
- `rows` — inline/static data.

### API-bound rows

Instead of static `rows`, provide a `dataSource`; the renderer fetches at render time.

```json
"dataSource": {
  "url": "https://api.acme.com/users",
  "method": "GET",
  "rowsPath": "data.items",
  "params": { "page": 1, "pageSize": 20 },
  "refreshInterval": 0,
  "auth": "bearer"
}
```

- `url` / `method` — endpoint.
- `rowsPath` — dot path to the array inside the response (e.g. `data.items`). Each item is mapped onto `columns` by `key`.
- `params` — query/body params.
- `refreshInterval` — seconds between re-fetches; `0` = fetch once.
- `auth` — references a credential the SERVER holds; never a raw token.

Renderer behavior: `rows` present → render statically; `dataSource` present → loading state while fetching, error row on failure, map response to columns, re-fetch on `refreshInterval`.

## Security (server-authoritative)

- The dynamic-ui server validates every patch before persist; a bad schema can't go live.
- Table fetches should go through a server-side proxy with an allowlist of endpoints. Do NOT let the client hit arbitrary `url`s from the schema — a schema edit (via Claude) must not be able to point a table at an arbitrary host or leak a token.
- Auth tokens live server-side; the schema only references credential names.

## Reference

- Canonical example: `ui-schema.json` (header + table + footer).
- Implements TASK-1061 (edit dynamic UI live via Claude Desktop). Originated from INBOX-435.
