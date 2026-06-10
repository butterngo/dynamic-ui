import { SchemaRenderer } from "./sdui/SchemaRenderer";
import { useSchema } from "./sdui/useSchema";
import "./App.css";

export default function App() {
  const { schema, version, connected } = useSchema();

  return (
    <div className="app">
      <header className="app-bar">
        <span className="app-title">Dynamic UI</span>
        <span className={`app-status ${connected ? "live" : "off"}`}>
          {connected ? "● live" : "○ disconnected"} · v{version}
        </span>
      </header>
      <main className="app-canvas">
        <SchemaRenderer schema={schema} />
      </main>
      <footer className="app-foot">
        Rendered entirely from the stored JSON schema. Edit it from Claude Desktop via the
        <code> ui_* </code> MCP tools — changes appear here live.
      </footer>
    </div>
  );
}
