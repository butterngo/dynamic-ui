import { useEffect, useState } from "react";
import { HubConnectionBuilder } from "@microsoft/signalr";
import { SERVER_BASE, type SchemaChanged, type UiNode } from "./types";

// Bootstraps the schema over HTTP, then keeps it live via SignalR. On reconnect it re-fetches
// the full schema (AC-7: full re-fetch, no per-client patch replay) so a client that missed
// broadcasts converges to the current version with no stale or partial state.
export function useSchema() {
  const [schema, setSchema] = useState<UiNode | null>(null);
  const [version, setVersion] = useState(0);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    let disposed = false;

    async function fetchSchema() {
      const res = await fetch(`${SERVER_BASE}/api/schema`);
      const data = (await res.json()) as { version: number; schema: UiNode };
      if (disposed) return;
      setSchema(data.schema);
      setVersion(data.version);
    }

    const conn = new HubConnectionBuilder()
      .withUrl(`${SERVER_BASE}/hub/ui`)
      .withAutomaticReconnect()
      .build();

    conn.on("SchemaChanged", (msg: SchemaChanged) => {
      setSchema(msg.schema);
      setVersion(msg.version);
    });
    conn.onreconnecting(() => setConnected(false));
    conn.onreconnected(() => {
      setConnected(true);
      void fetchSchema(); // catch up on anything missed while disconnected
    });
    conn.onclose(() => setConnected(false));

    void fetchSchema();
    conn
      .start()
      .then(() => !disposed && setConnected(true))
      .catch((err) => console.error("SignalR connect failed:", err));

    return () => {
      disposed = true;
      void conn.stop();
    };
  }, []);

  return { schema, version, connected };
}
