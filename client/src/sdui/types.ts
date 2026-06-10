// A node in the stored UI schema tree. Mirrors the server's component model.
export interface UiNode {
  id?: string;
  type: string;
  props?: Record<string, unknown>;
  children?: UiNode[];
}

// Payload broadcast by the SignalR hub on every applied change / rollback.
export interface SchemaChanged {
  version: number;
  schema: UiNode;
  patch: unknown;
}

export const SERVER_BASE = "http://localhost:5179";
