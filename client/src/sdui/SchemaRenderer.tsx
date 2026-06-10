import { Fragment, type JSX } from "react";
import { registry } from "./registry";
import type { UiNode } from "./types";

// Walks the schema tree and renders each node via the registry. Unknown types render a
// visible fallback rather than crashing (the server should already have rejected them).
// The key lives on a Fragment, not a wrapper element, so a node's rendered output is the
// *direct* DOM child of its parent — CSS grid/flex layouts (the launcher relies on these)
// would break if an extra <span> sat between a grid container and its real children.
export function renderNode(node: UiNode): JSX.Element {
  const render = registry[node.type];
  const key = node.id ?? `${node.type}-${Math.random()}`;
  if (!render) {
    return (
      <div key={key} className="sdui-unknown">
        Unknown component: <code>{node.type}</code>
      </div>
    );
  }
  return <Fragment key={key}>{render(node, renderNode)}</Fragment>;
}

export function SchemaRenderer({ schema }: { schema: UiNode | null }) {
  if (!schema) return <div className="sdui-empty">Loading schema…</div>;
  return renderNode(schema);
}
