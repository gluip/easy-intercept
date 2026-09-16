export interface ProxySession {
  id: string;
  timestamp: string;
  method: string;
  url: string;
  requestHeaders: Record<string, string>;
  requestBody: string;
  responseStatus: number;
  responseHeaders: Record<string, string>;
  responseBody: string;
  durationMs: number;
  /** false while the response body (or an open WebSocket) is still being relayed; absent/true otherwise. */
  responseComplete?: boolean;
  /** ms until the upstream response headers arrived; 0/absent when unknown. */
  timeToFirstByteMs?: number;
  /** Present (possibly empty) for WebSocket sessions only. */
  webSocketMessages?: readonly WebSocketMessage[];
}

export interface WebSocketMessage {
  /** "out" = client → server, "in" = server → client */
  direction: "in" | "out";
  /** ms since the session started */
  offsetMs: number;
  type: "text" | "binary" | "close" | "note";
  data: string;
}

export interface AutoResponderRule {
  id: string;
  name: string;
  isEnabled: boolean;
  method: string;
  url: string;
  responseStatus: number;
  responseHeaders: Record<string, string>;
  responseBody: string;
  latencyMs: number;
  bodyMatchType: "none" | "contains" | "regex";
  bodyMatch: string;
}
