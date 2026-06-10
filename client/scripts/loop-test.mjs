// Integration check for the full live-edit loop (no Claude Desktop needed):
//   MCP tools/call (raw JSON-RPC over stdin) -> SchemaStore.ApplyPatchAsync (JsonPatch.Net)
//   -> persist -> SignalR broadcast -> connected client receives "SchemaChanged".
// Also checks that an invalid patch is rejected and does NOT bump the version.
import { spawn } from "node:child_process";
import { HubConnectionBuilder } from "@microsoft/signalr";

const SERVER = "http://localhost:5179";
const DLL = "bin/Debug/net8.0/DynamicUi.Server.dll";
const results = [];
const ok = (name, cond, detail = "") => { results.push({ name, pass: !!cond, detail }); };

// 1. Launch server in --mcp mode (also hosts Kestrel/SignalR). cwd=server so it reuses the seeded db.
const srv = spawn("dotnet", [DLL, "--mcp"], { cwd: process.cwd(), stdio: ["pipe", "pipe", "inherit"] });

// JSON-RPC over stdio is newline-delimited.
let buf = "";
const pending = new Map();
srv.stdout.on("data", (chunk) => {
  buf += chunk.toString();
  let nl;
  while ((nl = buf.indexOf("\n")) >= 0) {
    const line = buf.slice(0, nl).trim();
    buf = buf.slice(nl + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    if (msg.id != null && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  }
});
const rpc = (id, method, params) =>
  new Promise((resolve, reject) => {
    if (id != null) {
      pending.set(id, resolve);
      setTimeout(() => { if (pending.has(id)) { pending.delete(id); reject(new Error(`timeout: ${method}`)); } }, 15000);
    }
    srv.stdin.write(JSON.stringify({ jsonrpc: "2.0", ...(id != null ? { id } : {}), method, params }) + "\n");
    if (id == null) resolve();
  });

// Unwrap an MCP tools/call result: content[0].text holds our tool's JSON return.
const unwrap = (res) => {
  try { return JSON.parse(res.result.content[0].text); } catch { return null; }
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
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

  // 3. MCP handshake.
  await rpc(1, "initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "loop-test", version: "1.0" },
  });
  await rpc(null, "notifications/initialized", {});

  // 4. Valid edit: rename the welcome heading. (mirrors AC-2 "make the ... say ...")
  const patch = JSON.stringify([{ op: "replace", path: "/children/0/props/text", value: "Hello team" }]);
  const applyRes = await rpc(2, "tools/call", { name: "ui_apply_patch", arguments: { patch } });
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
  const rejRes = await rpc(3, "tools/call", { name: "ui_apply_patch", arguments: { patch: badPatch } });
  const rej = unwrap(rejRes);
  ok("invalid patch rejected (ok:false + reason)", rej?.ok === false && /Unknown component/.test(rej?.error ?? ""), rej?.error?.slice(0, 100));

  const after = await (await fetch(`${SERVER}/api/schema`)).json();
  ok("version bumped exactly once (reject didn't persist)", after.version === before.version + 1, `${before.version} -> ${after.version}`);

  await conn.stop();
} catch (e) {
  ok("test harness completed without throwing", false, String(e));
} finally {
  try { srv.stdout.removeAllListeners(); } catch {}
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
