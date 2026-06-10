import { useContext, type JSX } from "react";
import type { UiNode } from "./types";
import { DuiContext } from "./dui-context";

// The client half of the component registry — must stay in sync with the server's
// ComponentRegistry (the server rejects any type not known there). Each entry maps a
// component type to how it renders; `renderChildren` recurses into the subtree.
//
// dynamic-ui spec v1.0: styling is carried entirely in props.className (Tailwind). The
// renderer just passes className through to the DOM — no scoped CSS, no design tokens.
type Renderer = (node: UiNode, renderChildren: (n: UiNode) => JSX.Element) => JSX.Element;

const p = (n: UiNode) => n.props ?? {};
const str = (v: unknown, fb = "") => (typeof v === "string" ? v : fb);
// Text content can legitimately be a number (counts) — coerce those too.
const txt = (v: unknown, fb = "") =>
  typeof v === "string" ? v : typeof v === "number" ? String(v) : fb;
const cls = (n: UiNode) => str(p(n).className) || undefined;
const kids = (n: UiNode, rc: (n: UiNode) => JSX.Element) => (n.children ?? []).map(rc);
// className + node-id stamp every rendered element carries (id aids live-patch targeting).
const attrs = (n: UiNode) => ({ "data-node-id": n.id, className: cls(n) });

// Rows / columns are objects read defensively (they come straight from the schema).
type Obj = Record<string, unknown>;
const objs = (v: unknown): Obj[] => (Array.isArray(v) ? (v as Obj[]) : []);

// resolve a (possibly nested) row field path, e.g. "profile.role"
function getByPath(obj: Obj, path: string): unknown {
  return String(path)
    .split(".")
    .reduce<unknown>((o, k) => (o == null ? undefined : (o as Obj)[k]), obj);
}

const STATUS_BADGE: Record<string, string> = {
  Active: "bg-emerald-50 dark:bg-emerald-500/10 text-emerald-700 dark:text-emerald-400 ring-emerald-600/20",
  Invited: "bg-amber-50 dark:bg-amber-500/10 text-amber-700 dark:text-amber-400 ring-amber-600/20",
  Suspended: "bg-rose-50 dark:bg-rose-500/10 text-rose-700 dark:text-rose-400 ring-rose-600/20",
};

// one table cell, honoring an optional column variant (badge | user | plain)
function Cell({ col, row, tdClassName }: { col: Obj; row: Obj; tdClassName?: string }) {
  const cellCls = str(col.tdClassName) || tdClassName;
  const value = getByPath(row, str(col.key));

  if (col.variant === "badge") {
    const tone = STATUS_BADGE[String(value)] || "bg-gray-100 dark:bg-gray-800 text-gray-600 dark:text-gray-300 ring-gray-500/20";
    return (
      <td className={cellCls}>
        <span className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${tone}`}>
          <span className="w-1.5 h-1.5 rounded-full bg-current opacity-70" />
          {String(value ?? "—")}
        </span>
      </td>
    );
  }

  if (col.variant === "user") {
    return (
      <td className={cellCls}>
        <span className="flex items-center gap-3">
          <span className="w-8 h-8 shrink-0 rounded-full bg-gray-100 dark:bg-gray-800 ring-1 ring-gray-200 dark:ring-gray-700 flex items-center justify-center text-[11px] font-semibold text-gray-600 dark:text-gray-300">
            {str(row.initials) || String(value ?? "?").slice(0, 1)}
          </span>
          <span className="font-medium text-gray-900 dark:text-gray-100">{String(value ?? "")}</span>
        </span>
      </td>
    );
  }

  return <td className={cellCls}>{value == null ? "—" : String(value)}</td>;
}

// the declarative table: column definitions + static `rows`. (An API-bound `dataSource`
// is part of the spec but must fetch through a server-side proxy/allowlist — not yet wired,
// so the seed ships static rows.)
function TableNode({ node }: { node: UiNode }) {
  const pr = p(node);
  const cols = objs(pr.columns);
  const rows = objs(pr.rows);

  return (
    <div className="overflow-hidden" data-node-id={node.id}>
      <table className={str(pr.className)}>
        <thead className={str(pr.headerClassName)}>
          <tr>
            {cols.map((c) => (
              <th key={str(c.key)} className={str(pr.thClassName)} scope="col">
                {txt(c.label)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) => (
            <tr key={i} className={str(pr.rowClassName)}>
              {cols.map((c) => (
                <Cell key={str(c.key)} col={c} row={row} tdClassName={str(pr.tdClassName)} />
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// A button whose props.onClick={action,payload} dispatches a client-side action (no MCP round-trip).
// The "toggleTheme" button is special-cased to show a theme-reactive label, since the label has to
// update instantly on click — it can't wait for a schema patch to rewrite props.text.
function ActionButton({ node }: { node: UiNode }) {
  const { theme, dispatch } = useContext(DuiContext);
  const pr = p(node);
  const onClick = pr.onClick as { action?: string; payload?: unknown } | undefined;
  const action = onClick?.action;
  const label =
    action === "toggleTheme" ? (theme === "dark" ? "☀ Light mode" : "☾ Dark mode") : txt(pr.text);
  return (
    <button
      {...attrs(node)}
      type="button"
      onClick={action ? () => dispatch(action, onClick?.payload) : undefined}
    >
      {label}
    </button>
  );
}

export const registry: Record<string, Renderer> = {
  // a container may carry inline text (e.g. the brand-mark glyph) instead of children
  container: (n, rc) => {
    const t = p(n).text;
    return <div {...attrs(n)}>{t != null ? txt(t) : kids(n, rc)}</div>;
  },
  header: (n, rc) => <header {...attrs(n)}>{kids(n, rc)}</header>,
  footer: (n, rc) => {
    const t = p(n).text;
    return <footer {...attrs(n)}>{t != null ? txt(t) : kids(n, rc)}</footer>;
  },
  text: (n) => {
    const as = str(p(n).as, "p");
    const Tag = (["h1", "h2", "h3", "p", "span"].includes(as) ? as : "p") as keyof JSX.IntrinsicElements;
    return <Tag {...attrs(n)}>{txt(p(n).text)}</Tag>;
  },
  link: (n) => (
    <a {...attrs(n)} href={str(p(n).href, "#")}>
      {txt(p(n).text)}
    </a>
  ),
  button: (n) => <ActionButton node={n} />,
  table: (n) => <TableNode node={n} />,
};
