// Integration check for the full live-edit loop (no Claude client needed):
//   MCP tools/call (JSON-RPC over the Streamable-HTTP transport at /mcp) -> SchemaStore.ApplyPatchAsync
//   (JsonPatch.Net) -> persist -> SignalR broadcast -> connected client receives "SchemaChanged".
// Also checks that an invalid patch is rejected and does NOT bump the version.
//
// The server is HTTP-transport now (one shared process serves every client over /mcp), so this talks
// to it exactly like Claude Code / Claude Desktop do — POST JSON-RPC, read the SSE reply, echo the
// Mcp-Session-Id header on every subsequent call.
import { spawn } from "node:child_process";
import { HubConnectionBuilder } from "@microsoft/signalr";

const SERVER = "http://localhost:5179";
const MCP = `${SERVER}/mcp`;
const DLL = "bin/Debug/net8.0/DynamicUi.Server.dll";
const results = [];
const ok = (name, cond, detail = "") => { results.push({ name, pass: !!cond, detail }); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// 1. Launch the server (web host + MCP at /mcp + SignalR — all one process). If a singleton is already
//    running on 5179 this copy just exits and the test drives the live one; either way 5179 serves.
const srv = spawn("dotnet", [DLL], { cwd: process.cwd(), stdio: ["ignore", "inherit", "inherit"] });

// Minimal Streamable-HTTP MCP client. Each request POSTs one JSON-RPC message; the reply comes back as
// a single SSE "message" event (notifications get 202 + empty body). The session id handed out by
// initialize must be echoed on every later request via the Mcp-Session-Id header.
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

// Unwrap an MCP tools/call result: content[0].text holds our tool's JSON return.
const unwrap = (res) => {
  try { return JSON.parse(res.result.content[0].text); } catch { return null; }
};
const waitForServer = async () => {
  for (let i = 0; i < 40; i++) {
    try { const r = await fetch(`${SERVER}/api/schema`); if (r.ok) return await r.json(); } catch {}
    await sleep(500);
  }
  throw new Error("server never came up on " + SERVER);
};

try {
  const before = await waitForServer();

  // 2. Connect a real SignalR client and capture broadcasts.
  const received = [];
  const conn = new HubConnectionBuilder().withUrl(`${SERVER}/hub/ui`).build();
  conn.on("SchemaChanged", (m) => received.push(m));
  await conn.start();

  // 3. MCP handshake over HTTP.
  const init = await mcp("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "loop-test", version: "1.0" },
  });
  ok("MCP initialize over HTTP returns a session", !!sessionId && !!init?.result, `session=${sessionId ? "yes" : "no"}`);
  await mcp("notifications/initialized", {}, { notify: true });

  // 4. Valid edit: rename the title-bar heading. (mirrors AC-2 "make the ... say ...")
  const patch = JSON.stringify([{ op: "replace", path: "/children/0/props/title", value: "Hello team" }]);
  const applyRes = await mcp("tools/call", { name: "ui_apply_patch", arguments: { patch } });
  const apply = unwrap(applyRes);
  const applyOk = apply?.ok === true && JSON.stringify(apply.schema).includes("Hello team");
  ok("ui_apply_patch returns ok+new schema", applyOk, `ok=${apply?.ok} version=${apply?.version}`);

  // 5. Broadcast must reach the connected client with the new version + schema.
  for (let i = 0; i < 20 && received.length === 0; i++) await sleep(100);
  const evt = received[0];
  ok("SignalR broadcast received by client", !!evt, evt ? `version=${evt.version}` : "no event");
  ok("broadcast carries the new version", evt && evt.version === before.version + 1, evt ? `${before.version} -> ${evt.version}` : "");
  ok("broadcast schema reflects the edit", evt && JSON.stringify(evt.schema).includes("Hello team"));

  // 6. Invalid patch (unknown component type) must be rejected and NOT bump the version.
  const badPatch = JSON.stringify([{ op: "add", path: "/children/-", value: { id: "x", type: "Nonsense", props: {} } }]);
  const rejRes = await mcp("tools/call", { name: "ui_apply_patch", arguments: { patch: badPatch } });
  const rej = unwrap(rejRes);
  ok("invalid patch rejected (ok:false + reason)", rej?.ok === false && /Unknown component/.test(rej?.error ?? ""), rej?.error?.slice(0, 100));

  const after = await (await fetch(`${SERVER}/api/schema`)).json();
  ok("version bumped exactly once (reject didn't persist)", after.version === before.version + 1, `${before.version} -> ${after.version}`);

  await conn.stop();
} catch (e) {
  ok("test harness completed without throwing", false, String(e));
} finally {
  try { srv.kill(); } catch {}
}

console.log("\n===== LOOP TEST RESULTS =====");
let allPass = true;
for (const r of results) {
  allPass &&= r.pass;
  console.log(`${r.pass ? "PASS" : "FAIL"}  ${r.name}${r.detail ? "  — " + r.detail : ""}`);
}
console.log(allPass ? "\nALL PASS ✅" : "\nSOME FAILED ❌");
// Delay exit so libuv can finish closing the child/socket handles (avoids a Windows teardown assert).
setTimeout(() => process.exit(allPass ? 0 : 1), 300);
