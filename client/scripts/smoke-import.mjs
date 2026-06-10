// E2E check for ui_import_url's style + structure fidelity. Talks to the server over the same
// Streamable-HTTP MCP transport (/mcp) as loop-test.mjs and the real clients — the server is
// HTTP-only (see Program.cs), there is no stdio MCP mode. Serves a small styled HTML fixture on
// localhost, imports it, and asserts the resulting schema carries CSS (props.style) and preserved
// nesting — not just flattened text. Then rolls back.
import { spawn } from "node:child_process";
import { createServer } from "node:http";

const SERVER = "http://localhost:5179";
const MCP = `${SERVER}/mcp`;
const DLL = "bin/Debug/net8.0/DynamicUi.Server.dll";
const results = [];
const ok = (name, cond, detail = "") => { results.push({ name, pass: !!cond, detail }); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// A deliberately styled, nested page: a <style> block (class-based rules) + inline style, so the
// importer must resolve cascaded CSS — not just read text — to reproduce it.
const FIXTURE = `<!doctype html><html><head><title>Fixture Page</title><style>
  body { margin: 0; font-family: Georgia, serif; }
  .hero { display: flex; flex-direction: column; gap: 16px; padding: 24px; background-color: rgb(11, 15, 26); color: rgb(240, 240, 240); }
  .card { background-color: rgb(30, 41, 59); border-radius: 12px; padding: 18px; }
  .card h2 { color: rgb(108, 222, 196); }
</style></head><body>
  <section class="hero">
    <div class="card">
      <h2>Styled Heading</h2>
      <p>Body copy inside a styled card.</p>
    </div>
    <div class="card" style="background-color: rgb(80, 20, 20);">
      <h2>Second Card</h2>
      <p>Another paragraph.</p>
    </div>
  </section>
</body></html>`;

// Walk helpers over the schema tree.
const maxDepth = (n, d = 1) => {
  const kids = Array.isArray(n?.children) ? n.children : [];
  return kids.length ? Math.max(...kids.map((c) => maxDepth(c, d + 1))) : d;
};
const anyNode = (n, pred) => pred(n) || (Array.isArray(n?.children) && n.children.some((c) => anyNode(c, pred)));
const hasStyle = (n) => n?.props && typeof n.props.style === "object" && n.props.style && Object.keys(n.props.style).length > 0;

// Serve the fixture on an ephemeral localhost port so the import is deterministic and offline.
const fixture = createServer((_req, res) => {
  res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
  res.end(FIXTURE);
});
await new Promise((r) => fixture.listen(0, "127.0.0.1", r));
const fixtureUrl = `http://127.0.0.1:${fixture.address().port}/`;

// Launch the server (web host + MCP at /mcp). If a singleton is already on 5179 this copy exits and
// the test drives the live one; either way 5179 serves.
const srv = spawn("dotnet", [DLL], { cwd: process.cwd(), stdio: ["ignore", "inherit", "inherit"] });

// Minimal Streamable-HTTP MCP client (mirrors loop-test.mjs): POST one JSON-RPC message per request,
// read the single SSE "message" reply, echo the Mcp-Session-Id handed out by initialize.
let sessionId = null;
const parseSse = (text) => {
  for (const line of text.split("\n")) {
    const t = line.trim();
    if (t.startsWith("data:")) { try { return JSON.parse(t.slice(5).trim()); } catch {} }
  }
  return null;
};
let nextId = 1;
const mcp = async (method, params, { notify = false } = {}) => {
  const headers = { "Content-Type": "application/json", "Accept": "application/json, text/event-stream" };
  if (sessionId) headers["Mcp-Session-Id"] = sessionId;
  const payload = { jsonrpc: "2.0", ...(notify ? {} : { id: nextId++ }), method, params };
  const res = await fetch(MCP, { method: "POST", headers, body: JSON.stringify(payload) });
  const sid = res.headers.get("mcp-session-id");
  if (sid) sessionId = sid;
  if (notify) return null;
  return parseSse(await res.text());
};
const unwrap = (res) => { try { return JSON.parse(res.result.content[0].text); } catch { return null; } };
const waitForServer = async () => {
  for (let i = 0; i < 40; i++) {
    try { const r = await fetch(`${SERVER}/api/schema`); if (r.ok) return await r.json(); } catch {}
    await sleep(500);
  }
  throw new Error("server never came up on " + SERVER);
};

try {
  const before = await waitForServer();
  const baseVersion = before.version;

  await mcp("initialize", { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "import-smoke", version: "1.0" } });
  ok("MCP initialize over HTTP returns a session", !!sessionId);
  await mcp("notifications/initialized", {}, { notify: true });

  const imp = unwrap(await mcp("tools/call", { name: "ui_import_url", arguments: { url: fixtureUrl } }));
  ok("import returns ok", imp?.ok === true, imp?.error ?? `version=${imp?.version}`);

  const schema = imp?.schema;
  ok("imported title preserved", JSON.stringify(schema ?? {}).includes("Fixture Page"));
  ok("imported text preserved", anyNode(schema, (n) => n?.props?.text === "Styled Heading"));

  // The core fix: at least one node must carry resolved CSS, and the styled cards must keep nesting.
  ok("at least one node carries a style object", anyNode(schema, hasStyle));
  ok("background color resolved from CSS", JSON.stringify(schema ?? {}).includes("backgroundColor"));
  ok("structure preserved (nesting depth > 2)", maxDepth(schema) > 2, `depth=${maxDepth(schema)}`);

  const bad = unwrap(await mcp("tools/call", { name: "ui_import_url", arguments: { url: "not-a-url" } }));
  ok("bad url rejected", bad?.ok === false, bad?.error);

  const back = unwrap(await mcp("tools/call", { name: "ui_rollback", arguments: { version: baseVersion } }));
  ok("rollback ok", back?.ok === true, `version=${back?.version}`);
} catch (e) {
  ok("smoke harness completed without throwing", false, String(e));
} finally {
  try { srv.kill(); } catch {}
  try { fixture.close(); } catch {}
}

console.log("\n===== IMPORT SMOKE RESULTS =====");
let allPass = true;
for (const r of results) {
  allPass &&= r.pass;
  console.log(`${r.pass ? "PASS" : "FAIL"}  ${r.name}${r.detail ? "  — " + r.detail : ""}`);
}
console.log(allPass ? "\nALL PASS ✅" : "\nSOME FAILED ❌");
setTimeout(() => process.exit(allPass ? 0 : 1), 300);
