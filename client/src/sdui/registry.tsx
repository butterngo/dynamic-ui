import type { JSX } from "react";
import type { UiNode } from "./types";

// The client half of the component registry — must stay in sync with the server's
// ComponentRegistry (the server rejects any type not known there). Each entry maps a
// component type to how it renders; `renderChildren` recurses into the subtree.
type Renderer = (node: UiNode, renderChildren: (n: UiNode) => JSX.Element) => JSX.Element;

const p = (node: UiNode) => node.props ?? {};
const str = (v: unknown, fallback = "") => (typeof v === "string" ? v : fallback);
// Text content can legitimately be a number (counts, versions) — coerce those too.
const txt = (v: unknown, fallback = "") =>
  typeof v === "string" ? v : typeof v === "number" ? String(v) : fallback;
const bool = (v: unknown) => v === true;
// Array props (options / items / rows / actions). Items are objects we read defensively.
type Obj = Record<string, unknown>;
const arr = (v: unknown): Obj[] => (Array.isArray(v) ? (v as Obj[]) : []);
const kids = (n: UiNode, rc: (n: UiNode) => JSX.Element) => (n.children ?? []).map(rc);

export const registry: Record<string, Renderer> = {
  // --- Generic primitives -------------------------------------------------
  Screen: (n, rc) => (
    <div className="sdui-screen">
      {str(p(n).title) && <h1 className="sdui-screen-title">{str(p(n).title)}</h1>}
      {kids(n, rc)}
    </div>
  ),
  Container: (n, rc) => <div className="sdui-container">{kids(n, rc)}</div>,
  Stack: (n, rc) => <div className="sdui-stack">{kids(n, rc)}</div>,
  Heading: (n) => <h2 className="sdui-heading">{str(p(n).text)}</h2>,
  Text: (n) => <p className="sdui-text">{str(p(n).text)}</p>,
  Banner: (n) => (
    <div className="sdui-banner" style={{ background: str(p(n).color, "#1f6f5c") }}>
      {str(p(n).text)}
    </div>
  ),
  Button: (n) => (
    <button className="sdui-button" style={{ background: str(p(n).color, "#2563eb") }}>
      {str(p(n).label)}
    </button>
  ),
  Input: (n) => (
    <input className="sdui-input" name={str(p(n).name)} placeholder={str(p(n).placeholder)} />
  ),
  Image: (n) => <img className="sdui-image" src={str(p(n).src)} alt={str(p(n).alt)} />,

  // --- App-launcher catalogue (styles scoped under `.lx` in App.css) ------
  LauncherWindow: (n, rc) => (
    <div className="lx">
      <div className="desktop">
        <div className={`wpf${bool(p(n).dark) ? " dark" : ""}`}>{kids(n, rc)}</div>
      </div>
    </div>
  ),

  TitleBar: (n) => {
    const title = txt(p(n).title);
    return (
      <div className="titlebar">
        <div className="tb-left">
          <div className="app-icon" style={{ background: str(p(n).iconColor, "#7C3AED") }}>
            {str(p(n).icon, title.charAt(0) || "M")}
          </div>
          <span className="tb-title">{title}</span>
          {txt(p(n).subtitle) && <span className="tb-sub">{txt(p(n).subtitle)}</span>}
        </div>
        <div className="tb-controls">
          <button className="winctl">─</button>
          <button className="winctl">▢</button>
          <button className="winctl close">✕</button>
        </div>
      </div>
    );
  },

  Toolbar: (n, rc) => <div className="toolbar">{kids(n, rc)}</div>,

  EnvSegment: (n) => (
    <div className="env-seg">
      {arr(p(n).options).map((o, i) => (
        <button key={i} className={`env-btn${bool(o.active) ? " active" : ""}`}>
          <span className="env-dot" style={{ background: str(o.color, "#8a86a0") }} />
          {txt(o.label)}
        </button>
      ))}
    </div>
  ),

  SearchBar: (n) => (
    <div className="search">
      <span className="search-i">⌕</span>
      <input placeholder={str(p(n).placeholder, "Search…")} />
    </div>
  ),

  ToolButton: (n) => <button className="tbtn">{str(p(n).icon, "⚙")}</button>,

  Body: (n, rc) => <div className="body">{kids(n, rc)}</div>,

  Sidebar: (n, rc) => <div className="sidebar">{kids(n, rc)}</div>,

  SideSection: (n) => (
    <div className="side-section">
      <div className="side-label">{txt(p(n).label)}</div>
      {arr(p(n).items).map((it, i) => (
        <button key={i} className={`side-item${bool(it.active) ? " active" : ""}`}>
          <span className="si-icon">{str(it.icon, "•")}</span>
          <span className="si-label">{txt(it.label)}</span>
          {it.count != null && <span className="si-count">{txt(it.count)}</span>}
        </button>
      ))}
    </div>
  ),

  EnvCard: (n) => (
    <div className="side-footer">
      <div className="env-card">
        <div className="env-card-row">
          <span className="env-dot lg" style={{ background: str(p(n).dotColor, "#6cdec4") }} />
          <div>
            <div className="ec-title">{txt(p(n).title)}</div>
            {txt(p(n).subtitle) && <div className="ec-sub">{txt(p(n).subtitle)}</div>}
          </div>
        </div>
        {txt(p(n).footer) && <div className="ec-foot">{txt(p(n).footer)}</div>}
      </div>
    </div>
  ),

  MainPanel: (n, rc) => <div className="main">{kids(n, rc)}</div>,

  MainHeader: (n) => (
    <div className="main-head">
      <div className="mh-title">
        {txt(p(n).title)}
        {p(n).count != null && <span className="mh-count">{txt(p(n).count)}</span>}
      </div>
      {txt(p(n).subtitle) && <div className="mh-sub">{txt(p(n).subtitle)}</div>}
    </div>
  ),

  CardGrid: (n, rc) => (
    <div className="grid-wrap">
      <div className="grid">{kids(n, rc)}</div>
    </div>
  ),

  AppCard: (n) => {
    const name = txt(p(n).name);
    const pinned = bool(p(n).pinned);
    const rows = arr(p(n).rows);
    const actions = arr(p(n).actions);
    const cls = `card${pinned ? " pinned" : ""}${bool(p(n).selected) ? " selected" : ""}`;
    return (
      <div className={cls}>
        <button className={`pin${pinned ? " on" : ""}`}>⚲</button>
        <div className="card-top">
          <div className="thumb">{str(p(n).thumb, name.charAt(0).toUpperCase() || "?")}</div>
          <div className="card-meta">
            <div className="card-name">
              <span className="card-name-text">{name}</span>
              {txt(p(n).chip) && <span className="chip">{txt(p(n).chip)}</span>}
            </div>
            {txt(p(n).short) && <div className="card-short">{txt(p(n).short)}</div>}
          </div>
        </div>
        {rows.length > 0 && (
          <div className="kv">
            {rows.map((r, i) => (
              <div key={i} className="kv-row">
                <span className="kv-label">{txt(r.label)}</span>
                <span className="kv-text">{txt(r.value)}</span>
              </div>
            ))}
          </div>
        )}
        {actions.length > 0 && (
          <div className="card-actions">
            {actions.map((a, i) => (
              <button key={i} className={`btn${bool(a.primary) ? " primary" : ""}`}>
                {txt(a.label)}
              </button>
            ))}
          </div>
        )}
      </div>
    );
  },

  StatusBar: (n) => (
    <div className="statusbar">
      <div className="sb-left">{txt(p(n).left)}</div>
      <div className="sb-mid">{txt(p(n).mid)}</div>
      <div className="sb-right">{txt(p(n).right)}</div>
    </div>
  ),
};
