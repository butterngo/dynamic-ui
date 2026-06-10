import type { JSX } from "react";
import type { UiNode } from "./types";

// The client half of the component registry — must stay in sync with the server's
// ComponentRegistry (the server rejects any type not known there). Each entry maps a
// component type to how it renders; `renderChildren` recurses into the subtree.
type Renderer = (node: UiNode, renderChildren: (n: UiNode) => JSX.Element) => JSX.Element;

const p = (node: UiNode) => node.props ?? {};
const str = (v: unknown, fallback = "") => (typeof v === "string" ? v : fallback);

export const registry: Record<string, Renderer> = {
  Screen: (n, rc) => (
    <div className="sdui-screen">
      {str(p(n).title) && <h1 className="sdui-screen-title">{str(p(n).title)}</h1>}
      {(n.children ?? []).map(rc)}
    </div>
  ),
  Container: (n, rc) => <div className="sdui-container">{(n.children ?? []).map(rc)}</div>,
  Stack: (n, rc) => <div className="sdui-stack">{(n.children ?? []).map(rc)}</div>,
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
};
